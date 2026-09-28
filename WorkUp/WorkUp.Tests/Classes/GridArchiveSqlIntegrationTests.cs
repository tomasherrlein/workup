using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Staff;
using WorkUp.Data;
using WorkUp.Data.Businesses;
using WorkUp.Data.Catalog;
using WorkUp.Data.Classes;
using WorkUp.Data.Staff;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Rules;
using WorkUp.Tests.Fakes;
using Xunit.Abstractions;

namespace WorkUp.Tests.Classes;

/// <summary>
/// Opt-in, retained synthetic LocalDB fixture for the archive guard of T27 (hueco de T04, RF-02/RF-03/RF-04).
/// <para>
/// The first three tests map the guard onto real storage: a service or a worker still referenced by an
/// <c>IsActive = 1</c> row of <c>GridClasses</c> cannot be archived, an archived grid class really stops blocking,
/// the guard is scoped to the business even though <c>GridClasses</c> only constrains <c>ServiceId</c>/<c>WorkerId</c>
/// by foreign key, and an archived grid class only comes back once its class type and its teacher are active again.
/// </para>
/// <para>
/// The last three tests prove the lock, not the read: they hold a real transaction open in one session and drive a
/// service archive, a worker archive and a class reactivation through a second session, so the loser still meets the
/// winner's committed row. That only holds because the writer takes the parent row's update lock before its guard.
/// They are deterministic by construction: the second session signals when it has entered its lock call, and the first
/// session commits only after that signal, so no sleep and no retry decides the interleaving. They prove these
/// two-session orderings and do not claim a universal absence of deadlocks, which no finite fixture can establish.
/// </para>
/// Requires a new, dedicated database supplied through WORKUP_T27_GRID_ARCHIVE_SQL_CONNECTION; without it every test
/// returns N/A and no SQL connection is opened. Each test derives its own database name from the supplied one, so the
/// whole class runs in a single command, and nothing is torn down: the target is retained on purpose.
/// </summary>
public sealed class GridArchiveSqlIntegrationTests(ITestOutputHelper output)
{
    private const string VariableName = "WORKUP_T27_GRID_ARCHIVE_SQL_CONNECTION";
    private const string Server = @"(localdb)\MSSQLLocalDB";

    /// <summary>The single grid class slot every fixture uses: Tuesday 18:00, inside the seeded teacher's schedule.</summary>
    private static readonly DayOfWeek ClassDay = DayOfWeek.Tuesday;

    private static readonly TimeOnly ClassStart = new(18, 0);

    [Fact]
    public async Task An_active_grid_class_blocks_archiving_its_service_and_its_worker_without_flipping_IsActive()
    {
        var target = await ResolveTargetAsync("_BlocksArchive");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");
        var serviceId = await SeedClassServiceAsync(target.ConnectionString, businessId, "Yoga");
        var workerId = await SeedWorkerAsync(target.ConnectionString, businessId, "20304050607");
        var gridClassId = await SeedGridClassAsync(target.ConnectionString, businessId, serviceId, workerId);

        await using var session = new DbSession(target.ConnectionString);
        var services = new SqlServiceRepository(session);
        var workers = new SqlWorkerRepository(session);
        var gridClasses = new SqlGridClassRepository(session);

        // Both archives are rejected by the real stored row, and neither flips the stored IsActive: a rejection that
        // wrote anyway would leave the class unusable, which is exactly what the rule prevents.
        var serviceArchive = await new CatalogService(services, session).SetActiveAsync(businessId, serviceId, isActive: false);
        Assert.False(serviceArchive.Succeeded);
        Assert.Equal("service_has_active_grid_class", serviceArchive.Error!.Code);
        Assert.True(await IsServiceActiveAsync(target.ConnectionString, businessId, serviceId));

        var workerArchive = await new StaffService(workers, session).SetActiveAsync(businessId, workerId, isActive: false);
        Assert.False(workerArchive.Succeeded);
        Assert.Equal("worker_has_active_grid_class", workerArchive.Error!.Code);
        Assert.True(await IsWorkerActiveAsync(target.ConnectionString, businessId, workerId));

        // Archiving the class itself is the way out, and only then do both resources archive.
        var classArchive = await new ClassesService(gridClasses, services, workers, session, new FakeClock())
            .SetActiveAsync(businessId, gridClassId, isActive: false);
        Assert.True(classArchive.Succeeded, classArchive.Error?.Code);
        Assert.False(await IsGridClassActiveAsync(target.ConnectionString, gridClassId));

        Assert.True((await new CatalogService(services, session).SetActiveAsync(businessId, serviceId, isActive: false)).Succeeded);
        Assert.False(await IsServiceActiveAsync(target.ConnectionString, businessId, serviceId));
        Assert.True((await new StaffService(workers, session).SetActiveAsync(businessId, workerId, isActive: false)).Succeeded);
        Assert.False(await IsWorkerActiveAsync(target.ConnectionString, businessId, workerId));

        output.WriteLine($"Target: {target.DatabaseName}; the active grid class blocked both archives and the stored flags stayed 1; retained without teardown.");
    }

    [Fact]
    public async Task An_archived_grid_class_only_reactivates_once_its_service_and_its_worker_are_active_again()
    {
        var target = await ResolveTargetAsync("_Reactivation");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");
        var serviceId = await SeedClassServiceAsync(target.ConnectionString, businessId, "Yoga");
        var workerId = await SeedWorkerAsync(target.ConnectionString, businessId, "20304050607");
        var gridClassId = await SeedGridClassAsync(target.ConnectionString, businessId, serviceId, workerId);

        await using var session = new DbSession(target.ConnectionString);
        var services = new SqlServiceRepository(session);
        var workers = new SqlWorkerRepository(session);
        var gridClasses = new SqlGridClassRepository(session);
        var classes = new ClassesService(gridClasses, services, workers, session, new FakeClock());

        // The archived class stops blocking, so both resources archive for real.
        Assert.True((await classes.SetActiveAsync(businessId, gridClassId, isActive: false)).Succeeded);
        Assert.True((await new CatalogService(services, session).SetActiveAsync(businessId, serviceId, isActive: false)).Succeeded);
        Assert.True((await new StaffService(workers, session).SetActiveAsync(businessId, workerId, isActive: false)).Succeeded);

        // Reactivation meets the stored flags: the archived class type is reported first, then the archived teacher,
        // and neither rejection flips the stored IsActive.
        var withoutService = await classes.SetActiveAsync(businessId, gridClassId, isActive: true);
        Assert.False(withoutService.Succeeded);
        Assert.Equal("grid_class_invalid_service", withoutService.Error!.Code);
        Assert.False(await IsGridClassActiveAsync(target.ConnectionString, gridClassId));

        Assert.True((await new CatalogService(services, session).SetActiveAsync(businessId, serviceId, isActive: true)).Succeeded);

        var withoutWorker = await classes.SetActiveAsync(businessId, gridClassId, isActive: true);
        Assert.False(withoutWorker.Succeeded);
        Assert.Equal("grid_class_invalid_worker", withoutWorker.Error!.Code);
        Assert.False(await IsGridClassActiveAsync(target.ConnectionString, gridClassId));

        Assert.True((await new StaffService(workers, session).SetActiveAsync(businessId, workerId, isActive: true)).Succeeded);

        // With both resources active the class comes back, and the real row says so.
        var reactivated = await classes.SetActiveAsync(businessId, gridClassId, isActive: true);
        Assert.True(reactivated.Succeeded, reactivated.Error?.Code);
        Assert.True(await IsGridClassActiveAsync(target.ConnectionString, gridClassId));

        output.WriteLine($"Target: {target.DatabaseName}; reactivation met the stored resource flags and only then flipped IsActive; retained without teardown.");
    }

    /// <summary>
    /// <c>GridClasses</c> only constrains <c>ServiceId</c> and <c>WorkerId</c> by foreign key, so a row can carry
    /// another tenant's resources. The guard reads <c>BusinessId</c> too: an active row of another business must not
    /// block this tenant's archive.
    /// </summary>
    [Fact]
    public async Task The_active_grid_class_guard_is_scoped_to_the_business()
    {
        var target = await ResolveTargetAsync("_TenantScope");
        if (target is null)
            return;

        var ownerBusiness = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");
        var otherBusiness = await SeedBusinessAsync(target.ConnectionString, "estudio-lucia");
        Assert.True(ownerBusiness != otherBusiness, "the fixture seeded two distinct tenants");
        var serviceId = await SeedClassServiceAsync(target.ConnectionString, ownerBusiness, "Yoga");
        var workerId = await SeedWorkerAsync(target.ConnectionString, ownerBusiness, "20304050607");
        await SeedGridClassAsync(target.ConnectionString, otherBusiness, serviceId, workerId);

        await using var session = new DbSession(target.ConnectionString);
        var services = new SqlServiceRepository(session);
        var workers = new SqlWorkerRepository(session);

        var serviceArchive = await new CatalogService(services, session).SetActiveAsync(ownerBusiness, serviceId, isActive: false);
        Assert.True(serviceArchive.Succeeded, serviceArchive.Error?.Code);
        Assert.False(await IsServiceActiveAsync(target.ConnectionString, ownerBusiness, serviceId));

        var workerArchive = await new StaffService(workers, session).SetActiveAsync(ownerBusiness, workerId, isActive: false);
        Assert.True(workerArchive.Succeeded, workerArchive.Error?.Code);
        Assert.False(await IsWorkerActiveAsync(target.ConnectionString, ownerBusiness, workerId));

        output.WriteLine($"Target: {target.DatabaseName}; another tenant's active grid class did not block this tenant's archives; retained without teardown.");
    }

    [Fact]
    public async Task A_service_archive_in_another_session_still_meets_a_grid_class_committed_inside_the_lock_window()
    {
        var target = await ResolveTargetAsync("_CreateVsArchiveService");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");
        var serviceId = await SeedClassServiceAsync(target.ConnectionString, businessId, "Yoga");
        var workerId = await SeedWorkerAsync(target.ConnectionString, businessId, "20304050607");

        // Session A is the competing create: it holds the class type's update lock for the whole transaction and stores
        // its active class inside it, exactly as ClassesService.CreateAsync does. It commits only after session B has
        // entered its own lock call, so B's guard can only pass if it reads A's committed class.
        await using var createSession = new DbSession(target.ConnectionString);
        var createServices = new SqlServiceRepository(createSession);
        var createGridClasses = new SqlGridClassRepository(createSession);
        await using var createTransaction = await createSession.BeginAsync();
        await createServices.LockForGridClassWriteAsync(businessId, serviceId);

        // Session B is the archive. Its lock call signals the test before delegating, so the competing create commits
        // only once B is already inside the lock it must wait for; without the lock call B would never signal.
        var reachedLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var archiveTask = Task.Run(async () =>
        {
            await using var archiveSession = new DbSession(target.ConnectionString);
            var archiveServices = new SignallingServiceRepository(new SqlServiceRepository(archiveSession), reachedLock);
            return await new CatalogService(archiveServices, archiveSession)
                .SetActiveAsync(businessId, serviceId, isActive: false);
        });
        await WaitForLockAsync(reachedLock);

        await createGridClasses.InsertAsync(new GridClass
        {
            BusinessId = businessId,
            ServiceId = serviceId,
            WorkerId = workerId,
            DayOfWeek = ClassDay,
            StartTime = ClassStart,
            IsActive = true
        });
        await createTransaction.CommitAsync();

        var result = await archiveTask;
        Assert.False(result.Succeeded);
        Assert.Equal("service_has_active_grid_class", result.Error!.Code);
        Assert.True(await IsServiceActiveAsync(target.ConnectionString, businessId, serviceId));
        output.WriteLine($"Target: {target.DatabaseName}; the archive met the class a competing session committed inside the lock window; retained without teardown.");
    }

    [Fact]
    public async Task A_worker_archive_in_another_session_still_meets_a_grid_class_committed_inside_the_lock_window()
    {
        var target = await ResolveTargetAsync("_CreateVsArchiveWorker");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");
        var serviceId = await SeedClassServiceAsync(target.ConnectionString, businessId, "Yoga");
        var workerId = await SeedWorkerAsync(target.ConnectionString, businessId, "20304050607");

        // Same deterministic create-vs-archive handshake, now on the teacher's row.
        await using var createSession = new DbSession(target.ConnectionString);
        var createWorkers = new SqlWorkerRepository(createSession);
        var createGridClasses = new SqlGridClassRepository(createSession);
        await using var createTransaction = await createSession.BeginAsync();
        await createWorkers.LockForGridClassWriteAsync(businessId, workerId);

        var reachedLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var archiveTask = Task.Run(async () =>
        {
            await using var archiveSession = new DbSession(target.ConnectionString);
            var archiveWorkers = new SignallingWorkerRepository(new SqlWorkerRepository(archiveSession), reachedLock);
            return await new StaffService(archiveWorkers, archiveSession)
                .SetActiveAsync(businessId, workerId, isActive: false);
        });
        await WaitForLockAsync(reachedLock);

        await createGridClasses.InsertAsync(new GridClass
        {
            BusinessId = businessId,
            ServiceId = serviceId,
            WorkerId = workerId,
            DayOfWeek = ClassDay,
            StartTime = ClassStart,
            IsActive = true
        });
        await createTransaction.CommitAsync();

        var result = await archiveTask;
        Assert.False(result.Succeeded);
        Assert.Equal("worker_has_active_grid_class", result.Error!.Code);
        Assert.True(await IsWorkerActiveAsync(target.ConnectionString, businessId, workerId));
        output.WriteLine($"Target: {target.DatabaseName}; the worker archive met the class a competing session committed inside the lock window; retained without teardown.");
    }

    [Fact]
    public async Task A_class_reactivation_in_another_session_still_meets_a_service_archive_committed_inside_the_lock_window()
    {
        var target = await ResolveTargetAsync("_ReactivateVsArchive");
        if (target is null)
            return;

        var businessId = await SeedBusinessAsync(target.ConnectionString, "peluqueria-de-marina");
        var serviceId = await SeedClassServiceAsync(target.ConnectionString, businessId, "Yoga");
        var workerId = await SeedWorkerAsync(target.ConnectionString, businessId, "20304050607");
        var gridClassId = await SeedGridClassAsync(target.ConnectionString, businessId, serviceId, workerId);

        // The class is archived, so it is a reactivation candidate while its class type is still active.
        await using (var setupSession = new DbSession(target.ConnectionString))
        {
            var setupClasses = new SqlGridClassRepository(setupSession);
            var archived = (await setupClasses.GetByIdAsync(businessId, gridClassId))!;
            archived.IsActive = false;
            await setupClasses.UpdateAsync(archived);
        }

        // Session A is the winning service archive: it holds the class type's update lock for its whole transaction and
        // commits only after session B's reactivation has entered its own lock call.
        await using var archiveSession = new DbSession(target.ConnectionString);
        var archiveServices = new SqlServiceRepository(archiveSession);
        await using var archiveTransaction = await archiveSession.BeginAsync();
        await archiveServices.LockForGridClassWriteAsync(businessId, serviceId);

        var reachedLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reactivateTask = Task.Run(async () =>
        {
            await using var reactivateSession = new DbSession(target.ConnectionString);
            var services = new SignallingServiceRepository(new SqlServiceRepository(reactivateSession), reachedLock);
            var workers = new SqlWorkerRepository(reactivateSession);
            var gridClasses = new SqlGridClassRepository(reactivateSession);
            return await new ClassesService(gridClasses, services, workers, reactivateSession, new FakeClock())
                .SetActiveAsync(businessId, gridClassId, isActive: true);
        });
        await WaitForLockAsync(reachedLock);

        var service = (await archiveServices.GetByIdAsync(businessId, serviceId))!;
        service.IsActive = false;
        await archiveServices.UpdateAsync(service);
        await archiveTransaction.CommitAsync();

        var result = await reactivateTask;
        Assert.False(result.Succeeded);
        Assert.Equal("grid_class_invalid_service", result.Error!.Code);
        Assert.False(await IsGridClassActiveAsync(target.ConnectionString, gridClassId));
        output.WriteLine($"Target: {target.DatabaseName}; the reactivation met the class type a competing archive committed inside the lock window; retained without teardown.");
    }

    /// <summary>
    /// Waits until the second session is inside its parent-row lock call. The second session cannot finish while the
    /// first holds the lock, so this handshake makes the interleaving deterministic: no sleep and no retry decides it.
    /// The bounded wait only turns a regression that drops the lock call into a clear failure instead of a hung test.
    /// </summary>
    private static async Task WaitForLockAsync(TaskCompletionSource reachedLock)
    {
        var signalled = await Task.WhenAny(reachedLock.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(signalled == reachedLock.Task,
            "the competing operation reached its parent-row lock before this session committed; the lock call never signalled");
    }

    private static async Task<int> SeedBusinessAsync(string connectionString, string slug)
    {
        await using var session = new DbSession(connectionString);
        var inserted = await new SqlBusinessRepository(session).InsertAsync(new Business
        {
            Name = slug,
            Slug = slug,
            ContactPhone = "1122334455",
            CreatedAt = DateTime.UtcNow
        });
        Assert.True(inserted.IsInserted, "the fixture could not seed the business");
        return inserted.BusinessId;
    }

    /// <summary>Seeds an active class-type service (max capacity greater than 1), the only kind a grid class accepts.</summary>
    private static async Task<int> SeedClassServiceAsync(string connectionString, int businessId, string name)
    {
        await using var session = new DbSession(connectionString);
        return await new SqlServiceRepository(session).InsertAsync(new Service
        {
            BusinessId = businessId,
            Name = name,
            Price = 3000m,
            DurationMinutes = 60,
            MaxCapacity = 12,
            IsActive = true
        });
    }

    /// <summary>Seeds an active worker that works the fixture's class day, so the class it receives is describable.</summary>
    private static async Task<int> SeedWorkerAsync(string connectionString, int businessId, string cuil)
    {
        Assert.True(CuilFormat.TryNormalize(cuil, out var canonicalCuil), "the fixture seeds only valid CUILs");
        await using var session = new DbSession(connectionString);
        var repository = new SqlWorkerRepository(session);
        var inserted = await repository.InsertAsync(new Worker
        {
            BusinessId = businessId,
            FirstName = "Sofía",
            LastName = "Díaz",
            Phone = "1122334455",
            Cuil = canonicalCuil,
            IsActive = true
        });
        Assert.True(inserted.IsInserted, "the fixture could not seed the worker");
        await repository.ReplaceSchedulesAsync(inserted.WorkerId, [new WorkerSchedule
        {
            DayOfWeek = ClassDay,
            StartTime = new TimeOnly(17, 0),
            EndTime = new TimeOnly(21, 0)
        }]);
        return inserted.WorkerId;
    }

    /// <summary>
    /// Seeds the active grid class row directly, with the tenant that must own the guard. The tenant is a parameter
    /// because the tenant-scope test stores another business's active row over this business's resources.
    /// </summary>
    private static async Task<int> SeedGridClassAsync(string connectionString, int businessId, int serviceId, int workerId)
    {
        await using var session = new DbSession(connectionString);
        return await new SqlGridClassRepository(session).InsertAsync(new GridClass
        {
            BusinessId = businessId,
            ServiceId = serviceId,
            WorkerId = workerId,
            DayOfWeek = ClassDay,
            StartTime = ClassStart,
            IsActive = true
        });
    }

    private static Task<bool> IsServiceActiveAsync(string connectionString, int businessId, int serviceId) =>
        IsActiveAsync(connectionString,
            "SELECT IsActive FROM Services WHERE BusinessId = @businessId AND Id = @id",
            p => p.AddValue("@businessId", businessId).AddValue("@id", serviceId));

    private static Task<bool> IsWorkerActiveAsync(string connectionString, int businessId, int workerId) =>
        IsActiveAsync(connectionString,
            "SELECT IsActive FROM Workers WHERE BusinessId = @businessId AND Id = @id",
            p => p.AddValue("@businessId", businessId).AddValue("@id", workerId));

    private static Task<bool> IsGridClassActiveAsync(string connectionString, int gridClassId) =>
        IsActiveAsync(connectionString,
            "SELECT IsActive FROM GridClasses WHERE Id = @id",
            p => p.AddValue("@id", gridClassId));

    private static async Task<bool> IsActiveAsync(
        string connectionString, string sql, Action<SqlParameterCollection> parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        parameters(command.Parameters);
        var value = await command.ExecuteScalarAsync();
        Assert.NotNull(value);
        return (bool)value;
    }

    /// <summary>
    /// Resolves this test's opt-in target. <c>null</c> means the variable is absent: the test returns N/A without
    /// opening any connection or touching any server.
    /// </summary>
    private async Task<SqlTarget?> ResolveTargetAsync(string testSuffix)
    {
        var supplied = Environment.GetEnvironmentVariable(VariableName);
        if (string.IsNullOrWhiteSpace(supplied))
        {
            output.WriteLine($"N/A: {VariableName} absent; no SQL connection opened.");
            return null;
        }

        var target = ValidateTarget(supplied, testSuffix);
        var applied = await CreateTargetAsync(target);
        Assert.Equal(3, applied);
        output.WriteLine($"Target: {target.DatabaseName}; DB_ID absent before creation; applied {applied} schema script(s); retained without teardown.");
        return target;
    }

    private static SqlTarget ValidateTarget(string suppliedConnectionString, string testSuffix)
    {
        SqlConnectionStringBuilder supplied;
        try
        {
            supplied = new SqlConnectionStringBuilder(suppliedConnectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"{VariableName} could not be parsed; no SQL connection was opened.");
        }

        // Suffixed so each opt-in test derives its own fresh target from one configured
        // WORKUP_T27_GRID_ARCHIVE_SQL_CONNECTION and the whole class still runs in a single command.
        var databaseName = supplied.InitialCatalog + testSuffix;
        var safeName = databaseName.Length <= 128
            && Regex.IsMatch(databaseName, @"\AWorkUpDemoT27GridArchiveSql[A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);
        if (!string.Equals(supplied.DataSource, Server, StringComparison.OrdinalIgnoreCase)
            || !supplied.IntegratedSecurity
            || !safeName
            || !string.IsNullOrEmpty(supplied.UserID)
            || !string.IsNullOrEmpty(supplied.Password)
            || !string.IsNullOrEmpty(supplied.AttachDBFilename)
            || supplied.UserInstance
            || !string.IsNullOrEmpty(supplied.FailoverPartner))
        {
            throw new InvalidOperationException(
                $"{VariableName} must target (localdb)\\MSSQLLocalDB with Integrated Security and a safe WorkUpDemoT27GridArchiveSql* database; credentials, attached files, and failover targets are rejected. No SQL connection was opened.");
        }

        // Rebuild from only the validated instance and database so no other connection-string option can redirect it
        // to a default database or add credentials.
        var safeConnection = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            ConnectTimeout = 15,
            ApplicationName = "WorkUp T27 grid archive SQL integration test",
            TrustServerCertificate = true
        };
        return new SqlTarget(databaseName, safeConnection.ConnectionString);
    }

    private static async Task<int> CreateTargetAsync(SqlTarget target)
    {
        var startInfo = new ProcessStartInfo("sqlcmd")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "-I", "-S", Server, "-d", "master", "-E", "-b", "-h", "-1", "-W", "-Q",
                     $"SET NOCOUNT ON; SELECT CASE WHEN DB_ID(N'{target.DatabaseName}') IS NULL THEN 0 ELSE 1 END;"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = StartSqlCmd(startInfo);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"master preflight failed: {stderr.Trim()}");
        Assert.Equal("0", stdout.Trim());

        // SQL identifiers cannot be parameters. The strict allowlist above excludes brackets and quotes, so quoting
        // the identifier keeps CREATE DATABASE bound to exactly this target, and a competing creator fails loudly
        // instead of letting this run migrate someone else's database.
        var masterConnectionString = new SqlConnectionStringBuilder(target.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using (var master = new SqlConnection(masterConnectionString))
        {
            await master.OpenAsync();
            await using var create = new SqlCommand($"CREATE DATABASE [{target.DatabaseName}]", master);
            await create.ExecuteNonQueryAsync();
        }

        var scripts = await new DatabaseMigrator(target.ConnectionString).MigrateAsync();
        return scripts.Count;
    }

    /// <summary>
    /// Starts <c>sqlcmd</c>, turning a missing binary into an explicit message instead of an uncaught
    /// <see cref="Win32Exception"/> that hides which tool is required.
    /// </summary>
    private static Process StartSqlCmd(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("sqlcmd preflight unavailable.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "sqlcmd could not be started; install the SQL Server command line tools and make sure sqlcmd is on PATH.",
                exception);
        }
    }

    /// <summary>
    /// Wraps the real service repository and signals <paramref name="reachedLock"/> when a writer reaches its
    /// parent-row lock, before delegating. That signal is how the fixture commits the competing session only once this
    /// one is already inside the lock it must wait for, with no sleep and no retry. Every other call goes to the real
    /// repository; no write is faked.
    /// </summary>
    private sealed class SignallingServiceRepository(IServiceRepository inner, TaskCompletionSource reachedLock) : IServiceRepository
    {
        public Task<List<Service>> ListByBusinessAsync(int businessId, CancellationToken ct = default) =>
            inner.ListByBusinessAsync(businessId, ct);

        public Task<Service?> GetByIdAsync(int businessId, int id, CancellationToken ct = default) =>
            inner.GetByIdAsync(businessId, id, ct);

        public Task<bool> NameExistsAsync(int businessId, string name, int? excludingId = null, CancellationToken ct = default) =>
            inner.NameExistsAsync(businessId, name, excludingId, ct);

        public Task<int> InsertAsync(Service service, CancellationToken ct = default) =>
            inner.InsertAsync(service, ct);

        public Task UpdateAsync(Service service, CancellationToken ct = default) =>
            inner.UpdateAsync(service, ct);

        public Task<bool> HasBookingHistoryAsync(int businessId, int id, CancellationToken ct = default) =>
            inner.HasBookingHistoryAsync(businessId, id, ct);

        public Task<bool> HasActiveGroupServiceAsync(int businessId, CancellationToken ct = default) =>
            inner.HasActiveGroupServiceAsync(businessId, ct);

        public Task<bool> HasActiveGridClassAsync(int businessId, int id, CancellationToken ct = default) =>
            inner.HasActiveGridClassAsync(businessId, id, ct);

        public Task LockForGridClassWriteAsync(int businessId, int id, CancellationToken ct = default)
        {
            reachedLock.TrySetResult();
            return inner.LockForGridClassWriteAsync(businessId, id, ct);
        }
    }

    /// <summary>
    /// Wraps the real worker repository and signals <paramref name="reachedLock"/> when a writer reaches its parent-row
    /// lock, before delegating. Same deterministic handshake as the service wrapper; every other call goes to the real
    /// repository.
    /// </summary>
    private sealed class SignallingWorkerRepository(IWorkerRepository inner, TaskCompletionSource reachedLock) : IWorkerRepository
    {
        public Task<List<Worker>> ListByBusinessAsync(int businessId, CancellationToken ct = default) =>
            inner.ListByBusinessAsync(businessId, ct);

        public Task<Worker?> GetByIdAsync(int businessId, int id, CancellationToken ct = default) =>
            inner.GetByIdAsync(businessId, id, ct);

        public Task<bool> CuilExistsAsync(int businessId, string cuil, int? excludingId = null, CancellationToken ct = default) =>
            inner.CuilExistsAsync(businessId, cuil, excludingId, ct);

        public Task<WorkerInsertResult> InsertAsync(Worker worker, CancellationToken ct = default) =>
            inner.InsertAsync(worker, ct);

        public Task<WorkerUpdateResult> UpdateAsync(Worker worker, CancellationToken ct = default) =>
            inner.UpdateAsync(worker, ct);

        public Task ReplaceSchedulesAsync(int workerId, List<WorkerSchedule> schedules, CancellationToken ct = default) =>
            inner.ReplaceSchedulesAsync(workerId, schedules, ct);

        public Task ReplaceBreaksAsync(int workerId, List<WorkerBreak> breaks, CancellationToken ct = default) =>
            inner.ReplaceBreaksAsync(workerId, breaks, ct);

        public Task<bool> HasActiveGridClassAsync(int businessId, int id, CancellationToken ct = default) =>
            inner.HasActiveGridClassAsync(businessId, id, ct);

        public Task LockForGridClassWriteAsync(int businessId, int id, CancellationToken ct = default)
        {
            reachedLock.TrySetResult();
            return inner.LockForGridClassWriteAsync(businessId, id, ct);
        }
    }

    private sealed record SqlTarget(string DatabaseName, string ConnectionString);
}
