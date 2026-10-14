using Microsoft.Extensions.Options;
using WorkUp.BusinessLogic.Scheduling;

namespace WorkUp.Infrastructure;

/// <summary>Builds private appointment links only from the trusted WorkUp:PublicBaseUrl setting.</summary>
public sealed class PublicAppointmentUrlBuilder : IPublicAppointmentUrlBuilder
{
    private readonly string _configuredBaseUrl;

    public PublicAppointmentUrlBuilder(IOptions<WorkUpOptions> options)
    {
        _configuredBaseUrl = options.Value.PublicBaseUrl;
        PublicAppointmentUrlRules.ValidateBaseUrl(_configuredBaseUrl);
    }

    public string Build(string token) => PublicAppointmentUrlRules.Build(_configuredBaseUrl, token);
}
