using System.Text.RegularExpressions;

namespace WorkUp.Tests.Accessibility;

// T27 bounded accessibility cut (source-provable slice). The action controls of the public client panel and the owner
// agenda must all carry the same base button class so they share sizing, padding and no-underline behaviour; the
// variant class (btn-primary / btn-destructive) only paints colours on top of that base. A variant without the base
// renders as a bare inline anchor (no padding, no min-height, underlined link text) or as a browser-default button,
// which is exactly the inconsistency this file pins down. The owner shared-clock warning is the same story on the
// notice side: notice-warning without the notice base loses the flex layout, border and padding of every other notice
// on the panel.
//
// These are source-level assertions, not a rendered-HTML check: they prove the class attributes the Razor views emit,
// never the computed style a browser resolves. The real HTTP/DOM smoke with a database is a separate opt-in harness and
// is deliberately not recreated here.
public sealed class ActionStylesTests
{
    private static readonly string[] ButtonVariants = ["btn-primary", "btn-destructive"];

    private static readonly string[] NoticeVariants =
        ["notice-info", "notice-success", "notice-warning", "notice-error"];

    // --- Representative critical public anchors and buttons ------------------------------------------------------

    [Fact]
    public void The_public_agenda_entry_anchor_uses_the_btn_base_class()
    {
        var view = ReadClientAppointmentsView("Index.cshtml");

        // The "Reservar un turno" entry is always visible, even on an empty agenda: it is the first action the panel
        // offers, so it can never be the unstyled variant.
        Assert.Equal("btn btn-primary", ClassOf(TagContaining(view, "mis-turnos/reservar/turno\"")));
    }

    [Fact]
    public void The_client_reschedule_screen_uses_the_btn_base_class_on_its_actions()
    {
        var view = ReadClientAppointmentsView("Reschedule.cshtml");

        // The nearest-alternative anchor and the confirm submit are the two actions of the empty and full branches.
        Assert.Equal("btn btn-primary", ClassOf(TagContaining(view, "?date=@DateOnly.FromDateTime(nearest)")));
        Assert.Equal("btn btn-primary", ClassOf(TagContaining(view, "Confirmar reprogramaci")));
    }

    [Fact]
    public void The_client_individual_reservation_screen_uses_the_btn_base_class_on_its_actions()
    {
        var view = ReadClientReservationsView("Individual.cshtml");

        Assert.Equal("btn btn-primary", ClassOf(TagContaining(view, "reservar/turno?serviceId=")));
        Assert.Equal("btn btn-primary", ClassOf(TagContaining(view, "Confirmar reserva")));
    }

    // --- Representative critical owner agenda anchors and buttons ------------------------------------------------

    [Fact]
    public void The_owner_agenda_entry_anchor_uses_the_btn_base_class()
    {
        var view = ReadOwnerAgendaView("Index.cshtml");

        Assert.Equal("btn btn-primary", ClassOf(TagContaining(view, "asp-action=\"NewBooking\"")));
    }

    [Fact]
    public void The_owner_new_booking_screen_uses_the_btn_base_class_on_its_actions()
    {
        var view = ReadOwnerAgendaView("NewBooking.cshtml");

        Assert.Equal("btn btn-primary", ClassOf(TagContaining(view, "asp-route-startTime=\"@nearestTime")));
        Assert.Equal("btn btn-primary", ClassOf(TagContaining(view, "Reservar turno")));
    }

    [Fact]
    public void The_owner_reschedule_screen_uses_the_btn_base_class_on_its_actions()
    {
        var view = ReadOwnerAgendaView("Reschedule.cshtml");

        Assert.Equal("btn btn-primary", ClassOf(TagContaining(view, "asp-route-date=\"@DateOnly.FromDateTime(nearest)")));
        Assert.Equal("btn btn-primary", ClassOf(TagContaining(view, "Confirmar reprogramaci")));
    }

    [Fact]
    public void The_owner_cancel_action_uses_the_btn_base_class_with_its_destructive_variant()
    {
        var view = ReadOwnerAgendaView("Detail.cshtml");

        // The destructive variant owns the colour and border; without the base it is a browser-default button again.
        Assert.Equal("btn btn-destructive", ClassOf(TagContaining(view, "Cancelar turno")));
    }

    // --- Owner shared-clock warning ------------------------------------------------------------------------------

    [Fact]
    public void The_owner_clock_warning_uses_the_notice_base_class()
    {
        var view = ReadOwnerDevToolsView("Index.cshtml");

        // The clock warning is the only notice on the panel; role="note" is on its own div, so the tag is the notice box.
        Assert.Equal("notice notice-warning", ClassOf(TagContaining(view, "role=\"note\"")));
    }

    // --- Whole-repository guards ---------------------------------------------------------------------------------

    [Fact]
    public void No_view_renders_a_button_variant_without_the_btn_base_class()
    {
        var offenders = new List<string>();

        foreach (var file in EnumerateViews())
        {
            foreach (var classes in ClassAttributes(File.ReadAllText(file)))
            {
                var tokens = tokensOf(classes);
                if (ButtonVariants.Any(tokens.Contains) && !tokens.Contains("btn"))
                {
                    offenders.Add($"{Relative(file)} => class=\"{classes}\"");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_view_renders_the_shared_clock_warning_variant_without_the_notice_base_class()
    {
        var view = ReadOwnerDevToolsView("Index.cshtml");

        // Any notice variant on the clock panel must keep the notice base: it is the widest, most prominent warning of
        // the demo panel and it must keep the same box as every other notice.
        var offenders = ClassAttributes(view)
            .Where(classes => NoticeVariants.Any(tokensOf(classes).Contains) && !tokensOf(classes).Contains("notice"))
            .ToArray();

        Assert.Empty(offenders);
    }

    // --- Fixture -------------------------------------------------------------------------------------------------

    private static string[] tokensOf(string classes) =>
        classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static IEnumerable<string> ClassAttributes(string source) =>
        Regex.Matches(source, "class=\"([^\"]*)\"").Select(match => match.Groups[1].Value);

    private static string TagContaining(string source, string marker)
    {
        var index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"La fuente debe contener {marker}.");
        var start = source.LastIndexOf('<', index);
        Assert.True(start >= 0, $"El marcador {marker} debe estar dentro de una etiqueta.");
        var end = source.IndexOf('>', index);
        Assert.True(end > start, $"La etiqueta que contiene {marker} debe cerrar.");
        return source[start..(end + 1)];
    }

    private static string ClassOf(string tag)
    {
        var match = Regex.Match(tag, "class=\"([^\"]*)\"");
        Assert.True(match.Success, $"La etiqueta debe declarar class: {tag}");
        return match.Groups[1].Value;
    }

    private static IEnumerable<string> EnumerateViews() =>
        Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot(), "WorkUp"), "*.cshtml", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"));

    private static string Relative(string file) =>
        Path.GetRelativePath(RepositoryRoot(), file).Replace('\\', '/');

    private static string ReadClientAppointmentsView(string fileName) =>
        ReadSource("WorkUp", "Views", "ClientAppointments", fileName);

    private static string ReadClientReservationsView(string fileName) =>
        ReadSource("WorkUp", "Views", "ClientReservations", fileName);

    private static string ReadOwnerAgendaView(string fileName) =>
        ReadSource("WorkUp", "Areas", "Owner", "Views", "Agenda", fileName);

    private static string ReadOwnerDevToolsView(string fileName) =>
        ReadSource("WorkUp", "Areas", "Owner", "Views", "DevTools", fileName);

    // Mirrors the view-source convention of the rest of the suite: the resolved root is the solution folder, so every
    // reader appends the "WorkUp" project segment itself.
    private static string ReadSource(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. segments]));

    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
