using System.Security.Claims;

namespace WorkUp.Infrastructure.Auth;

public static class ClaimsPrincipalExtensions
{
    public static int GetBusinessId(this ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(WorkUpClaims.BusinessId)
            ?? throw new InvalidOperationException("The signed-in user has no business."));

    public static int GetOwnerAccountId(this ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("The signed-in user has no owner account."));

    public static string GetBusinessName(this ClaimsPrincipal user) => user.FindFirstValue(WorkUpClaims.BusinessName) ?? "";

    public static string GetBusinessSlug(this ClaimsPrincipal user) => user.FindFirstValue(WorkUpClaims.BusinessSlug) ?? "";

    /// <summary>Owner username, recorded as the person responsible for annulments (RF-28).</summary>
    public static string GetOwnerUsername(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Name) ?? "";

    public static int GetClientId(this ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(WorkUpClaims.ClientId)
            ?? throw new InvalidOperationException("The signed-in user has no client record."));

    public static int GetClientAccountId(this ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(WorkUpClaims.ClientAccountId)
            ?? throw new InvalidOperationException("The signed-in user has no client account."));
}
