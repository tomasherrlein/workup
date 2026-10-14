namespace WorkUp.BusinessLogic.ClientAccounts;

/// <summary>
/// Client sign-in (RF-15). Like registration, the business is identified only by its public slug, so a caller cannot
/// ask for an account of a tenant it did not reach through the public route.
/// </summary>
public sealed record ClientLoginRequest(string Slug, string Email, string Password);

/// <summary>
/// Server-side identity of an authenticated client (RF-15). It carries only what the session cookie needs to be
/// signed and rebuilt: the tenant (<see cref="BusinessId"/>, <see cref="BusinessSlug"/>, <see cref="BusinessName"/>)
/// and the pair that authorizes the panel (<see cref="ClientAccountId"/>, <see cref="ClientId"/>). It never carries
/// the password, its hash, or any token.
/// </summary>
public sealed record ClientSession(
    int BusinessId,
    string BusinessSlug,
    string BusinessName,
    int ClientAccountId,
    int ClientId,
    string Email);

/// <summary>
/// Password recovery request (RF-27). The answer is the same whether the email has an active account or not, so it
/// never reveals who has an account in the business.
/// </summary>
public sealed record ClientPasswordResetRequest(string Slug, string Email);

/// <summary>Completion of a password recovery from the emailed link (RF-27).</summary>
public sealed record CompleteClientPasswordResetRequest(string Slug, string Token, string NewPassword);

/// <summary>
/// A client account that is allowed to start a session and to receive a recovery link: it belongs to the business,
/// it has a verified email and it is already linked to its ficha. A pending or unlinked account is never active.
/// </summary>
public sealed record ActiveClientAccount(int ClientAccountId, int ClientId, string Email, string PasswordHash);
