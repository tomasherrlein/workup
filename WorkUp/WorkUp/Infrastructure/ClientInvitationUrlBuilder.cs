using Microsoft.Extensions.Options;
using WorkUp.BusinessLogic.ClientAccounts;

namespace WorkUp.Infrastructure;

/// <summary>
/// Builds the assisted-invitation link of RF-26 only from the validated <c>WorkUp:PublicBaseUrl</c> setting, exactly
/// like the verification and recovery builders. It takes no request input, so neither a hostile <c>Host</c> header nor
/// the slug of another business can retarget the link the invited person receives.
/// </summary>
public sealed class ClientInvitationUrlBuilder : IClientInvitationUrlBuilder
{
    private readonly string configuredBaseUrl;

    public ClientInvitationUrlBuilder(IOptions<WorkUpOptions> options)
    {
        configuredBaseUrl = options.Value.PublicBaseUrl;
        ClientAccountUrlRules.ValidateBaseUrl(configuredBaseUrl);
    }

    public string BuildInvitationUrl(string slug, string token) =>
        ClientAccountUrlRules.BuildInvitationUrl(configuredBaseUrl, slug, token);
}
