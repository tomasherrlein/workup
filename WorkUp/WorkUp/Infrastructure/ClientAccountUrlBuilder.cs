using Microsoft.Extensions.Options;
using WorkUp.BusinessLogic.ClientAccounts;

namespace WorkUp.Infrastructure;

/// <summary>Builds client account links only from the validated WorkUp:PublicBaseUrl setting.</summary>
public sealed class ClientAccountUrlBuilder : IClientAccountUrlBuilder
{
    private readonly string configuredBaseUrl;

    public ClientAccountUrlBuilder(IOptions<WorkUpOptions> options)
    {
        configuredBaseUrl = options.Value.PublicBaseUrl;
        ClientAccountUrlRules.ValidateBaseUrl(configuredBaseUrl);
    }

    public string BuildVerificationUrl(string slug, string token) =>
        ClientAccountUrlRules.BuildVerificationUrl(configuredBaseUrl, slug, token);

    public string BuildPasswordResetUrl(string slug, string token) =>
        ClientAccountUrlRules.BuildPasswordResetUrl(configuredBaseUrl, slug, token);
}
