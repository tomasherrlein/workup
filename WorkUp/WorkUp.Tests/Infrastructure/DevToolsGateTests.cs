using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WorkUp.BusinessLogic.Notifications;
using WorkUp.Infrastructure;

namespace WorkUp.Tests.Infrastructure;

/// <summary>
/// T26: the gate of the test tools must fail closed, because the simulated clock is installation-wide and moving it
/// changes both demo businesses at once. It is enabled only when the environment is <c>Development</c>, the explicit
/// <c>Features.DevTools</c> flag is on and the validated email mode retains notices; an owner session will still be
/// required on top of this gate, so the gate is necessary but never sufficient.
/// </summary>
public sealed class DevToolsGateTests
{
    [Theory]
    [InlineData("Development", true, OutboxMode.Retain, true)]
    [InlineData("Development", false, OutboxMode.Retain, false)]
    [InlineData("Development", true, OutboxMode.Deliver, false)]
    [InlineData("Development", false, OutboxMode.Deliver, false)]
    [InlineData("Production", true, OutboxMode.Retain, false)]
    [InlineData("Production", true, OutboxMode.Deliver, false)]
    [InlineData("Staging", true, OutboxMode.Retain, false)]
    [InlineData("development", true, OutboxMode.Retain, true)]
    [InlineData("Development ", true, OutboxMode.Retain, false)]
    [InlineData("PreDevelopment", true, OutboxMode.Retain, false)]
    [InlineData("", true, OutboxMode.Retain, false)]
    public void Gate_is_enabled_only_in_Development_with_the_flag_on_and_a_retaining_mode(
        string environmentName,
        bool devTools,
        OutboxMode mode,
        bool expected)
    {
        var gate = NewGate(environmentName, devTools, mode);

        Assert.Equal(expected, gate.IsEnabled);
    }

    [Fact]
    public void Gate_refuses_when_the_feature_block_is_missing()
    {
        var options = new WorkUpOptions { Features = null! };

        var gate = new DevToolsGate(
            NewEnvironment(Environments.Development), Options.Create(options), OutboxMode.Retain);

        Assert.False(gate.IsEnabled);
    }

    [Fact]
    public void Gate_refuses_when_the_options_snapshot_is_null()
    {
        var gate = new DevToolsGate(
            NewEnvironment(Environments.Development), Options.Create<WorkUpOptions>(null!), OutboxMode.Retain);

        Assert.False(gate.IsEnabled);
    }

    [Fact]
    public void Gate_cannot_be_turned_on_after_it_was_constructed_disabled()
    {
        var options = new WorkUpOptions { Features = new FeatureOptions { DevTools = false } };
        var gate = new DevToolsGate(
            NewEnvironment(Environments.Development), Options.Create(options), OutboxMode.Retain);
        Assert.False(gate.IsEnabled);

        // The singleton reads the boot configuration once. Widening the flag later must never open the gate.
        options.Features.DevTools = true;

        Assert.False(gate.IsEnabled);
    }

    [Fact]
    public void Composition_root_gate_is_enabled_only_for_a_development_installation_that_retains_notices()
    {
        Assert.True(GateEnabledBy("Development", devTools: true, mode: "Outbox"));
        // A missing mode also parses to the retaining default, so the gate follows the validated mode, not the text.
        Assert.True(GateEnabledBy("Development", devTools: true, mode: ""));
        // A flag mistakenly turned on in production must not open the tools.
        Assert.False(GateEnabledBy("Production", devTools: true, mode: "Outbox"));
        // A real SMTP configuration must never expose the tools, even in Development.
        Assert.False(GateEnabledBy("Development", devTools: true, mode: "Smtp"));
        Assert.False(GateEnabledBy("Development", devTools: false, mode: "Outbox"));
    }

    [Fact]
    public void The_gate_is_a_singleton_shared_across_scopes()
    {
        var services = new ServiceCollection();
        services.AddWorkUp(
            BuildConfiguration(DevToolsSettings(devTools: true, mode: "Outbox")),
            new FakeHostEnvironment());

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(DevToolsGate));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Same(
            provider.GetRequiredService<DevToolsGate>(),
            scope.ServiceProvider.GetRequiredService<DevToolsGate>());
    }

    // ---- Helpers ----

    private static DevToolsGate NewGate(string environmentName, bool devTools, OutboxMode mode)
    {
        var options = new WorkUpOptions { Features = new FeatureOptions { DevTools = devTools } };
        return new DevToolsGate(NewEnvironment(environmentName), Options.Create(options), mode);
    }

    private static IHostEnvironment NewEnvironment(string environmentName) =>
        new FakeHostEnvironment { EnvironmentName = environmentName };

    private static bool GateEnabledBy(string environmentName, bool devTools, string mode)
    {
        var services = new ServiceCollection();
        services.AddWorkUp(
            BuildConfiguration(DevToolsSettings(devTools, mode)),
            NewEnvironment(environmentName));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<DevToolsGate>().IsEnabled;
    }

    private static Dictionary<string, string?> DevToolsSettings(bool devTools, string mode)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:WorkUp"] =
                @"Server=(localdb)\MSSQLLocalDB;Database=WorkUpT26Gate;Trusted_Connection=True;TrustServerCertificate=True",
            ["WorkUp:PublicBaseUrl"] = "https://reservas.example.com",
            ["WorkUp:TimeZone"] = "America/Argentina/Buenos_Aires",
            ["WorkUp:Features:DevTools"] = devTools ? "true" : "false",
            ["WorkUp:Email:Mode"] = mode
        };
        if (string.Equals(mode, "Smtp", StringComparison.OrdinalIgnoreCase))
            settings["WorkUp:Email:Smtp:Host"] = "smtp.example.test";

        return settings;
    }

    private static IConfiguration BuildConfiguration(IDictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "WorkUp.Tests";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
