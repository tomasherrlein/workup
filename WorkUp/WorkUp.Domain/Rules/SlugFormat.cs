using System.Text.RegularExpressions;

namespace WorkUp.Domain.Rules;

/// <summary>
/// Format rule for the public slug segment of the booking link <c>/r/{slug}</c> (RF-01): lowercase letters, digits
/// and single hyphens between words, matching the schema column <c>Businesses.Slug VARCHAR(60)</c>. Pure, no I/O;
/// uniqueness is checked separately by the repository.
/// </summary>
public static partial class SlugFormat
{
    public const int MaxLength = 60;

    public static bool IsValid(string slug) =>
        slug.Length is > 0 and <= MaxLength && Pattern().IsMatch(slug);

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Pattern();
}
