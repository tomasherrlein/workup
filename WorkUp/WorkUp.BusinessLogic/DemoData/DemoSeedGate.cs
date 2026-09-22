namespace WorkUp.BusinessLogic.DemoData;

public sealed record DemoSeedGateResult(bool IsAllowed, string? ErrorCode);

public static class DemoSeedGate
{
    public static DemoSeedGateResult Evaluate(bool hasCommandFlag, string environmentName, bool enabled, string? password)
    {
        if (!hasCommandFlag)
            return new(false, "command_flag_required");
        if (!string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
            return new(false, "development_only");
        if (!enabled)
            return new(false, "demo_configuration_required");
        if (string.IsNullOrWhiteSpace(password))
            return new(false, "password_required");

        return new(true, null);
    }
}
