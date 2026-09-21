namespace WorkUp.Infrastructure.Auth;

/// <summary>
/// Two independent cookie schemes: the business owner (panel) and the optional client account. A browser can hold
/// both. Workers never sign in (D-06).
/// </summary>
public static class AuthSchemes
{
    public const string Owner = "Owner";
    public const string Client = "Client";
}

public static class WorkUpClaims
{
    public const string BusinessId = "workup:business_id";
    public const string BusinessName = "workup:business_name";
    public const string BusinessSlug = "workup:business_slug";
    public const string ClientId = "workup:client_id";
    public const string ClientAccountId = "workup:client_account_id";
}
