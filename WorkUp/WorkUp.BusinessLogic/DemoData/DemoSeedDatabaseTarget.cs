using System.Data.Common;
using System.Globalization;

namespace WorkUp.BusinessLogic.DemoData;

public sealed record DemoSeedDatabaseTargetResult(bool IsAllowed, string? ErrorCode);

/// <summary>Only dedicated databases on the default LocalDB instance can receive the demo seed.</summary>
public static class DemoSeedDatabaseTarget
{
    private static readonly string[] DataSourceKeys = ["Data Source", "Server", "Address", "Addr", "Network Address"];
    private static readonly string[] CatalogKeys = ["Initial Catalog", "Database"];
    private static readonly string[] IntegratedSecurityKeys = ["Integrated Security", "Trusted_Connection"];
    private static readonly string[] UserIdKeys = ["User ID", "UID"];
    private static readonly string[] PasswordKeys = ["Password", "Pwd"];

    public static DemoSeedDatabaseTargetResult Evaluate(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return Rejected();

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            if (!TryGetSingleValue(builder, DataSourceKeys, out var dataSource)
                || !string.Equals(dataSource, @"(localdb)\MSSQLLocalDB", StringComparison.OrdinalIgnoreCase)
                || !TryGetSingleValue(builder, CatalogKeys, out var catalog)
                || !IsDedicatedCatalog(catalog)
                || !TryGetSingleValue(builder, IntegratedSecurityKeys, out var integratedSecurity)
                || !(bool.TryParse(integratedSecurity, out var trusted) && trusted || integratedSecurity == "SSPI")
                || HasAnyKey(builder, UserIdKeys)
                || HasAnyKey(builder, PasswordKeys)
                || HasAnyKey(builder, ["AttachDbFilename", "Failover Partner"]))
                return Rejected();

            return new(true, null);
        }
        catch (ArgumentException)
        {
            return Rejected();
        }
    }

    private static bool IsDedicatedCatalog(string catalog) =>
        catalog.StartsWith("WorkUpDemo", StringComparison.OrdinalIgnoreCase)
        || catalog.StartsWith("WorkUpT07Demo", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetSingleValue(DbConnectionStringBuilder builder, IReadOnlyList<string> aliases, out string value)
    {
        var presentKeys = aliases.Where(builder.ContainsKey).ToArray();
        if (presentKeys.Length != 1)
        {
            value = "";
            return false;
        }

        value = Convert.ToString(builder[presentKeys[0]], CultureInfo.InvariantCulture) ?? "";
        return value.Length > 0;
    }

    private static bool HasAnyKey(DbConnectionStringBuilder builder, IReadOnlyList<string> keys) => keys.Any(builder.ContainsKey);

    private static DemoSeedDatabaseTargetResult Rejected() => new(false, "dedicated_localdb_required");
}
