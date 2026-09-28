using System.Text.RegularExpressions;

namespace WorkUp.Tests.Accessibility;

// T27 D3 (client-authenticated POST views): the individual booking screen (ClientReservations/Individual.cshtml) and
// the own-turn reprogramming screen (ClientAppointments/Reschedule.cshtml) each render a visible
// <select asp-for="Input.StartTime"> with its own field-level <span asp-validation-for="Input.StartTime">, but before
// this slice that span had no stable id and the select carried neither aria-describedby nor aria-invalid. A screen
// reader had no programmatic link between the chosen start and the server message, and nothing announced the invalid
// state after a refused save.
//
// This file pins the source-level contract of the fix, copied from the proven single-form account pages:
//   * the select points at its OWN validation container through aria-describedby;
//   * that container owns a stable, per-view id, distinct from the id jquery.validate.js injects (idOrName + "-error",
//     which for this field is "Input_StartTime-error"): a duplicate id would make the described-by link ambiguous;
//   * aria-invalid="true" is rendered only when the server recorded an error for the exact ModelState key the POST
//     binds, "Input.StartTime", and the attribute is omitted entirely (null) on the valid branch.
//
// The key stays prefixed on purpose: the controller records the error under "Input.StartTime" (the [Bind(Prefix =
// "Input")] shape), never under a bare "StartTime", so the condition has to read the key the server actually wrote.
// The GET filter form, the hidden Date/ServiceId/WorkerId selectors, the ModelOnly summary role="alert" and the T27 D1
// lift of the hidden Date error into that summary (Input.Date still has no span of its own) are all preserved: this
// slice only adds the association, it changes no flow and no controller.
//
// These are source-level assertions, not a rendered-HTML check: they prove the attributes Razor emits and the
// ModelState condition it binds, never the computed accessibility tree. What stays for the independent verifier on a
// real render: that the select tag helper preserves aria-describedby/aria-invalid in the emitted HTML, and that
// jquery.validate.js nests its injected "{id}-error" label as a child of the distinct container id (no duplicate id)
// and that it is announced through the aria-describedby link. Reaching these views over HTTP needs a client account
// (booking screen) and an own reschedulable turn (reprogramming screen), which is exactly why the automated contract
// here stays source level and launches no host, database or browser.
public sealed class ClientSlotErrorAssociationTests
{
    // The ModelState key the POST actually binds, prefixed by the [Bind(Prefix = "Input")] shape of the controller.
    private const string Field = "Input.StartTime";

    // jquery.validate.js injects its error label with idOrName + "-error". ASP.NET renders the select for
    // Input.StartTime with id="Input_StartTime", so the injected id is this exact string: the container must never use it.
    private const string JqueryErrorId = "Input_StartTime-error";

    // One row per view: the repository-relative source, the stable container id and the preserved visible label text.
    // Both ids are deliberately prefixed by the view and the field, and deliberately different from jquery's inject.
    private static readonly (string View, string ContainerId, string Label)[] Contract =
    [
        ("WorkUp/Views/ClientReservations/Individual.cshtml", "client-reserve-start-time-error", "Horario"),
        ("WorkUp/Views/ClientAppointments/Reschedule.cshtml", "client-reschedule-start-time-error", "Nuevo horario"),
    ];

    private static readonly string[] Views =
        Contract.Select(row => row.View).Distinct(StringComparer.Ordinal).ToArray();

    // The condition every view must bind, verbatim modulo whitespace: it consults the entry of the field it names and
    // only claims "true" when the server recorded at least one error for that exact field.
    private const string ServerMarkedInvalidBody =
        "bool ServerMarkedInvalid(string field) => ViewData.ModelState.TryGetValue(field, out var entry) && entry.Errors.Count > 0;";

    public static TheoryData<string, string, string> Slots()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (view, containerId, label) in Contract)
            data.Add(view, containerId, label);

        return data;
    }

    public static TheoryData<string> ViewRows()
    {
        var data = new TheoryData<string>();
        foreach (var view in Views)
            data.Add(view);

        return data;
    }

    [Theory]
    [MemberData(nameof(Slots))]
    public void The_select_links_its_own_container_through_aria_describedby(string view, string containerId, string label)
    {
        var source = ReadSource(view);
        var select = SelectTag(source, Field);

        Assert.Contains($"aria-describedby=\"{containerId}\"", Collapse(select));
        Assert.Equal(containerId, ContainerId(source, Field));

        // The association rides on top of the existing bindings, never instead of them: the label and the server span
        // stay exactly where they were.
        Assert.Contains($"<label asp-for=\"{Field}\">{label}</label>", source);
        Assert.Contains($"asp-validation-for=\"{Field}\"", source);
    }

    [Theory]
    [MemberData(nameof(ViewRows))]
    public void Aria_invalid_is_conditioned_only_on_the_start_time_server_error(string view)
    {
        var select = Collapse(SelectTag(ReadSource(view), Field));

        // Never hardcoded: a valid rerender has to omit the attribute entirely, so no false error is announced.
        Assert.DoesNotContain("aria-invalid=\"true\"", select);
        Assert.Contains($"aria-invalid=\"@(ServerMarkedInvalid(\"{Field}\") ? \"true\" : null)\"", select);
    }

    [Fact]
    public void The_container_ids_are_unique_per_view_and_never_collide_with_the_id_jquery_injects()
    {
        var ids = Contract.Select(row => row.ContainerId).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.NotEqual(JqueryErrorId, id));
        // Neither the prefixed nor the bare property suffix may be reused: jquery builds its injected id from the
        // generated id, which already carries the "Input_" prefix, so the container keeps its own view-scoped name.
        Assert.All(ids, id => Assert.NotEqual("StartTime-error", id));
    }

    [Fact]
    public void Each_container_id_is_declared_exactly_once_inside_its_own_view()
    {
        foreach (var (view, containerId, _) in Contract)
        {
            var source = ReadSource(view);

            // Declared exactly once, so the aria-describedby link is never ambiguous.
            Assert.Single(Regex.Matches(source, $"id=\"{Regex.Escape(containerId)}\"").Cast<Match>());
            Assert.Equal(containerId, ContainerId(source, Field));
        }
    }

    [Fact]
    public void The_invalid_condition_reads_only_the_prefixed_start_time_key()
    {
        foreach (var view in Views)
        {
            var source = Collapse(ReadSource(view));

            Assert.Contains(ServerMarkedInvalidBody, source);
            // The lookup is scoped to the exact key the POST binds, never to the bare property name the binder does
            // not use: a generic, model-level answer under the empty key can therefore never leak onto the select.
            Assert.DoesNotContain("ServerMarkedInvalid(\"StartTime\")", source);
        }
    }

    [Theory]
    [InlineData("WorkUp/Views/ClientReservations/Individual.cshtml",
        "action=\"/r/@Model.Slug/mis-turnos/reservar/turno\"", "Input.ServiceId,Input.WorkerId,Input.Date")]
    [InlineData("WorkUp/Views/ClientAppointments/Reschedule.cshtml",
        "action=\"/r/@Model.Slug/mis-turnos/@Model.BookingId/reprogramar\"", "Input.Date")]
    public void The_get_filter_form_hidden_selectors_summary_and_post_flow_are_preserved(
        string view, string action, string hiddenFieldsCsv)
    {
        var source = ReadSource(view);

        // One GET filter form and one POST form, both on the same canonical route, plus a single antiforgery token.
        Assert.Equal(1, Occurrences(source, "method=\"get\""));
        Assert.Equal(1, Occurrences(source, "method=\"post\""));
        Assert.Equal(1, Occurrences(source, "class=\"agenda-filters\""));
        Assert.Equal(2, Occurrences(source, action));
        Assert.Equal(1, Occurrences(source, "@Html.AntiForgeryToken()"));

        // The hidden selectors keep travelling in the POST body, never in the query string.
        foreach (var field in hiddenFieldsCsv.Split(','))
        {
            var hidden = TagContaining(source, $"asp-for=\"{field}\"");
            Assert.Contains("type=\"hidden\"", hidden);
        }

        // A hidden selector has no span of its own, which is exactly why the controller lifts its error into the
        // summary (T27 D1); that lift and its live region must stay as they are.
        Assert.DoesNotContain("asp-validation-for=\"Input.Date\"", source);
        var summary = TagContaining(source, "asp-validation-summary=");
        Assert.Contains("asp-validation-summary=\"ModelOnly\"", summary);
        Assert.Contains("role=\"alert\"", summary);
    }

    // --- Fixture -------------------------------------------------------------------------------------------------

    // The select the form lets a person edit, bound to the given field. The tag spans several lines, and the tag helper
    // only preserves the hand-written aria-* attributes when they travel inside the same tag.
    private static string SelectTag(string source, string field)
    {
        var select = Regex.Matches(source, "<select\\b[^>]*>")
            .Cast<Match>()
            .Select(match => match.Value)
            .SingleOrDefault(tag => tag.Contains($"asp-for=\"{field}\"", StringComparison.Ordinal));

        Assert.True(select is not null, $"Ningún select declara asp-for=\"{field}\".");
        return select!;
    }

    // The id of the validation span that carries the field's server message.
    private static string ContainerId(string source, string field)
    {
        var span = TagContaining(source, $"asp-validation-for=\"{field}\"");
        var id = Regex.Match(span, "id=\"([^\"]+)\"");
        Assert.True(id.Success, $"El contenedor de validación de {field} debe declarar un id.");

        return id.Groups[1].Value;
    }

    private static int Occurrences(string source, string literal) =>
        Regex.Matches(source, Regex.Escape(literal)).Count;

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

    private static string Collapse(string source) =>
        Regex.Replace(source, "\\s+", " ").Trim();

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    // Mirrors the view-source convention of the rest of the suite: the resolved root is the solution folder.
    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
