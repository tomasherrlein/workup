using System.Text.RegularExpressions;

namespace WorkUp.Tests.Accessibility;

// T27 reflow defect of the owner bottom bar, pinned at the source level.
//
// The browser observation behind this file: at a 360px viewport with a 32px root font (200% text, Arial fallback) the
// fixed `.bottom-bar` grid pushed the right edge of the "Clientes" and "Más" labels to 434.56px, well past the layout
// clientWidth of 360px, and no document scrollbar appeared. The cause is the track sizing: `repeat(4, 1fr)` is
// shorthand for `repeat(4, minmax(auto, 1fr))`, and the `auto` minimum is the track's min-content size. At text floor
// 16px the word "Clientes" fits inside its share; at 200% text its min-content is wider than the whole viewport, so
// the four tracks grow past the viewport instead of shrinking. The labels then have no wrapping opportunity at all, so
// they overflow rather than reflow.
//
// The fix is therefore split in two source-level facts, which is exactly what this file asserts:
//   * the four tracks must be `minmax(0, 1fr)`, a zero minimum that cannot be inflated by a label's min-content;
//   * the label spans must declare `overflow-wrap: anywhere`, which (unlike `break-word`) also lowers the label's
//     min-content contribution, so the span shrink-to-fits inside its track and the word wraps inside the bar.
// With both facts in place no `min-width: 0` on the anchors is required: a grid item only gets a content-based
// automatic minimum size when it spans a track whose min track sizing function is `auto`, and the fix removes exactly
// that condition.
//
// These are source-level assertions over `workup.css`, not a rendered-DOM check: they prove the declarations the
// stylesheet ships and the cascade position they live in (the base rules, not behind a breakpoint), never the computed
// box a browser resolves. The browser-side remainder is the independent verifier's work on a fresh target: measuring
// the four labels' client rects against `document.documentElement.clientWidth` at 360px/200% and confirming the labels
// wrap instead of overflowing. No browser and no database is launched here.
public sealed class BottomBarReflowTests
{
    [Fact]
    public void The_bottom_bar_divides_the_viewport_into_four_tracks_that_cannot_outgrow_it()
    {
        var body = TopLevelRule(Css(), ".bottom-bar");

        // `repeat(4, minmax(0, 1fr))`: the zero minimum is the whole fix for the track half of the defect. The
        // negative part of the assertion documents why a bare `1fr` (i.e. `minmax(auto, 1fr)`) is not acceptable
        // here: `auto` makes each track at least as wide as the widest unbreakable label.
        var columns = Declaration(body, "grid-template-columns");
        Assert.Matches(@"^repeat\(\s*4\s*,\s*minmax\(\s*0\s*,\s*1fr\s*\)\s*\)$", columns);
        Assert.DoesNotContain("minmax(auto", columns);
    }

    [Fact]
    public void The_bottom_bar_label_spans_can_break_inside_a_word_so_they_wrap_inside_their_track()
    {
        var css = Css();
        var labelRules = TopLevelRules(css)
            .Where(rule => SelectorParts(rule.Selector)
                .Any(selector => Regex.IsMatch(selector, @"^\.bottom-bar (?:a|button)\s*>\s*span")))
            .ToArray();

        // Exactly one rule owns the labels: both bottom bar controls (the four anchors and the "Más" button) and only
        // their text label -- the absolutely positioned .badge counter is a sibling span that must keep its own box.
        var labelRule = Assert.Single(labelRules);
        var selectorParts = SelectorParts(labelRule.Selector);
        Assert.Contains(selectorParts, selector => Regex.IsMatch(selector, @"^\.bottom-bar a > span:not\(\.badge\)$"));
        Assert.Contains(selectorParts, selector => Regex.IsMatch(selector, @"^\.bottom-bar button > span:not\(\.badge\)$"));

        // `anywhere` is load-bearing, not stylistic: it is the value that shrinks the label's min-content contribution
        // to a single character, so the span fits the track and the word breaks over two lines. `break-word` would wrap
        // the text too but would leave the min-content width untouched, so the shrink-to-fit width stays at the full
        // word and the owner bottom bar would still overflow at 200% text.
        Assert.Equal("anywhere", Declaration(labelRule.Body, "overflow-wrap"));
    }

    [Fact]
    public void No_bottom_bar_rule_truncates_a_label_with_an_ellipsis_or_nowrap()
    {
        var offenders = new List<string>();

        // The four labels must stay visible and readable at 200% text, so the last-resort desktop pattern (nowrap plus
        // ellipsis) is exactly what must not appear anywhere in the bottom bar cascade, including its media queries.
        foreach (var (selector, body) in AllRules(Css()))
        {
            if (!selector.Contains(".bottom-bar", StringComparison.Ordinal)) continue;
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

    [Fact]
    public void The_bottom_bar_targets_keep_their_touch_height()
    {
        var rule = TopLevelRules(Css()).Single(candidate =>
            SelectorParts(candidate.Selector).SequenceEqual([".bottom-bar a", ".bottom-bar button"]));

        // The wrap must not buy its room by shrinking the control: each of the four anchors and the button is the whole
        // keyboard target, and it keeps the mobile touch floor.
        Assert.Equal("64px", Declaration(rule.Body, "min-height"));
    }

    [Fact]
    public void The_current_page_variant_still_marks_the_active_tab()
    {
        var body = TopLevelRule(Css(), ".bottom-bar [aria-current=\"page\"]");

        // The reflow fix changes only sizing and wrapping; the active-tab variant keeps painting the pine label and the
        // 3px marker border the anchors already reserve as a transparent top border.
        Assert.Contains("var(--pine)", Declaration(body, "color"), StringComparison.Ordinal);
        Assert.Contains("var(--pine)", Declaration(body, "border-top-color"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_desktop_breakpoint_still_removes_the_bottom_bar()
    {
        // From 1024px up the rail replaces the bottom bar; a base-cascade wrap rule must not leak the bar back in.
        Assert.Contains(
            BlocksAfter(Css(), "@media (min-width: 1024px)"),
            body => Regex.IsMatch(body, @"\.bottom-bar[^{]*\{[^}]*display:\s*none"));
    }

    // --- Fixture -------------------------------------------------------------------------------------------------

    private static string Css() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "WorkUp", "wwwroot", "css", "workup.css"));

    // Top-level declarations only: at-rule blocks (media queries) are skipped whole, so what these lookups find is what
    // the bare cascade ships to a 360px viewport. A rule that only existed inside a media query would not match, which
    // is the property the defect needs: the wrap fix must not hide behind a breakpoint.
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
        var match = Regex.Match(body, $@"(?:^|;)\s*{Regex.Escape(property)}\s*:\s*([^;}}]*)");
        Assert.True(match.Success, $"The rule must declare {property}. Body: {Collapse(body)}");
        return match.Groups[1].Value.Trim();
    }

    // Bodies of every block that starts with `marker`, in file order, with nested blocks kept inside.
    private static IEnumerable<string> BlocksAfter(string css, string marker)
    {
        var source = StripComments(css);
        var search = 0;
        while (true)
        {
            var start = source.IndexOf(marker, search, StringComparison.Ordinal);
            if (start < 0) yield break;
            var open = source.IndexOf('{', start);
            if (open < 0) yield break;
            var end = MatchingBrace(source, open);
            yield return source[(open + 1)..end];
            search = end + 1;
        }
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

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
