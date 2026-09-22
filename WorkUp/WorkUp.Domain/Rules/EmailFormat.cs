using System.Text.RegularExpressions;

namespace WorkUp.Domain.Rules;

/// <summary>
/// Basic email format rule (RF 1): one local part, one '@', one domain part with at least one dot, no whitespace.
/// This is a shape check, not full RFC 5322 validation. Pure, no I/O; uniqueness per business is checked
/// separately by the repository.
/// </summary>
public static partial class EmailFormat
{
    public static bool IsValid(string? email) => !string.IsNullOrWhiteSpace(email) && Pattern().IsMatch(email);

    [GeneratedRegex(@"^[^@\s]+@[^@\s.][^@\s]*\.[^@\s]+$")]
    private static partial Regex Pattern();
}
