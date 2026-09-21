using WorkUp.Business.Abstractions;
using WorkUp.Business.Businesses;
using WorkUp.Data;
using WorkUp.Data.Businesses;

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

        return services;
    }
}
