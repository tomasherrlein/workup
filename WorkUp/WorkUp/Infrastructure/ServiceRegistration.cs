using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Clients;
using WorkUp.BusinessLogic.ClientAccounts;
using WorkUp.BusinessLogic.Enrollments;
using WorkUp.BusinessLogic.Payments;
using WorkUp.BusinessLogic.Reports;
using WorkUp.BusinessLogic.Staff;
using WorkUp.BusinessLogic.Expenses;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.BusinessLogic.Supplies;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Data;
using WorkUp.Data.Businesses;
using WorkUp.Data.Catalog;
using WorkUp.Data.Classes;
using WorkUp.Data.Clients;
using WorkUp.Data.ClientAccounts;
using WorkUp.Data.Enrollments;
using WorkUp.Data.DemoData;
using WorkUp.Data.Payments;
using WorkUp.Data.Reports;
using WorkUp.Data.Staff;
using WorkUp.Data.Expenses;
using WorkUp.Data.Notifications;
using WorkUp.Data.Supplies;
using WorkUp.Data.Scheduling;
using WorkUp.Infrastructure.Jobs;
using WorkUp.Infrastructure.Notifications;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace WorkUp.Infrastructure;

/// <summary>
/// Composition root. Every task registers its repositories and services here, one line each, grouped by module.
/// Repositories and services are scoped (they share the request DbSession).
/// </summary>
public static class ServiceRegistration
{
    /// <summary>
    /// Development-only opt-in that moves the DataProtection keyring to a disposable local directory, so
    /// synthetic end-to-end verification never reads or writes the shared default keyring.
    /// </summary>
    private const string DevelopmentDataProtectionKeysPathKey = "WorkUp:Development:DataProtectionKeysPath";

    /// <summary>Required leaf prefix of the isolated keyring directory; keeps it recognizable and disposable.</summary>
    private const string DemoKeysDirectoryNamePrefix = "WorkUpDemoT12Keys";

    public static IServiceCollection AddWorkUp(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var section = configuration.GetSection("WorkUp");
        var options = section.Get<WorkUpOptions>() ?? new WorkUpOptions();
        PublicAppointmentUrlRules.ValidateBaseUrl(options.PublicBaseUrl, environment.IsDevelopment());

        // Fail fast on the scheduled-email configuration, before the host migrates the database and before the
        // scheduler can claim a notice, so a typo stops the startup instead of the queue. In Outbox mode the SMTP
        // block is deliberately never validated: the default configuration has an empty host and retaining notices
        // must stay valid.
        var email = options.Email ?? new EmailOptions();
        var emailMode = OutboxModeParser.Parse(email.Mode);
        if (emailMode == OutboxMode.Deliver)
            SmtpEmailSender.ValidateSettings(email);

        var connectionString = configuration.GetConnectionString("WorkUp")
            ?? throw new InvalidOperationException("Missing connection string \"WorkUp\" in appsettings.json.");
        services.Configure<WorkUpOptions>(section);

        // Infrastructure
        services.AddSingleton(new DatabaseMigrator(connectionString));
        services.AddScoped(_ => new DbSession(connectionString));
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<DbSession>());
        // The real business clock is the single source of real time, and the simulated clock of the T26 test tools
        // wraps it. Both are singletons: the installation shares one simulated instant across both demo businesses,
        // the offset lives only in memory (a restart is the safe reset, there is no SQL or SystemSettings row), and
        // resolving IClock or the control interface always returns the same instance. The gate is registered from the
        // already parsed email mode and the boot options; it defines no routes yet and keeps SMTP behaviour untouched.
        var businessClock = new BusinessClock(TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone));
        services.AddSingleton(businessClock);
        services.AddSingleton(sp => new SimulatedBusinessClock(sp.GetRequiredService<BusinessClock>()));
        services.AddSingleton<IClock>(sp => sp.GetRequiredService<SimulatedBusinessClock>());
        services.AddSingleton<ISimulatedClockControl>(sp => sp.GetRequiredService<SimulatedBusinessClock>());
        services.AddSingleton(sp => new DevToolsGate(
            environment,
            sp.GetRequiredService<IOptions<WorkUpOptions>>(),
            emailMode));
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
        services.AddScoped(sp => new DemoDataSeeder(
            connectionString,
            sp.GetRequiredService<IPasswordHasher>(),
            sp.GetRequiredService<IClock>()));
        AddWorkUpDataProtection(services, configuration, environment);
        services.AddSingleton<ILinkTokenProtector, DataProtectionLinkTokenProtector>();
        services.AddSingleton<IPublicAppointmentUrlBuilder, PublicAppointmentUrlBuilder>();

        // Modules: add repositories and services below as each task introduces them.

        // Businesses (T01)
        services.AddScoped<IBusinessRepository, SqlBusinessRepository>();
        services.AddScoped<IOwnerAccountRepository, SqlOwnerAccountRepository>();
        services.AddScoped<IExpenseTypeRepository, SqlExpenseTypeRepository>();
        services.AddScoped<RegisterBusinessService>();
        services.AddScoped<OwnerLoginService>();
        services.AddScoped<BusinessSettingsService>();
        services.AddScoped<ExpenseTypeService>();

        // Catalog (T02)
        services.AddScoped<IServiceRepository, SqlServiceRepository>();
        services.AddScoped<IMembershipPriceRepository, SqlMembershipPriceRepository>();
        services.AddScoped<CatalogService>();
        services.AddScoped<MembershipPriceService>();
        services.AddScoped<OwnerNavigationProvider>();

        // Staff (T03)
        services.AddScoped<IWorkerRepository, SqlWorkerRepository>();
        services.AddScoped<StaffService>();

        // Classes (T04)
        services.AddScoped<IGridClassRepository, SqlGridClassRepository>();
        services.AddScoped<ClassesService>();
        services.AddScoped<IWeekGenerationRepository, SqlWeekGenerationRepository>();
        services.AddScoped<WeekGenerationService>();

        // Class detail, attendance, walk-in, date actions and the late charge of a drop-in (T16, T18)
        services.AddScoped<IClassDetailRepository, SqlClassDetailRepository>();
        services.AddScoped<ClassDetailService>();
        services.AddScoped<IClassAttendanceRepository, SqlClassAttendanceRepository>();
        services.AddScoped<ClassAttendanceService>();
        services.AddScoped<ClassWalkInService>();
        services.AddScoped<ClassDropInChargeService>();
        services.AddScoped<IClassDateActionsRepository, SqlClassDateActionsRepository>();
        services.AddScoped<ClassDateActionsService>();

        // Clients (T05)
        services.AddScoped<IClientRepository, SqlClientRepository>();
        services.AddScoped<IClientNoteRepository, SqlClientNoteRepository>();
        services.AddScoped<ClientsService>();

        // Expenses and supplies (T06)
        services.AddScoped<IExpenseRepository, SqlExpenseRepository>();
        services.AddScoped<ExpenseService>();
        services.AddScoped<ISupplyRepository, SqlSupplyRepository>();
        services.AddScoped<SupplyService>();

        // Public individual reservations (T09)
        services.AddScoped<SqlPublicBookingRepository>();
        services.AddScoped<IPublicBookingRepository>(sp => sp.GetRequiredService<SqlPublicBookingRepository>());
        services.AddScoped<IPublicBookingBrowseRepository>(sp => sp.GetRequiredService<SqlPublicBookingRepository>());
        services.AddScoped<BookingService>();
        services.AddScoped<PublicBookingBrowseService>();
        services.AddScoped<SqlPrivateAppointmentLinkRepository>();
        services.AddScoped<IPrivateAppointmentLinkRepository>(sp =>
            sp.GetRequiredService<SqlPrivateAppointmentLinkRepository>());
        services.AddScoped<IOwnerPrivateAppointmentRepository>(sp =>
            sp.GetRequiredService<SqlPrivateAppointmentLinkRepository>());
        services.AddScoped<PrivateAppointmentLinkService>();
        services.AddScoped<OwnerAppointmentLinksService>();

        // Owner agenda (T11)
        services.AddScoped<IOwnerAgendaRepository, SqlOwnerAgendaRepository>();
        services.AddScoped<OwnerAgendaService>();
        services.AddScoped<IOwnerDayRepository, SqlOwnerDayRepository>();
        services.AddScoped<OwnerDayService>();
        services.AddScoped<IOwnerDayActionsRepository, SqlOwnerDayActionsRepository>();
        services.AddScoped<OwnerDayActionsService>();
        services.AddScoped<IOwnerWalkInRepository, SqlOwnerWalkInRepository>();
        services.AddScoped<OwnerWalkInService>();
        services.AddScoped<IOwnerBookingActionsRepository, SqlOwnerBookingActionsRepository>();
        services.AddScoped<OwnerBookingActionsService>();
        services.AddScoped<IOwnerRescheduleRepository, SqlOwnerRescheduleRepository>();
        services.AddScoped<OwnerRescheduleService>();

        // Public drop-in reservations (T14)
        services.AddScoped<IClassDropInRepository, SqlClassDropInRepository>();
        services.AddScoped<ClassDropInService>();

        // Memberships: enrollments and monthly memberships (T15)
        services.AddScoped<IEnrollmentRepository, SqlEnrollmentRepository>();
        services.AddScoped<EnrollmentService>();

        // Guided fixed-schedule change (T17)
        services.AddScoped<IFixedScheduleChangeRepository, SqlFixedScheduleChangeRepository>();
        services.AddScoped<FixedScheduleChangeService>();

        // Payments: charges, annulments and refunds (T18)
        services.AddScoped<IPaymentLedgerRepository, SqlPaymentLedgerRepository>();
        services.AddScoped<PaymentLedgerService>();

        // Reports: period result and indicators (T19)
        services.AddScoped<IReportsRepository, SqlReportsRepository>();
        services.AddScoped<ReportsService>();

        // Public client account: registration, verification, sign-in and recovery (T20)
        services.AddSingleton<IClientAccountUrlBuilder, ClientAccountUrlBuilder>();
        services.AddScoped<IClientRegistrationRepository, SqlClientRegistrationRepository>();
        services.AddScoped<ClientRegistrationService>();
        services.AddScoped<IClientAccessRepository, SqlClientAccessRepository>();
        services.AddScoped<ClientAccessService>();

        // Read-only client panel of the authenticated ficha: "Mis turnos y clases" (T21.1)
        services.AddScoped<IClientPanelRepository, SqlClientPanelRepository>();
        services.AddScoped<ClientPanelService>();

        // Confirm/cancel of an individual turn from the client panel (T21.2)
        services.AddScoped<IClientBookingActionsRepository, SqlClientBookingActionsRepository>();
        services.AddScoped<ClientBookingActionsService>();

        // Cancel of an own drop-in class from the client panel (T21.3). It reuses the IClassDropInRepository already
        // registered for the public drop-in reservations (T14) and the shared request DbSession, so no second,
        // incompatible session is created.
        services.AddScoped<ClientClassCancellationService>();

        // Owner-assisted invitation of an existing ficha: issue and revoke (RF-26, T22). The URL builder is a
        // singleton because it only reads the validated PublicBaseUrl, exactly like the other link builders; the
        // repository and the service are scoped so they share the request DbSession.
        services.AddSingleton<IClientInvitationUrlBuilder, ClientInvitationUrlBuilder>();
        services.AddScoped<IClientInvitationRepository, SqlClientInvitationRepository>();
        services.AddScoped<ClientInvitationService>();

        // Acceptance of the invitation from the public link (RF-26, T22): the read-only inspection plus the two
        // binding paths. The repository and the service are scoped so they share the request DbSession.
        services.AddScoped<IClientInvitationAcceptanceRepository, SqlClientInvitationAcceptanceRepository>();
        services.AddScoped<ClientInvitationAcceptanceService>();

        // Scheduled jobs and outbox dispatch (T23). The repository, the dispatcher, the outbox job and the runner are
        // scoped so every tick gets a fresh scope with its own DbSession; the sender is chosen once by the validated
        // mode, and the hosted worker is registered exactly once. There is intentionally no job-enabled gate: in the
        // default Outbox mode OutboxDispatchService claims nothing and contacts no provider, but the jobs of the tick
        // still run, so the tick performs each job's own bounded SQL read (the reminder scan registered below) while an
        // existing HTTP smoke stays inert and the fixed tick-complete marker keeps proving that the tick ran.
        services.AddScoped<IOutboxRepository, SqlOutboxRepository>();
        services.AddScoped(sp => new OutboxDispatchService(
            sp.GetRequiredService<IOutboxRepository>(),
            sp.GetRequiredService<IEmailSender>(),
            sp.GetRequiredService<IUnitOfWork>(),
            sp.GetRequiredService<IClock>(),
            emailMode));
        if (emailMode == OutboxMode.Deliver)
            services.AddScoped<IEmailSender, SmtpEmailSender>();
        else
            services.AddScoped<IEmailSender, RetainEmailSender>();
        // Scheduled reminder (T24, RF-17). The repository and the core are scoped so they share the tick's DbSession,
        // and the job is registered BEFORE the outbox job: the runner preserves registration order, so a notice the
        // reminder job queues during a tick already exists when the outbox job of that same tick runs, and a due
        // reminder is dispatched in the tick that queued it instead of a minute later.
        services.AddScoped<IReminderRepository, SqlReminderRepository>();
        services.AddScoped<ReminderService>();
        services.AddScoped<IScheduledJob, ReminderScheduledJob>();

        // Scheduled owner/client notices (T25, RF-18/RF-19/RF-30): the daily summary, the periodic stock review and the
        // membership expiry notice. Each repository and its core are scoped so they share the tick's DbSession, exactly
        // like the reminder graph above, and each job is registered AFTER the reminder job and BEFORE the outbox job:
        // the runner preserves registration order, so a notice one of these jobs queues during a tick already exists
        // when the outbox job of that same tick runs, and it is dispatched in the tick that queued it instead of a
        // minute later. The three jobs forward the runner-supplied instant and have no IClock of their own.
        services.AddScoped<IDailySummaryRepository, SqlDailySummaryRepository>();
        services.AddScoped<DailySummaryService>();
        services.AddScoped<IScheduledJob, DailySummaryScheduledJob>();
        services.AddScoped<IStockReviewRepository, SqlStockReviewRepository>();
        services.AddScoped<StockReviewService>();
        services.AddScoped<IScheduledJob, StockReviewScheduledJob>();
        services.AddScoped<IMembershipExpiryRepository, SqlMembershipExpiryRepository>();
        services.AddScoped<MembershipExpiryService>();
        services.AddScoped<IScheduledJob, MembershipExpiryScheduledJob>();

        services.AddScoped<IScheduledJob, OutboxScheduledJob>();
        services.AddScoped<ScheduledJobRunner>();
        services.AddHostedService<ScheduledJobsHostedService>();

        return services;
    }

    /// <summary>
    /// Without <see cref="DevelopmentDataProtectionKeysPathKey"/> the registration is exactly
    /// <c>services.AddDataProtection()</c>. With it set, the keyring goes to a validated throwaway directory.
    /// </summary>
    private static void AddWorkUpDataProtection(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var configuredKeysPath = configuration[DevelopmentDataProtectionKeysPathKey];
        if (string.IsNullOrWhiteSpace(configuredKeysPath))
        {
            services.AddDataProtection();
            return;
        }

        var keysDirectory = ResolveDevelopmentDataProtectionKeysDirectory(configuredKeysPath, environment);
        var dataProtection = services.AddDataProtection().PersistKeysToFileSystem(keysDirectory);
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException(
                $"{DevelopmentDataProtectionKeysPathKey} needs Windows DPAPI to protect the isolated keyring at rest, " +
                "so it is rejected on this platform instead of persisting unencrypted keys.");

        dataProtection.ProtectKeysWithDpapi();
    }

    /// <summary>
    /// Validates the opt-in development keyring path: absolute, canonical, inside the OS temp directory and
    /// with a leaf starting with <c>WorkUpDemoT12Keys</c>. Validation is purely lexical, so an invalid path
    /// is never read, created or written.
    /// </summary>
    private static DirectoryInfo ResolveDevelopmentDataProtectionKeysDirectory(
        string configuredKeysPath,
        IHostEnvironment environment)
    {
        if (!environment.IsDevelopment())
            throw new InvalidOperationException(
                $"{DevelopmentDataProtectionKeysPathKey} is development-only and must not be set outside Development.");

        if (configuredKeysPath != configuredKeysPath.Trim())
            throw new InvalidOperationException(
                $"{DevelopmentDataProtectionKeysPathKey} must not have leading or trailing whitespace.");

        if (!Path.IsPathFullyQualified(configuredKeysPath))
            throw new InvalidOperationException(
                $"{DevelopmentDataProtectionKeysPathKey} must be an absolute path.");

        string canonicalPath;
        try
        {
            canonicalPath = Path.GetFullPath(configuredKeysPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException(
                $"{DevelopmentDataProtectionKeysPathKey} is not a valid file system path.", ex);
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var relativeToTemporaryRoot = Path.GetRelativePath(temporaryRoot, canonicalPath);
        if (Path.IsPathRooted(relativeToTemporaryRoot)
            || relativeToTemporaryRoot == ".."
            || relativeToTemporaryRoot.StartsWith(".." + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException(
                $"{DevelopmentDataProtectionKeysPathKey} must resolve inside the OS temp directory ({temporaryRoot}).");

        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(canonicalPath));
        if (!leaf.StartsWith(DemoKeysDirectoryNamePrefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{DevelopmentDataProtectionKeysPathKey} must end with a directory whose name starts with \"{DemoKeysDirectoryNamePrefix}\".");

        return new DirectoryInfo(canonicalPath);
    }
}
