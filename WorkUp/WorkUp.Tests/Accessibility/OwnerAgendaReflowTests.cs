using System.Text.RegularExpressions;

namespace WorkUp.Tests.Accessibility;

// T27 reflow defect of the owner Agenda screen (`/panel/Agenda`), pinned at the source level.
//
// The browser observation behind this file, measured on a real Chrome at 200% text (a 32px root font) and at the
// 16px text floor:
//   * At 360px the document scrolled sideways: `document.documentElement.scrollWidth` was 528 against a
//     `clientWidth` of 345 (+183). The list that carries the date navigation ("Anterior", "Siguiente",
//     "Ver por semana") is `display: flex` with a gap and no `flex-wrap`, so the links were forced onto a single
//     line whose min-content width is wider than the page.
//   * At 320px the same list pushed the leaf item "Ver por semana" to a right edge of 338px against a 305px
//     clientWidth (+33).
//   * Separately, at 360px/200% the agenda row's first column is the fixed `64px` time track of
//     `grid-template-columns: 64px 1fr`. The `<time>` box measured `scrollWidth` 90 against `clientWidth` 64, so the
//     time label painted outside its own cell even though the row box itself still fit the viewport.
//
// The fix this file pins has two halves, both CSS-only:
//   * `.nav-inline` declares `flex-wrap: wrap`, so each link moves to the next line instead of overflowing the page.
//     No `white-space: nowrap`, no ellipsis and no `overflow: hidden` is introduced, so every label stays fully
//     visible and the links keep their hit area.
//   * The agenda row stacks at narrow width: inside a `max-width: 480px` block `.row` becomes the single full-width
//     track `minmax(0, 1fr)`, so the time gets the whole row width above the detail instead of squeezing into a
//     fixed 64px column. The DOM order of `.row-time` and `.row-main` is untouched, so sequential focus order and the
//     screen-reader reading order are unchanged. That narrow block is declared *before* the `.row-flat` and
//     `.supply-row` variants on purpose: those are single-class rules with the same specificity, so keeping them
//     later in the sheet preserves their own track sizing, and settings/supplies rows keep their desktop shape.
//
// These are source-level assertions over `workup.css`, not a rendered-DOM check: they prove the declarations the
// stylesheet ships and the cascade position they live in, never the computed box a browser resolves. The
// browser-side remainder is the independent verifier's work on a fresh target: measuring
// `document.documentElement.scrollWidth` against `clientWidth` at 360px/200% and 320px on `/panel/Agenda`, and
// confirming the "Ver por semana" item and the row `<time>` both stay inside the viewport. No browser and no
// database is launched here.
public sealed class OwnerAgendaReflowTests
{
    // --- Date navigation list ------------------------------------------------------------------------------------

    [Fact]
    public void The_period_navigation_list_wraps_instead_of_forcing_every_link_onto_one_line()
    {
        var body = TopLevelRule(Css(), ".nav-inline");

        // `flex-wrap: wrap` is the load-bearing declaration of the navigation half of the defect: with the default
        // `nowrap` the three links ("Anterior", "Siguiente", "Ver por semana") cannot move to a second line, so at
        // 320px the last one painted at a 338px right edge inside a 305px viewport. Wrapping keeps each link inside
        // the content box; the list itself is not hidden or clipped to buy the room.
        Assert.Equal("wrap", Declaration(body, "flex-wrap"));
    }

    [Fact]
    public void No_nav_inline_rule_clips_a_link_with_an_ellipsis_nowrap_or_hidden_overflow()
    {
        var offenders = new List<string>();

        // The reflow must keep every navigation label fully readable: the last-resort desktop pattern (nowrap plus
        // ellipsis) and `overflow: hidden` are exactly what must not appear anywhere in the navigation cascade,
        // media queries included.
        foreach (var (selector, body) in AllRules(Css()))
        {
            if (!selector.Contains(".nav-inline", StringComparison.Ordinal)) continue;
            if (Regex.IsMatch(body, @"(?:^|;)\s*text-overflow\s*:\s*ellipsis"))
            {
                offenders.Add($"{selector} => text-overflow: ellipsis");
            }

            if (Regex.IsMatch(body, @"(?:^|;)\s*white-space\s*:\s*nowrap"))
            {
                offenders.Add($"{selector} => white-space: nowrap");
            }

            if (Regex.IsMatch(body, @"(?:^|;)\s*overflow\s*:\s*hidden"))
            {
                offenders.Add($"{selector} => overflow: hidden");
            }
        }

        Assert.Empty(offenders);
    }

    // --- Agenda row: desktop contract unchanged ------------------------------------------------------------------

    [Fact]
    public void The_agenda_row_keeps_its_two_column_tracks_on_desktop()
    {
        var body = TopLevelRule(Css(), ".row");

        // The base cascade keeps the original `64px 1fr` tracks and must not carry the narrow stack override, which
        // lives only inside the `max-width: 480px` block.
        Assert.Equal("64px 1fr", Declaration(body, "grid-template-columns"));
        Assert.DoesNotContain("minmax", body, StringComparison.Ordinal);
    }

    // --- Agenda row: narrow stack --------------------------------------------------------------------------------

    [Fact]
    public void The_agenda_row_stacks_time_above_detail_at_narrow_width()
    {
        var columns = NarrowRowTrack();

        // A single `minmax(0, 1fr)` track: the zero minimum is the load-bearing half of the fix, because it cannot be
        // inflated by the time label's min-content the way the fixed `64px` track was. One full-width column is what
        // puts the time above the detail instead of inside a 64px cell that is narrower than the text.
        Assert.Equal("minmax(0, 1fr)", columns);
    }

    [Fact]
    public void The_narrow_stack_is_declared_before_the_row_variants_so_they_keep_their_own_tracks()
    {
        var source = StripComments(Css());
        var narrowBlockStart = NarrowMediaBlockStart();

        // `.row-flat` and `.supply-row` are single-class rules just like `.row`, so if the narrow override were
        // declared after them it would win the tie on source order and silently restyle settings and supplies rows.
        // The block therefore sits earlier in the sheet, and these offsets are the regression guard for that.
        Assert.True(
            narrowBlockStart < source.IndexOf(".row-flat", StringComparison.Ordinal),
            "The narrow `.row` stack must be declared before `.row-flat`.");
        Assert.True(
            narrowBlockStart < source.IndexOf(".supply-row", StringComparison.Ordinal),
            "The narrow `.row` stack must be declared before `.supply-row`.");

        // ... and both variants still ship the track sizing that keeps them out of the agenda reflow.
        Assert.Equal("1fr", Declaration(TopLevelRule(Css(), ".row-flat"), "grid-template-columns"));
        Assert.Equal("1fr auto", Declaration(TopLevelRule(Css(), ".supply-row"), "grid-template-columns"));
    }

    [Fact]
    public void The_narrow_stack_only_targets_the_row_itself()
    {
        // The narrow block must not grow a reach into the variants: only `.row` may be re-tracked inside it.
        var reach = RulesIn(NarrowMediaBody())
            .Where(rule => Regex.IsMatch(rule.Body, @"(?:^|;)\s*grid-template-columns\s*:"))
            .Select(rule => rule.Selector)
            .ToArray();

        Assert.Equal([".row"], reach);
    }

    // --- The time label is never hidden or truncated -------------------------------------------------------------

    [Fact]
    public void No_row_rule_hides_or_truncates_the_time_label()
    {
        var offenders = new List<string>();

        // The whole point of the defect is that the time must stay visible and readable at 200% text, so neither the
        // last-resort truncation pattern nor `display: none` may appear anywhere in the row cascade.
        foreach (var (selector, body) in AllRules(Css()))
        {
            if (!selector.Contains(".row", StringComparison.Ordinal)) continue;
            if (Regex.IsMatch(body, @"(?:^|;)\s*text-overflow\s*:\s*ellipsis"))
            {
                offenders.Add($"{selector} => text-overflow: ellipsis");
            }

            if (Regex.IsMatch(body, @"(?:^|;)\s*white-space\s*:\s*nowrap"))
            {
                offenders.Add($"{selector} => white-space: nowrap");
            }

            if (Regex.IsMatch(body, @"(?:^|;)\s*overflow\s*:\s*hidden"))
            {
                offenders.Add($"{selector} => overflow: hidden");
            }

            if (Regex.IsMatch(body, @"(?:^|;)\s*display\s*:\s*none"))
            {
                offenders.Add($"{selector} => display: none");
            }

            if (Regex.IsMatch(body, @"(?:^|;)\s*visibility\s*:\s*hidden"))
            {
                offenders.Add($"{selector} => visibility: hidden");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_row_action_strip_still_wraps_so_targets_never_collapse_into_one_line()
    {
        var body = TopLevelRule(Css(), ".row-actions");

        // The row reflow changes track sizing only: the action strip keeps `flex-wrap: wrap`, so the "Ver detalle del
        // turno" control keeps its own line and its 52px `min-height` touch target instead of being squeezed away.
        Assert.Equal("flex", Declaration(body, "display"));
        Assert.Equal("wrap", Declaration(body, "flex-wrap"));
    }

    // --- DOM order that carries focus and reading order ----------------------------------------------------------

    [Fact]
    public void The_agenda_row_partial_keeps_the_time_before_the_detail()
    {
        var row = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "WorkUp", "Areas", "Owner", "Views", "Agenda", "_AgendaRow.cshtml"));

        // The reflow is CSS-only: the time stays first in DOM order, so a visual stack keeps the same sequential focus
        // order and the same screen-reader reading order as the desktop two-column row.
        var timeIndex = row.IndexOf("class=\"row-time\"", StringComparison.Ordinal);
        var mainIndex = row.IndexOf("class=\"row-main\"", StringComparison.Ordinal);
        Assert.True(timeIndex >= 0, "_AgendaRow.cshtml must keep the .row-time cell.");
        Assert.True(mainIndex >= 0, "_AgendaRow.cshtml must keep the .row-main cell.");
        Assert.True(timeIndex < mainIndex, "The .row-time cell must stay before .row-main in DOM order.");
    }

    // --- Fixture -------------------------------------------------------------------------------------------------

    private static string Css() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "WorkUp", "wwwroot", "css", "workup.css"));

    // The `grid-template-columns` value `.row` receives inside the narrow (`max-width: 480px`) reflow block.
    private static string NarrowRowTrack()
    {
        var values = RulesIn(NarrowMediaBody())
            .Where(rule => SelectorParts(rule.Selector).Contains(".row"))
            .Select(rule => OptionalDeclaration(rule.Body, "grid-template-columns"))
            .Where(value => value is not null)
            .ToArray();
        return Assert.Single(values)!;
    }

    private static int NarrowMediaBlockStart() => NarrowMediaBlock().Start;

    private static string NarrowMediaBody() => NarrowMediaBlock().Body;

    private static (int Start, string Header, string Body) NarrowMediaBlock() =>
        MediaBlocks(Css()).Single(block =>
            Regex.IsMatch(block.Header, @"max-width\s*:\s*480px")
            && RulesIn(block.Body).Any(rule =>
                SelectorParts(rule.Selector).Contains(".row")
                && OptionalDeclaration(rule.Body, "grid-template-columns") is not null));

    // Top-level declarations only: at-rule blocks (media queries) are skipped whole, so what this finds is what the
    // bare cascade ships to a wide viewport. The narrow override must not appear here.
    private static IReadOnlyList<(int Start, string Selector, string Body)> TopLevelRules(string css)
    {
        var source = StripComments(css);
        var rules = new List<(int Start, string Selector, string Body)>();
        var index = 0;
        while (index < source.Length)
        {
            var open = source.IndexOf('{', index);
            if (open < 0) break;
            var end = MatchingBrace(source, open);
            var selector = Collapse(source[index..open]);
            if (!selector.StartsWith('@'))
            {
                rules.Add((index, selector, source[(open + 1)..end]));
            }

            index = end + 1;
        }

        return rules;
    }

    private static IReadOnlyList<(int Start, string Header, string Body)> MediaBlocks(string css)
    {
        var source = StripComments(css);
        var blocks = new List<(int Start, string Header, string Body)>();
        var index = 0;
        while (index < source.Length)
        {
            var open = source.IndexOf('{', index);
            if (open < 0) break;
            var end = MatchingBrace(source, open);
            var header = Collapse(source[index..open]);
            if (header.StartsWith('@'))
            {
                blocks.Add((index, header, source[(open + 1)..end]));
            }

            index = end + 1;
        }

        return blocks;
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

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
