using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Clients;
using WorkUp.BusinessLogic.Staff;
using WorkUp.BusinessLogic.Expenses;
using WorkUp.BusinessLogic.Supplies;
using WorkUp.Data;
using WorkUp.Data.Businesses;
using WorkUp.Data.Catalog;
using WorkUp.Data.Classes;
using WorkUp.Data.Clients;
using WorkUp.Data.Staff;
using WorkUp.Data.Expenses;
using WorkUp.Data.Supplies;

namespace WorkUp.Infrastructure;

/// <summary>
/// Composition root. Every task registers its repositories and services here, one line each, grouped by module.
/// Repositories and services are scoped (they share the request DbSession).
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddWorkUp(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("WorkUp")
            ?? throw new InvalidOperationException("Missing connection string \"WorkUp\" in appsettings.json.");

        var section = configuration.GetSection("WorkUp");
        services.Configure<WorkUpOptions>(section);
        var options = section.Get<WorkUpOptions>() ?? new WorkUpOptions();

        // Infrastructure
        services.AddSingleton(new DatabaseMigrator(connectionString));
        services.AddScoped(_ => new DbSession(connectionString));
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<DbSession>());
        services.AddSingleton<IClock>(new BusinessClock(TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone)));
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
        services.AddDataProtection();
        services.AddSingleton<ILinkTokenProtector, DataProtectionLinkTokenProtector>();

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

        return services;
    }
}
