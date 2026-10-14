using System.Data;
using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.DemoData;
using WorkUp.Data;

namespace WorkUp.Data.DemoData;

public sealed record DemoSeedOutcome(int CreatedBusinesses, int AlreadySeededBusinesses);

public sealed class DemoSeedConflictException(string message) : InvalidOperationException(message);

public sealed class DemoSeedSafetyException(string message) : InvalidOperationException(message);

/// <summary>Explicit, transactional ADO.NET seed. It never updates an existing row.</summary>
public sealed class DemoDataSeeder(string connectionString, IPasswordHasher passwordHasher, IClock clock)
{
    public async Task<DemoSeedOutcome> SeedAsync(string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("A local demo password is required.", nameof(password));
        if (!DemoSeedDatabaseTarget.Evaluate(connectionString).IsAllowed)
            throw new DemoSeedSafetyException("Demo seeding requires the default LocalDB instance and a dedicated WorkUpDemo* or WorkUpT07Demo* database.");

        var plan = DemoSeedPlan.Create();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        try
        {
            var existingBusinesses = new Dictionary<string, int?>(StringComparer.Ordinal);
            foreach (var business in plan)
            {
                var existingId = await FindBusinessIdAsync(connection, transaction, business.Slug, ct);
                if (existingId.HasValue)
                {
                    await VerifyExistingBusinessAsync(connection, transaction, existingId.Value, business, password, ct);
                }
                else if (await OwnerUsernameExistsAsync(connection, transaction, business.OwnerUsername, ct))
                {
                    throw new DemoSeedConflictException(
                        "A demo owner username is already assigned to another business; no existing data was changed.");
                }

                existingBusinesses.Add(business.Slug, existingId);
            }

            var created = 0;
            foreach (var business in plan)
            {
                if (existingBusinesses[business.Slug].HasValue)
                    continue;

                await InsertBusinessAsync(connection, transaction, business, password, ct);
                created++;
            }

            await transaction.CommitAsync(ct);
            return new DemoSeedOutcome(created, plan.Count - created);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task VerifyExistingBusinessAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int businessId,
        DemoBusinessSeed expected,
        string password,
        CancellationToken ct)
    {
        await using (var command = Command(connection, transaction,
            """
            SELECT Name, Slug, ContactPhone, NotificationEmail, AllowsWalkIns, ReminderLeadHours,
                   DailySummaryTime, StockReviewEveryDays
              FROM Businesses WITH (UPDLOCK, HOLDLOCK)
             WHERE Id = @businessId
            """,
            ("@businessId", businessId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)
                || reader.GetString(0) != expected.Name
                || reader.GetString(1) != expected.Slug
                || reader.GetString(2) != expected.ContactPhone
                || reader.IsDBNull(3)
                || reader.GetString(3) != expected.NotificationEmail
                || !reader.GetBoolean(4)
                || reader.GetInt32(5) != 24
                || reader.GetFieldValue<TimeOnly>(6) != new TimeOnly(21, 0)
                || reader.GetInt32(7) != 7)
                throw Conflict(expected);
        }

        await VerifyOwnerAsync(connection, transaction, businessId, expected, password, ct);
        var workers = await ReadWorkersAsync(connection, transaction, businessId, ct);
        if (workers.Count != expected.Workers.Count)
            throw Conflict(expected);

        var workersByCuil = workers.ToDictionary(worker => worker.Cuil, StringComparer.Ordinal);
        foreach (var worker in expected.Workers)
        {
            if (!workersByCuil.TryGetValue(worker.Cuil, out var actual)
                || actual.FirstName != worker.FirstName
                || actual.LastName != worker.LastName
                || actual.Phone != worker.Phone
                || !actual.IsActive)
                throw Conflict(expected);
        }

        await VerifySchedulesAsync(connection, transaction, businessId, expected, ct);
        await VerifyNoBreaksAsync(connection, transaction, businessId, expected, ct);
        await VerifyServicesAsync(connection, transaction, businessId, expected, ct);
        await VerifyGridClassesAsync(connection, transaction, businessId, expected, ct);
    }

    private async Task VerifyOwnerAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int businessId,
        DemoBusinessSeed expected,
        string password,
        CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "SELECT Username, PasswordHash FROM OwnerAccounts WITH (UPDLOCK, HOLDLOCK) WHERE BusinessId = @businessId",
            ("@businessId", businessId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw Conflict(expected);

        var username = reader.GetString(0);
        var hash = reader.GetString(1);
        if (await reader.ReadAsync(ct)
            || username != expected.OwnerUsername
            || !VerifyPassword(hash, password))
            throw Conflict(expected);
    }

    private async Task<List<WorkerSnapshot>> ReadWorkersAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int businessId,
        CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            """
            SELECT Id, FirstName, LastName, Phone, Cuil, IsActive
              FROM Workers WITH (UPDLOCK, HOLDLOCK)
             WHERE BusinessId = @businessId
            """,
            ("@businessId", businessId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<WorkerSnapshot>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new WorkerSnapshot(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetBoolean(5)));
        }

        return result;
    }

    private async Task VerifySchedulesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int businessId,
        DemoBusinessSeed expected,
        CancellationToken ct)
    {
        var actual = new List<ScheduleSnapshot>();
        await using (var command = Command(connection, transaction,
            """
            SELECT w.Cuil, ws.DayOfWeek, ws.StartTime, ws.EndTime
              FROM WorkerSchedules ws WITH (UPDLOCK, HOLDLOCK)
              JOIN Workers w ON w.Id = ws.WorkerId
             WHERE w.BusinessId = @businessId
            """,
            ("@businessId", businessId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                actual.Add(new ScheduleSnapshot(
                    reader.GetString(0), (DayOfWeek)reader.GetByte(1),
                    reader.GetFieldValue<TimeOnly>(2), reader.GetFieldValue<TimeOnly>(3)));
            }
        }

        var expectedRows = expected.Workers
            .SelectMany(worker => worker.Schedules.Select(schedule =>
                new ScheduleSnapshot(worker.Cuil, schedule.DayOfWeek, schedule.StartTime, schedule.EndTime)))
            .ToArray();
        if (!DemoSeedMultiset.HasSameRows(actual, expectedRows))
            throw Conflict(expected);
    }

    private static async Task VerifyNoBreaksAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int businessId,
        DemoBusinessSeed expected,
        CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            """
            SELECT COUNT(*)
              FROM WorkerBreaks wb WITH (UPDLOCK, HOLDLOCK)
              JOIN Workers w ON w.Id = wb.WorkerId
             WHERE w.BusinessId = @businessId
            """,
            ("@businessId", businessId));
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 0)
            throw Conflict(expected);
    }

    private static async Task VerifyServicesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int businessId,
        DemoBusinessSeed expected,
        CancellationToken ct)
    {
        var actual = new List<ServiceSnapshot>();
        await using (var command = Command(connection, transaction,
            """
            SELECT Name, Price, DurationMinutes, MaxCapacity, IsActive
              FROM Services WITH (UPDLOCK, HOLDLOCK)
             WHERE BusinessId = @businessId
            """,
            ("@businessId", businessId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                actual.Add(new ServiceSnapshot(
                    reader.GetString(0), reader.GetDecimal(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetBoolean(4)));
            }
        }

        var expectedRows = expected.Services
            .Select(service => new ServiceSnapshot(service.Name, service.Price, service.DurationMinutes, service.MaxCapacity, true))
            .ToArray();
        if (!DemoSeedMultiset.HasSameRows(actual, expectedRows))
            throw Conflict(expected);
    }

    private static async Task VerifyGridClassesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int businessId,
        DemoBusinessSeed expected,
        CancellationToken ct)
    {
        var actual = new List<GridClassSnapshot>();
        await using (var command = Command(connection, transaction,
            """
            SELECT s.BusinessId, s.Name, w.BusinessId, w.Cuil, g.DayOfWeek, g.StartTime, g.IsActive
              FROM GridClasses g WITH (UPDLOCK, HOLDLOCK)
              LEFT JOIN Services s ON s.Id = g.ServiceId
              LEFT JOIN Workers w ON w.Id = g.WorkerId
             WHERE g.BusinessId = @businessId
            """,
            ("@businessId", businessId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                actual.Add(new GridClassSnapshot(
                    reader.IsDBNull(0) ? null : reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    (DayOfWeek)reader.GetByte(4), reader.GetFieldValue<TimeOnly>(5), reader.GetBoolean(6)));
            }
        }

        var serviceByKey = expected.Services.ToDictionary(service => service.Key, StringComparer.Ordinal);
        var workerByKey = expected.Workers.ToDictionary(worker => worker.Key, StringComparer.Ordinal);
        var expectedRows = expected.GridClasses
            .Select(gridClass => new GridClassSnapshot(
                businessId,
                serviceByKey[gridClass.ServiceKey].Name,
                businessId,
                workerByKey[gridClass.WorkerKey].Cuil,
                gridClass.DayOfWeek,
                gridClass.StartTime,
                true))
            .ToArray();
        if (!DemoSeedMultiset.HasSameRows(actual, expectedRows))
            throw Conflict(expected);
    }

    private async Task InsertBusinessAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DemoBusinessSeed business,
        string password,
        CancellationToken ct)
    {
        int businessId;
        await using (var command = Command(connection, transaction,
            """
            INSERT INTO Businesses
                (Name, Slug, ContactPhone, NotificationEmail, AllowsWalkIns, ReminderLeadHours,
                 DailySummaryTime, StockReviewEveryDays, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@name, @slug, @contactPhone, @notificationEmail, 1, 24, @dailySummaryTime, 7, @createdAt)
            """,
            ("@name", business.Name), ("@slug", business.Slug), ("@contactPhone", business.ContactPhone),
            ("@notificationEmail", business.NotificationEmail), ("@dailySummaryTime", new TimeOnly(21, 0)), ("@createdAt", clock.Now)))
        {
            businessId = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        }

        await using (var command = Command(connection, transaction,
            """
            INSERT INTO OwnerAccounts (BusinessId, Username, PasswordHash, CreatedAt)
            VALUES (@businessId, @username, @passwordHash, @createdAt)
            """,
            ("@businessId", businessId), ("@username", business.OwnerUsername),
            ("@passwordHash", passwordHasher.Hash(password)), ("@createdAt", clock.Now)))
        {
            await command.ExecuteNonQueryAsync(ct);
        }

        var serviceIds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var service in business.Services)
        {
            await using var command = Command(connection, transaction,
                """
                INSERT INTO Services (BusinessId, Name, Price, DurationMinutes, MaxCapacity, IsActive)
                OUTPUT INSERTED.Id
                VALUES (@businessId, @name, @price, @durationMinutes, @maxCapacity, 1)
                """,
                ("@businessId", businessId), ("@name", service.Name), ("@price", service.Price),
                ("@durationMinutes", service.DurationMinutes), ("@maxCapacity", service.MaxCapacity));
            serviceIds.Add(service.Key, Convert.ToInt32(await command.ExecuteScalarAsync(ct)));
        }

        var workerIds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var worker in business.Workers)
        {
            int workerId;
            await using (var command = Command(connection, transaction,
                """
                INSERT INTO Workers (BusinessId, FirstName, LastName, Phone, Cuil, IsActive)
                OUTPUT INSERTED.Id
                VALUES (@businessId, @firstName, @lastName, @phone, @cuil, 1)
                """,
                ("@businessId", businessId), ("@firstName", worker.FirstName), ("@lastName", worker.LastName),
                ("@phone", worker.Phone), ("@cuil", worker.Cuil)))
            {
                workerId = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            }

            workerIds.Add(worker.Key, workerId);
            foreach (var schedule in worker.Schedules)
            {
                await using var command = Command(connection, transaction,
                    "INSERT INTO WorkerSchedules (WorkerId, DayOfWeek, StartTime, EndTime) VALUES (@workerId, @day, @start, @end)",
                    ("@workerId", workerId), ("@day", schedule.DayOfWeek),
                    ("@start", schedule.StartTime), ("@end", schedule.EndTime));
                await command.ExecuteNonQueryAsync(ct);
            }
        }

        foreach (var gridClass in business.GridClasses)
        {
            await using var command = Command(connection, transaction,
                """
                INSERT INTO GridClasses (BusinessId, ServiceId, WorkerId, DayOfWeek, StartTime, IsActive)
                VALUES (@businessId, @serviceId, @workerId, @day, @start, 1)
                """,
                ("@businessId", businessId), ("@serviceId", serviceIds[gridClass.ServiceKey]),
                ("@workerId", workerIds[gridClass.WorkerKey]), ("@day", gridClass.DayOfWeek),
                ("@start", gridClass.StartTime));
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<int?> FindBusinessIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string slug,
        CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "SELECT Id FROM Businesses WITH (UPDLOCK, HOLDLOCK) WHERE Slug = @slug",
            ("@slug", slug));
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    private static async Task<bool> OwnerUsernameExistsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string username,
        CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "SELECT COUNT(*) FROM OwnerAccounts WITH (UPDLOCK, HOLDLOCK) WHERE Username = @username",
            ("@username", username));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 0;
    }

    private bool VerifyPassword(string hash, string password)
    {
        try
        {
            return passwordHasher.Verify(hash, password);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static DemoSeedConflictException Conflict(DemoBusinessSeed business) => new(
        $"Demo seed stopped because '{business.Slug}' already exists with non-matching or partial data; no existing data was changed.");

    private static SqlCommand Command(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = new SqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
            command.Parameters.AddValue(name, value);
        return command;
    }

    private sealed record WorkerSnapshot(int Id, string FirstName, string LastName, string Phone, string Cuil, bool IsActive);
    private sealed record ScheduleSnapshot(string Cuil, DayOfWeek Day, TimeOnly Start, TimeOnly End);
    private sealed record ServiceSnapshot(string Name, decimal Price, int DurationMinutes, int MaxCapacity, bool IsActive);
    private sealed record GridClassSnapshot(
        int? ServiceBusinessId, string? ServiceName, int? WorkerBusinessId, string? WorkerCuil,
        DayOfWeek Day, TimeOnly Start, bool IsActive);
}
