using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Clients;
using WorkUp.BusinessLogic.Enrollments;
using WorkUp.BusinessLogic.Payments;
using WorkUp.BusinessLogic.Staff;
using WorkUp.BusinessLogic.Expenses;
using WorkUp.BusinessLogic.Supplies;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Data;
using WorkUp.Data.Businesses;
using WorkUp.Data.Catalog;
using WorkUp.Data.Classes;
using WorkUp.Data.Clients;
using WorkUp.Data.Enrollments;
using WorkUp.Data.DemoData;
using WorkUp.Data.Payments;
using WorkUp.Data.Staff;
using WorkUp.Data.Expenses;
using WorkUp.Data.Supplies;
using WorkUp.Data.Scheduling;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;

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

        var connectionString = configuration.GetConnectionString("WorkUp")
            ?? throw new InvalidOperationException("Missing connection string \"WorkUp\" in appsettings.json.");
        services.Configure<WorkUpOptions>(section);

        // Infrastructure
        services.AddSingleton(new DatabaseMigrator(connectionString));
        services.AddScoped(_ => new DbSession(connectionString));
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<DbSession>());
        services.AddSingleton<IClock>(new BusinessClock(TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone)));
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
