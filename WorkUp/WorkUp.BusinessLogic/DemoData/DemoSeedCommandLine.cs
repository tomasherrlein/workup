namespace WorkUp.BusinessLogic.DemoData;

public sealed record DemoSeedCommandLineResult(bool IsValid, bool SeedRequested, string[] BuilderArguments, string? ErrorCode);

public static class DemoSeedCommandLine
{
    private const string Flag = "--seed-demo-data";
    private const string FlagNamespace = "--seed-demo";

    public static DemoSeedCommandLineResult Parse(string[] args)
    {
        var malformedFlag = args.Any(argument =>
            argument.StartsWith(FlagNamespace, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(argument, Flag, StringComparison.Ordinal));
        if (malformedFlag)
            return new(false, false, [], "malformed_command_flag");

        var flagCount = args.Count(argument => string.Equals(argument, Flag, StringComparison.Ordinal));
        if (flagCount > 1)
            return new(false, false, [], "duplicate_command_flag");

        return new(
            true,
            flagCount == 1,
            args.Where(argument => !string.Equals(argument, Flag, StringComparison.Ordinal)).ToArray(),
            null);
    }
}
