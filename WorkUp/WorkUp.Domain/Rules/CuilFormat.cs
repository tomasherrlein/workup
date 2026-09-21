namespace WorkUp.Domain.Rules;

/// <summary>
/// CUIL format rule (RF-03): exactly 11 digits, accepted with or without dashes or spaces, normalized to
/// <c>XX-XXXXXXXX-X</c> for storage and display, matching the schema column <c>Workers.Cuil VARCHAR(13)</c>.
/// Pure, no I/O; uniqueness per business is checked separately by the repository.
/// </summary>
public static class CuilFormat
{
    public const int NormalizedLength = 13;

    /// <summary>Strips anything that is not a digit and, when exactly 11 digits remain, formats them as XX-XXXXXXXX-X.</summary>
    public static bool TryNormalize(string? input, out string normalized)
    {
        var digits = input is null ? "" : new string(input.Where(char.IsDigit).ToArray());
        if (digits.Length != 11)
        {
            normalized = "";
            return false;
        }

        normalized = $"{digits[..2]}-{digits[2..10]}-{digits[10..]}";
        return true;
    }
}
