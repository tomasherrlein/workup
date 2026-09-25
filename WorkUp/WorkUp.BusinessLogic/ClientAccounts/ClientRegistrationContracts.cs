using WorkUp.BusinessLogic.Scheduling;

namespace WorkUp.BusinessLogic.ClientAccounts;

/// <summary>
/// Self-registration (RF-25). The business is identified only by its public slug; a request never carries a
/// business id, so a caller cannot register an account in a tenant it did not reach through the public route.
/// </summary>
public sealed record RegisterClientAccountRequest(
    string Slug,
    string FirstName,
    string LastName,
    string Email,
    string Password);

/// <summary>Non-enumerating resend request (RF-25): the answer is the same whether the email exists or not.</summary>
public sealed record ResendClientVerificationRequest(string Slug, string Email);

/// <summary>
/// Safe outcome of a registration. It never carries the verification token: the raw token exists only in the queued
/// email and the database keeps its SHA-256 hash (D-18).
/// </summary>
public sealed record ClientRegistrationReceipt(string Email, DateTime VerificationExpiresAt);

/// <summary>Result of confirming an email: the account is now linked to its ficha and can start a session.</summary>
public sealed record VerifiedClientAccount(int ClientAccountId, int ClientId, string Email, string FirstName, string LastName);

/// <summary>
/// Which email keys are in use inside one business. Both sources count as a collision for RF-25: a ficha
/// (<c>Clients</c>) and an existing account (<c>ClientAccounts</c>) are never duplicated by self-registration.
/// </summary>
public sealed record ClientRegistrationEmailState(bool HasClient, bool HasAccount)
{
    public bool IsTaken => HasClient || HasAccount;
}

/// <summary>
/// The temporary names of an account whose email is not verified yet. They live on the pending account so no visible
/// ficha is created before verification (D-18).
/// </summary>
public sealed record PendingClientAccount(
    int ClientAccountId,
    string Email,
    string? PendingFirstName,
    string? PendingLastName);

/// <summary>Builds the absolute verification link for a client account.</summary>
public interface IClientAccountUrlBuilder
{
    string BuildVerificationUrl(string slug, string token);
}

/// <summary>
/// Trusted-base-URL rules for client-account links. They reuse the single public base URL validation of the
/// application (<see cref="PublicAppointmentUrlRules"/>): the link is derived from the configured
/// <c>WorkUp:PublicBaseUrl</c> and from nothing else, so no request header can redirect a verification link.
/// </summary>
public static class ClientAccountUrlRules
{
    /// <summary>Path of the verification route below the public base URL (RF-25).</summary>
    public const string VerificationPathPrefix = "r/";

    public static Uri ValidateBaseUrl(string configuredBaseUrl) =>
        PublicAppointmentUrlRules.ValidateBaseUrl(configuredBaseUrl);

    public static string BuildVerificationUrl(string configuredBaseUrl, string slug, string token)
    {
        var validatedBaseUrl = ValidateBaseUrl(configuredBaseUrl);
        if (string.IsNullOrWhiteSpace(slug))
            throw new ArgumentException("A business slug is required.", nameof(slug));
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("A verification token is required.", nameof(token));

        return new Uri(
            validatedBaseUrl,
            $"{VerificationPathPrefix}{Uri.EscapeDataString(slug)}/cuenta/verificar/{Uri.EscapeDataString(token)}")
            .AbsoluteUri;
    }
}
