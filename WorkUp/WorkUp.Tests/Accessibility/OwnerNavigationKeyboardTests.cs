using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkUp.Tests.Accessibility;

// T27 keyboard slice for the owner navigation sheet. The sheet panel is declared `role="dialog" aria-modal="true"`
// in Views/Shared/_OwnerLayout.cshtml, so the rest of the page is inert while it is open: sequential focus navigation
// must not walk out of it, and closing it (Escape or a click on the backdrop) must return focus to the control that
// opened it. site.js opened the sheet, moved focus into it and handled Escape, but Tab and Shift+Tab were left to the
// browser, so from the last control of the panel the focus escaped into the page behind the modal.
//
// These cases do not read the JavaScript as text: they execute the shipped wwwroot/js/site.js file, unmodified, inside
// a tiny DOM mock built on Node.js built-ins (node:assert, node:fs, node:vm) and then drive it with real click and
// keydown events. The mock covers exactly the DOM surface site.js uses -- attributes, listeners with bubbling, focus,
// closest, querySelector(All) for tag/class/attribute selectors, and a getComputedStyle that resolves `display: none`
// for a [hidden] element or for an element the stylesheet hides. What it does NOT prove is anything that only a real
// browser can resolve: the actual CSS cascade of the 1024px media query, native Tab order of a real document, and
// assistive-technology behaviour. Those remain for the independent browser/DOM auditor.
//
// Node.js on PATH is a hard requirement of this file on purpose: without it the suite fails loudly instead of silently
// skipping the only executable proof of this keyboard behaviour. It runs the real interpreter, needs no package
// install, no network and no database.
public sealed class OwnerNavigationKeyboardTests
{
    // One executable scenario per case; the runner prints "OK <scenario>" and exits non-zero on any assertion failure.
    [Theory]
    [InlineData("open-moves-focus-into-panel")]
    [InlineData("tab-on-last-control-wraps-to-first")]
    [InlineData("shift-tab-on-first-control-wraps-to-last")]
    [InlineData("tab-on-middle-control-is-left-to-the-browser")]
    [InlineData("tab-from-background-pulls-focus-into-panel")]
    [InlineData("shift-tab-from-background-pulls-focus-into-panel")]
    [InlineData("single-control-panel-keeps-the-focus-on-it")]
    [InlineData("escape-closes-and-restores-its-own-opener")]
    [InlineData("tab-while-closed-is-not-hijacked")]
    [InlineData("backdrop-click-closes-and-restores-focus")]
    [InlineData("click-inside-panel-does-not-close")]
    [InlineData("sheet-without-controls-keeps-the-background-out")]
    [InlineData("sheet-hidden-by-the-desktop-media-query-is-not-a-modal")]
    public void The_owner_navigation_sheet_keeps_the_keyboard_inside_while_it_is_open(string scenario)
    {
        var result = RunScenario(scenario);

        if (result.ExitCode != 0 || !result.StdOut.Contains($"OK {scenario}", StringComparison.Ordinal))
        {
            Assert.Fail(
                $"""
                site.js failed the "{scenario}" scenario under the Node DOM mock.
                exit code: {result.ExitCode}
                stdout:
                {result.StdOut}
                stderr:
                {result.StdErr}
                """);
        }
    }

    [Fact]
    public void The_owner_layout_declares_the_sheet_as_a_hidden_modal_dialog()
    {
        var layout = File.ReadAllText(Path.Combine(RepositoryRoot(), "WorkUp", "Views", "Shared", "_OwnerLayout.cshtml"));

        // The trap is only correct because of these three facts of the markup: the sheet starts hidden, the panel claims
        // aria-modal="true" (the rest of the page is inert while open) and each opener advertises which sheet it toggles.
        var sheetTag = TagContaining(layout, "id=\"nav-sheet\"");
        Assert.Contains(" hidden", AttributesOf(sheetTag), StringComparison.Ordinal);

        var panelTag = TagContaining(layout, "role=\"dialog\"");
        Assert.Contains("aria-modal=\"true\"", AttributesOf(panelTag), StringComparison.Ordinal);

        var openers = Regex.Matches(layout, "<button[^>]*data-nav-open[^>]*>").Select(match => match.Value).ToArray();
        Assert.Equal(2, openers.Length);
        Assert.All(openers, opener =>
        {
            var attributes = AttributesOf(opener);
            Assert.Contains("aria-controls=\"nav-sheet\"", attributes, StringComparison.Ordinal);
            Assert.Contains("aria-expanded=\"false\"", attributes, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_desktop_breakpoint_still_hides_the_sheet_the_trap_stands_down_for()
    {
        var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "WorkUp", "wwwroot", "css", "workup.css"));

        // The `sheet-hidden-by-the-desktop-media-query-is-not-a-modal` scenario emulates exactly this rule: from 1024px up
        // the app bar, the bottom bar and the sheet are display:none and the rail takes over, so the trap must consult
        // the computed display of the sheet instead of trusting the `hidden` property alone.
        Assert.Contains(
            MediaQueryBodies(css, "@media (min-width: 1024px)"),
            body => Regex.IsMatch(body, @"\.nav-sheet[^{]*\{[^}]*display:\s*none"));
    }

    // --- Node driver -----------------------------------------------------------------------------------------------

    private static ScenarioResult RunScenario(string scenario)
    {
        var start = new ProcessStartInfo(NodeExecutablePath())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryRoot(),
        };
        start.ArgumentList.Add("--input-type=commonjs");
        start.ArgumentList.Add("-");
        start.Environment["WORKUP_NAV_SHEET_SITE_JS"] =
            Path.Combine(RepositoryRoot(), "WorkUp", "wwwroot", "js", "site.js");
        start.Environment["WORKUP_NAV_SHEET_SCENARIO"] = scenario;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(Harness);
        process.StandardInput.Close();

        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"The Node DOM mock for \"{scenario}\" did not finish within 60 seconds.");
        }

        return new ScenarioResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string NodeExecutablePath()
    {
        var executable = OperatingSystem.IsWindows() ? "node.exe" : "node";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), executable);
            if (File.Exists(candidate)) return candidate;
        }

        throw new InvalidOperationException(
            $"This regression executes the shipped site.js under Node.js and could not find {executable} on PATH. " +
            "Install a Node.js runtime (no packages, network or database are needed) to run it.");
    }

    private sealed record ScenarioResult(int ExitCode, string StdOut, string StdErr);

    // --- Fixture ---------------------------------------------------------------------------------------------------

    private static string AttributesOf(string tag)
    {
        var end = tag.IndexOf('>');
        Assert.True(end > 0, $"The tag must close: {tag}");
        return tag[..end];
    }

    private static string TagContaining(string source, string marker)
    {
        var index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"The source must contain {marker}.");
        var start = source.LastIndexOf('<', index);
        Assert.True(start >= 0, $"The marker {marker} must be inside a tag.");
        var end = source.IndexOf('>', index);
        Assert.True(end > start, $"The tag containing {marker} must close.");
        return source[start..(end + 1)];
    }

    // Bodies of every block that starts with `query`, in file order: the stylesheet has several 1024px media queries and
    // only one of them hides the sheet.
    private static IEnumerable<string> MediaQueryBodies(string css, string query)
    {
        var source = StripComments(css);
        var search = 0;
        var start = source.IndexOf(query, search, StringComparison.Ordinal);
        Assert.True(start >= 0, $"workup.css must declare the {query} media query.");
        while (start >= 0)
        {
            var open = source.IndexOf('{', start);
            Assert.True(open > start, $"The {query} media query must open a block.");
            var depth = 0;
            var text = new StringBuilder();
            for (var index = open; index < source.Length; index++)
            {
                if (source[index] == '{') depth++;
                if (source[index] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        yield return text.ToString();
                        break;
                    }
                }

                text.Append(source[index]);
            }

            Assert.True(depth == 0, $"Unbalanced braces after {query}.");
            search = start + query.Length;
            start = source.IndexOf(query, search, StringComparison.Ordinal);
        }
    }

    private static string StripComments(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    // --- DOM mock and scenarios executed by Node -------------------------------------------------------------------
    // A deliberately tiny DOM: only the surface site.js touches. Selector support is limited to tag, .class and
    // [attribute]/[attribute='value'] in a comma list, and an unsupported selector throws instead of silently
    // returning nothing, so the mock fails loudly if site.js starts using a selector it cannot resolve.
    private const string Harness = """
        "use strict";
        const assert = require("node:assert/strict");
        const fs = require("node:fs");
        const vm = require("node:vm");

        const siteJsPath = process.env.WORKUP_NAV_SHEET_SITE_JS;
        // Elements the stylesheet hides, i.e. the sheet under the 1024px media query.
        const hiddenByMediaQuery = new Set();
        let activeDocument = null;

        function matchesSimple(selector, element) {
          const parsed = /^([a-zA-Z]+)?((?:\.[\w-]+)*)((?:\[[^\]]+\])*)$/.exec(selector);
          if (!parsed) throw new Error("DOM mock cannot match selector: " + selector);
          if (parsed[1] && element.tagName !== parsed[1].toUpperCase()) return false;
          const required = parsed[2] ? parsed[2].split(".").filter(Boolean) : [];
          const present = (element.getAttribute("class") || "").split(/\s+/).filter(Boolean);
          if (!required.every((name) => present.includes(name))) return false;
          for (const attr of parsed[3] ? parsed[3].match(/\[[^\]]+\]/g) : []) {
            const inner = attr.slice(1, -1);
            const eq = inner.indexOf("=");
            if (eq === -1) {
              if (!element.hasAttribute(inner.trim())) return false;
              continue;
            }
            const name = inner.slice(0, eq).trim();
            const value = inner.slice(eq + 1).trim().replace(/^['"]|['"]$/g, "");
            if (element.getAttribute(name) !== value) return false;
          }
          return true;
        }

        function matches(selector, element) {
          return selector.split(",").some((part) => matchesSimple(part.trim(), element));
        }

        function el(tag, attrs) {
          const element = {
            tagName: tag.toUpperCase(),
            attributes: new Map(Object.entries(attrs || {})),
            children: [],
            parent: null,
            hidden: false,
            value: "",
            textContent: "",
            dataset: {},
            listeners: new Map(),
            setAttribute(name, value) { this.attributes.set(name, String(value)); },
            getAttribute(name) { return this.attributes.has(name) ? this.attributes.get(name) : null; },
            hasAttribute(name) { return this.attributes.has(name); },
            append(...nodes) { for (const node of nodes) { node.parent = this; this.children.push(node); } return this; },
            addEventListener(type, handler) {
              const list = this.listeners.get(type) || [];
              list.push(handler);
              this.listeners.set(type, list);
            },
            dispatch(event) {
              if (!event.target) event.target = this;
              for (const handler of this.listeners.get(event.type) || []) handler(event);
              if (this.parent) this.parent.dispatch(event);
            },
            focus() { activeDocument.activeElement = this; },
            click() {
              this.dispatch({ type: "click", target: this, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } });
            },
            closest(selector) {
              for (let node = this; node; node = node.parent) if (matches(selector, node)) return node;
              return null;
            },
            querySelectorAll(selector) {
              const found = [];
              const walk = (node) => {
                for (const child of node.children) {
                  if (matches(selector, child)) found.push(child);
                  walk(child);
                }
              };
              walk(this);
              return found;
            },
            querySelector(selector) { return this.querySelectorAll(selector)[0] || null; },
          };
          Object.defineProperty(element, "id", { get() { return this.getAttribute("id") || ""; } });
          return element;
        }

        // The owner shell of _OwnerLayout.cshtml: page behind the sheet, the two openers ([data-nav-open]), the hidden
        // sheet wrapper and the panel with its dialog semantics. `background` and `backgroundAfter` stand for the page a
        // Tab could escape into; the disabled control stands for a control no keyboard can reach.
        function buildFixture(options) {
          const doc = el("#document");
          doc.getElementById = (id) => doc.querySelectorAll("[id]").find((node) => node.id === id) || null;
          activeDocument = doc;
          const fixture = {
            doc,
            background: el("a", { href: "/owner/day" }),
            openerMenu: el("button", { "data-nav-open": "", "aria-controls": "nav-sheet", "aria-expanded": "false" }),
            openerMore: el("button", { "data-nav-open": "", "aria-controls": "nav-sheet", "aria-expanded": "false" }),
            backgroundAfter: el("a", { href: "/owner/reports" }),
            sheet: el("div", { id: "nav-sheet" }),
            panel: el("div", { role: "dialog", "aria-modal": "true", "aria-label": "Menu" }),
            first: el("a", { href: "/owner/day" }),
            middle: el("a", { href: "/owner/clients" }),
            disabled: el("button", { disabled: "" }),
            last: el("button", { type: "submit" }),
          };
          fixture.sheet.hidden = true;
          if (options && options.noFocusableControls) fixture.panel.append(el("p"));
          else if (options && options.singleControl) fixture.panel.append(fixture.first);
          else fixture.panel.append(fixture.first, fixture.middle, fixture.disabled, fixture.last);
          fixture.sheet.append(fixture.panel);
          doc.append(el("body").append(fixture.background, fixture.openerMenu, fixture.openerMore, fixture.backgroundAfter, fixture.sheet));
          return fixture;
        }

        // Loads the shipped site.js, untouched, in a fresh context so every scenario starts from a clean set of listeners.
        function start(fixture) {
          vm.runInNewContext(fs.readFileSync(siteJsPath, "utf8"), {
            document: fixture.doc,
            window: {
              getComputedStyle: (element) => ({
                display: hiddenByMediaQuery.has(element) || element.hidden ? "none" : "block",
              }),
              setTimeout: () => {},
            },
            navigator: { clipboard: { writeText: async () => {} } },
            setTimeout: () => {},
          }, { filename: siteJsPath });
          return fixture;
        }

        function pressKey(fix, key, shiftKey) {
          const event = {
            type: "keydown",
            key,
            shiftKey: !!shiftKey,
            target: fix.doc.activeElement,
            defaultPrevented: false,
            preventDefault() { this.defaultPrevented = true; },
          };
          fix.doc.dispatch(event);
          return event;
        }

        const scenarios = {
          "open-moves-focus-into-panel"() {
            const f = start(buildFixture());
            f.openerMenu.click();
            assert.equal(f.sheet.hidden, false);
            assert.equal(f.openerMenu.getAttribute("aria-expanded"), "true");
            assert.equal(f.doc.activeElement, f.first);
          },
          "tab-on-last-control-wraps-to-first"() {
            const f = start(buildFixture());
            f.openerMenu.click();
            f.last.focus();
            const event = pressKey(f, "Tab", false);
            assert.equal(event.defaultPrevented, true, "Tab on the last control must be handled by the sheet");
            assert.equal(f.doc.activeElement, f.first);
          },
          "shift-tab-on-first-control-wraps-to-last"() {
            const f = start(buildFixture());
            f.openerMenu.click();
            assert.equal(f.doc.activeElement, f.first);
            const event = pressKey(f, "Tab", true);
            assert.equal(event.defaultPrevented, true, "Shift+Tab on the first control must wrap");
            assert.equal(f.doc.activeElement, f.last, "the disabled control must be skipped");
          },
          "tab-on-middle-control-is-left-to-the-browser"() {
            const f = start(buildFixture());
            f.openerMenu.click();
            f.middle.focus();
            const event = pressKey(f, "Tab", false);
            assert.equal(event.defaultPrevented, false, "a Tab that stays inside the panel is left to the browser");
            assert.equal(f.doc.activeElement, f.middle);
          },
          "tab-from-background-pulls-focus-into-panel"() {
            const f = start(buildFixture());
            f.openerMenu.click();
            f.background.focus();
            const event = pressKey(f, "Tab", false);
            assert.equal(event.defaultPrevented, true, "the page behind the modal must not keep the tab move");
            assert.equal(f.doc.activeElement, f.first);
          },
          "shift-tab-from-background-pulls-focus-into-panel"() {
            const f = start(buildFixture());
            f.openerMenu.click();
            f.background.focus();
            const event = pressKey(f, "Tab", true);
            assert.equal(event.defaultPrevented, true, "the page behind the modal must not keep the backward move");
            assert.equal(f.doc.activeElement, f.last);
          },
          "single-control-panel-keeps-the-focus-on-it"() {
            const f = start(buildFixture({ singleControl: true }));
            f.openerMenu.click();
            assert.equal(f.doc.activeElement, f.first);
            assert.equal(pressKey(f, "Tab", false).defaultPrevented, true);
            assert.equal(f.doc.activeElement, f.first, "the only control is both the first and the last one");
            assert.equal(pressKey(f, "Tab", true).defaultPrevented, true);
            assert.equal(f.doc.activeElement, f.first);
          },
          "escape-closes-and-restores-its-own-opener"() {
            const f = start(buildFixture());
            f.openerMore.click();
            assert.equal(f.doc.activeElement, f.first);
            pressKey(f, "Escape", false);
            assert.equal(f.sheet.hidden, true);
            assert.equal(f.openerMore.getAttribute("aria-expanded"), "false");
            assert.equal(f.doc.activeElement, f.openerMore, "Escape must restore focus to the opener that opened it");
          },
          "tab-while-closed-is-not-hijacked"() {
            const f = start(buildFixture());
            f.background.focus();
            const event = pressKey(f, "Tab", false);
            assert.equal(event.defaultPrevented, false, "a closed sheet must not touch tabbing at all");
            assert.equal(f.doc.activeElement, f.background);
          },
          "backdrop-click-closes-and-restores-focus"() {
            const f = start(buildFixture());
            f.openerMenu.click();
            f.sheet.click();
            assert.equal(f.sheet.hidden, true);
            assert.equal(f.doc.activeElement, f.openerMenu);
          },
          "click-inside-panel-does-not-close"() {
            const f = start(buildFixture());
            f.openerMenu.click();
            f.first.click();
            assert.equal(f.sheet.hidden, false);
          },
          "sheet-without-controls-keeps-the-background-out"() {
            const f = start(buildFixture({ noFocusableControls: true }));
            f.openerMenu.click();
            assert.equal(f.sheet.hidden, false);
            f.background.focus();
            const event = pressKey(f, "Tab", false);
            assert.equal(event.defaultPrevented, true, "with nothing to focus inside, the tab move must be cancelled");
            assert.equal(f.doc.activeElement, f.background);
          },
          "sheet-hidden-by-the-desktop-media-query-is-not-a-modal"() {
            const f = start(buildFixture());
            f.openerMenu.click();
            hiddenByMediaQuery.add(f.sheet);
            f.last.focus();
            const event = pressKey(f, "Tab", false);
            assert.equal(event.defaultPrevented, false, "the desktop layout must keep normal tabbing");
            assert.equal(f.doc.activeElement, f.last);
          },
        };

        const only = process.env.WORKUP_NAV_SHEET_SCENARIO;
        let failures = 0;
        for (const scenario of only ? [only] : Object.keys(scenarios)) {
          if (!scenarios[scenario]) {
            console.log("UNKNOWN " + scenario);
            failures++;
            continue;
          }
          try {
            scenarios[scenario]();
            console.log("OK " + scenario);
          } catch (error) {
            failures++;
            console.log("FAIL " + scenario + " :: " + String(error.message).split("\n")[0]);
          }
        }
        process.exit(failures === 0 ? 0 : 1);
        """;
}
