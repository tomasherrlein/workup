namespace WorkUp.Infrastructure;

/// <summary>Bound from the "WorkUp" section of appsettings.json.</summary>
public sealed class WorkUpOptions
{
    /// <summary>IANA id of the single business time zone (plan assumption 12).</summary>
    public string TimeZone { get; set; } = "America/Argentina/Buenos_Aires";

    /// <summary>Absolute base URL used to build links inside emails sent by background jobs.</summary>
    public string PublicBaseUrl { get; set; } = "https://localhost:7160";

    public bool MigrateOnStartup { get; set; } = true;

    public FeatureOptions Features { get; set; } = new();

    public EmailOptions Email { get; set; } = new();
}

public sealed class FeatureOptions
{
    /// <summary>Simulated clock, outbox viewer and manual job runs (plan assumption 11). Off in production.</summary>
    public bool DevTools { get; set; }
}

public sealed class EmailOptions
{
    /// <summary>"Outbox" keeps emails in the Notifications table without sending them; "Smtp" sends them.</summary>
    public string Mode { get; set; } = "Outbox";

    public string From { get; set; } = "WorkUp <no-responder@workup.local>";

    public SmtpOptions Smtp { get; set; } = new();
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public bool EnableSsl { get; set; } = true;
}
