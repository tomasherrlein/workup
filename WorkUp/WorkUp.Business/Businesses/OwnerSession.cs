namespace WorkUp.Business.Businesses;

/// <summary>
/// Identifies the owner and her business right after a successful registration or login. The web layer turns this
/// into the <c>Owner</c> cookie claims (<c>WorkUpClaims</c>); the business layer never touches HTTP.
/// </summary>
public sealed record OwnerSession(int BusinessId, string BusinessName, string BusinessSlug, int OwnerAccountId, string Username);
