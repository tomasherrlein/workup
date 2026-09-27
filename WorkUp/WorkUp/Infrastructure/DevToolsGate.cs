using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WorkUp.BusinessLogic.Notifications;

namespace WorkUp.Infrastructure;

/// <summary>
/// Fail-closed gate of the T26 test tools (simulated clock, generated-email viewer and manual job runs).
/// <para>
/// The simulated clock moves both demo businesses at once, so the tools are exposed only when <em>all three</em>
/// conditions hold at startup:
/// <list type="number">
/// <item>the host environment is <c>Development</c>;</item>
/// <item><c>WorkUp:Features:DevTools</c> is explicitly <c>true</c>;</item>
/// <item>the parsed and validated email mode retains notices (<c>Outbox</c>), never <c>Smtp</c>.</item>
/// </list>
/// Every deviation is a refusal. A flag mistakenly turned on in production, a real SMTP configuration in Development,
/// a missing feature block or an environment name that merely looks like Development keeps the gate closed, so a
/// direct URL request cannot reach a tool when any condition is missing.
/// </para>
/// <para>
/// The decision is read once by the singleton, from the already validated options and the already parsed
/// <see cref="OutboxMode"/>, and can never be widened afterwards by mutating the options object.
/// </para>
/// <para>
/// This gate is <em>necessary but not sufficient</em>: the T26 tools are owner-only, so the controller layer must still
/// require an authenticated owner (<c>OwnerControllerBase</c>) on top of it. The gate deliberately defines no routes
/// and changes no configuration default and no SMTP behaviour.
/// </para>
/// </summary>
public sealed class DevToolsGate
{
    private readonly bool _isEnabled;

    public DevToolsGate(IHostEnvironment environment, IOptions<WorkUpOptions> options, OutboxMode emailMode)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);

        // Fail closed: a null feature block is a refusal, never a default-on.
        var devToolsEnabled = options.Value?.Features?.DevTools == true;

        _isEnabled = environment.IsDevelopment()
            && devToolsEnabled
            && emailMode == OutboxMode.Retain;
    }

    /// <summary>
    /// True only for a Development host with <c>Features.DevTools=true</c> and the retaining (<c>Outbox</c>) email
    /// mode. False in every other case, including a mistaken production flag and an SMTP installation.
    /// </summary>
    public bool IsEnabled => _isEnabled;
}
