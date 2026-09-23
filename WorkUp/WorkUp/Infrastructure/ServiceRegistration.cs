using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Clients;
using WorkUp.BusinessLogic.Staff;
using WorkUp.BusinessLogic.Expenses;
using WorkUp.BusinessLogic.Supplies;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Data;
using WorkUp.Data.Businesses;
using WorkUp.Data.Catalog;
using WorkUp.Data.Classes;
using WorkUp.Data.Clients;
using WorkUp.Data.DemoData;
using WorkUp.Data.Staff;
using WorkUp.Data.Expenses;
using WorkUp.Data.Supplies;
using WorkUp.Data.Scheduling;
using Microsoft.Extensions.Hosting;

namespace WorkUp.Infrastructure;

/// <summary>
/// Composition root. Every task registers its repositories and services here, one line each, grouped by module.
/// Repositories and services are scoped (they share the request DbSession).
/// </summary>
public static class ServiceRegistration
{
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
        services.AddDataProtection();
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

        return services;
    }
}
