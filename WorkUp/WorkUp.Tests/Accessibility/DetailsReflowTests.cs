using System.Text.RegularExpressions;

namespace WorkUp.Tests.Accessibility;

// T27 reflow defect of the shared `.details` definition list, pinned at the source level.
//
// The browser observation behind this file: on the public Contact and Confirmation screens at a 360px viewport with a
// 32px root font (200% text) the page's `document.documentElement.scrollWidth` measured 386 and 394 against a
// `clientWidth` of 345, so the document scrolled sideways and every `dd` value sat outside the viewport. The cause is
// the track sizing `grid-template-columns: max-content 1fr`: `max-content` sizes the label column to its longest word,
// which at 200% text is already wider than the whole viewport, and the `1fr` value column is shorthand for
// `minmax(auto, 1fr)` whose `auto` minimum is the value's min-content contribution. Together the two tracks cannot
// shrink and the grid overflows the page instead of reflowing.
//
// The fix this file pins is a narrow-width override, not a change to the desktop contract: at `max-width: 480px` the
// list becomes a single full-width column so each `dt` and `dd` wraps on its own line, `minmax(0, 1fr)` removes the
// content-based minimum that let a track outgrow the viewport, and `overflow-wrap: anywhere` lets an unbreakable token
// break inside the line. Because the DOM order of the `dt`/`dd` pairs is untouched, their pairing is preserved; no
// `white-space: nowrap` or `text-overflow: ellipsis` is introduced, so the full text stays readable at 200% text.
//
// These are source-level assertions over `workup.css` and the view sources, not a rendered-DOM check: they prove the
// declarations the stylesheet ships and the cascade position they live in, never the computed box a browser resolves.
// The browser-side remainder is the independent verifier's work on a fresh target: measuring the page's scrollWidth
// against clientWidth at 360px/200% on Contact, Confirmation and PrivateAppointments/Index. No browser and no database
// is launched here.
public sealed class DetailsReflowTests
{
    private const string NarrowBreakpoint = "@media (max-width: 480px)";

    // --- Narrow-width reflow -------------------------------------------------------------------------------------

    [Fact]
    public void The_details_list_stacks_into_one_full_width_column_at_narrow_width()
    {
        // `minmax(0, 1fr)`: a single track with a zero minimum. The zero is the load-bearing half of the fix -- it
        // cannot be inflated by a value's min-content, unlike the `1fr` (= `minmax(auto, 1fr)`) the desktop rule ships.
        var columns = NarrowDetailsDeclaration(".details", "grid-template-columns");
        Assert.Matches(@"^minmax\(\s*0\s*,\s*1fr\s*\)$", columns);
        Assert.DoesNotContain("max-content", columns);
        Assert.DoesNotContain("minmax(auto", columns);
    }

    [Fact]
    public void The_narrow_details_labels_and_values_can_break_inside_a_word()
    {
        // Both the label and the value must be breakable: a long worker name can sit in either cell at 200% text.
        // `anywhere` (not `break-word`) also lowers the element's min-content contribution, so the single track can
        // shrink below the longest unbreakable token instead of forcing a sideways scroll.
        Assert.Equal("anywhere", NarrowDetailsDeclaration(".details dt", "overflow-wrap"));
        Assert.Equal("anywhere", NarrowDetailsDeclaration(".details dd", "overflow-wrap"));
    }

    [Fact]
    public void No_details_rule_truncates_a_label_or_value_with_an_ellipsis_or_nowrap()
    {
        var offenders = new List<string>();

        // The whole point of the defect is that the full text must stay visible; the last-resort desktop pattern
        // (nowrap plus ellipsis) is exactly what must not appear anywhere in the `.details` cascade, media queries
        // included.
        foreach (var (selector, body) in AllRules(Css()))
        {
            if (!selector.Contains(".details", StringComparison.Ordinal)) continue;
            if (Regex.IsMatch(body, @"(?:^|;)\s*text-overflow\s*:\s*ellipsis"))
            {
                offenders.Add($"{selector} => text-overflow: ellipsis");
            }

            if (Regex.IsMatch(body, @"(?:^|;)\s*white-space\s*:\s*nowrap"))
            {
                offenders.Add($"{selector} => white-space: nowrap");
            }
        }

        Assert.Empty(offenders);
    }

    // --- Desktop contract unchanged ------------------------------------------------------------------------------

    [Fact]
    public void The_desktop_two_column_contract_is_unchanged()
    {
        var body = TopLevelRule(Css(), ".details");

        // Labels above value on desktop: the base cascade keeps the original `max-content 1fr` tracks and must not
        // carry the narrow reflow override, which lives only inside the `max-width: 480px` media query.
        Assert.Equal("max-content 1fr", Declaration(body, "grid-template-columns"));
        Assert.DoesNotContain("minmax", body);
    }

    [Fact]
    public void Only_the_narrow_breakpoint_replaces_the_two_column_tracks()
    {
        var columnRules = AllRules(Css())
            .Where(rule => rule.Selector.Contains(".details", StringComparison.Ordinal))
            .Where(rule => Regex.IsMatch(rule.Body, @"(?:^|;)\s*grid-template-columns\s*:"))
            .ToArray();

        // Exactly two rules touch the tracks -- the desktop base and the narrow override -- so the reflow cannot leak
        // into a wider breakpoint and silently change the desktop layout.
        Assert.Equal(2, columnRules.Length);
        Assert.Contains(columnRules, rule => Declaration(rule.Body, "grid-template-columns") == "max-content 1fr");
        Assert.Contains(columnRules, rule => Declaration(rule.Body, "grid-template-columns") == "minmax(0, 1fr)");
    }

    // --- Label/value pairing in the views -------------------------------------------------------------------------

    [Fact]
    public void The_detail_views_keep_each_label_immediately_paired_with_its_value()
    {
        var offenders = new List<string>();
        var detailViews = 0;

        foreach (var file in EnumerateViews())
        {
            var view = File.ReadAllText(file);
            if (!Regex.IsMatch(view, "class=\"[^\"]*\\bdetails\\b")) continue;
            detailViews++;

            // The reflow is CSS-only: the views keep shipping one `dd` for every `dt`, alternating label then value,
            // so a single-column stack renders each pair in the same order a screen reader reads the definition list.
            var tags = Regex.Matches(view, @"<dt\b|<dd\b")
                .Select(match => match.Value)
                .ToArray();
            var paired = tags.Length > 0 && tags.Length % 2 == 0
                && tags.Where((tag, index) => index % 2 == 0).All(tag => tag == "<dt")
                && tags.Where((tag, index) => index % 2 == 1).All(tag => tag == "<dd");
            if (!paired)
            {
                offenders.Add($"{Relative(file)} => {string.Join(", ", tags)}");
            }
        }

        // PrivateAppointments/Index shares `.details`, so its pairing is covered by the same guard.
        Assert.Equal(4, detailViews);
        Assert.Empty(offenders);
    }

    // --- Fixture -------------------------------------------------------------------------------------------------

    private static string Css() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "WorkUp", "wwwroot", "css", "workup.css"));

    // The single declaration of `property` on `selector`, among the rules inside the `max-width: 480px` media query.
    // A selector may own several narrow rules (the stack, the break and the pair spacing), so the lookup keys on the
    // property rather than on the rule count.
    private static string NarrowDetailsDeclaration(string selector, string property)
    {
        var values = RulesInMedia(Css(), IsNarrowMedia)
            .Where(rule => SelectorParts(rule.Selector).Contains(selector))
            .Select(rule => OptionalDeclaration(rule.Body, property))
            .Where(value => value is not null)
            .ToArray();
        return Assert.Single(values)!;
    }

    private static bool IsNarrowMedia(string header) =>
        Regex.IsMatch(header, @"^@media\b") && Regex.IsMatch(header, @"max-width\s*:\s*480px");

    // Top-level declarations only: at-rule blocks (media queries) are skipped whole, so what this finds is what the
    // bare cascade ships to a wide viewport. The narrow override must not appear here.
    private static IReadOnlyList<(string Selector, string Body)> TopLevelRules(string css)
    {
        var source = StripComments(css);
        var rules = new List<(string Selector, string Body)>();
        var index = 0;
        while (index < source.Length)
        {
            var open = source.IndexOf('{', index);
            if (open < 0) break;
            var end = MatchingBrace(source, open);
            var selector = Collapse(source[index..open]);
            if (!selector.StartsWith('@'))
            {
                rules.Add((selector, source[(open + 1)..end]));
            }

            index = end + 1;
        }

        return rules;
    }

    // Rules nested inside every block whose header satisfies `headerMatches`, flattened, media queries included.
    private static IEnumerable<(string Selector, string Body)> RulesInMedia(
        string css, Func<string, bool> headerMatches)
    {
        var source = StripComments(css);
        var index = 0;
        while (index < source.Length)
        {
            var open = source.IndexOf('{', index);
            if (open < 0) yield break;
            var end = MatchingBrace(source, open);
            var header = Collapse(source[index..open]);
            var body = source[(open + 1)..end];
            if (header.StartsWith('@') && headerMatches(header))
            {
                foreach (var nested in RulesIn(body)) yield return nested;
            }

            index = end + 1;
        }
    }

    // Every rule of the sheet, media queries included, flattened to `selector => body`.
    private static IEnumerable<(string Selector, string Body)> AllRules(string css) => RulesIn(StripComments(css));

    private static IEnumerable<(string Selector, string Body)> RulesIn(string source)
    {
        var index = 0;
        while (index < source.Length)
        {
            var open = source.IndexOf('{', index);
            if (open < 0) yield break;
            var end = MatchingBrace(source, open);
            var selector = Collapse(source[index..open]);
            var body = source[(open + 1)..end];
            if (selector.StartsWith('@'))
            {
                foreach (var nested in RulesIn(body)) yield return nested;
            }
            else
            {
                yield return (selector, body);
            }

            index = end + 1;
        }
    }

    private static string TopLevelRule(string css, string selector) =>
        TopLevelRules(css).Single(rule => rule.Selector == selector).Body;

    private static string[] SelectorParts(string selector) =>
        selector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Declaration(string body, string property)
    {
        var value = OptionalDeclaration(body, property);
        Assert.True(value is not null, $"The rule must declare {property}. Body: {Collapse(body)}");
        return value!;
    }

    private static string? OptionalDeclaration(string body, string property)
    {
        var match = Regex.Match(body, $@"(?:^|;)\s*{Regex.Escape(property)}\s*:\s*([^;}}]*)");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static int MatchingBrace(string source, int open)
    {
        var depth = 0;
        for (var index = open; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0) return index;
            }
        }

        throw new InvalidOperationException($"workup.css has an unbalanced block starting at offset {open}.");
    }

    private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static string StripComments(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    private static IEnumerable<string> EnumerateViews() =>
        Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot(), "WorkUp"), "*.cshtml", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"));

    private static string Relative(string file) =>
        Path.GetRelativePath(RepositoryRoot(), file).Replace('\\', '/');

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
