using System.Text.RegularExpressions;

namespace WorkUp.Tests.Accessibility;

// T27 D3 (real Chrome baseline): after an invalid public drop-in POST the server rendered the field error text for
// Email/FirstName/LastName and the alert summary, yet every <span asp-validation-for="..."> had no id and every input
// had aria-describedby=null and aria-invalid=null. A screen reader therefore had no programmatic link between the
// input and its own error message, and nothing told it the field was invalid.
//
// The drop-in form is special in one respect: ClassDropInService validates Email/FirstName/LastName and maps the
// rejected field onto its own control, so those three can carry a server-side invalid state. Phone reaches the model
// with no server or data-val validation at all: the view model leaves it unannotated, so the tag helper emits no
// data-val attributes and jquery.validate.js has no client message to inject. Its describedby target is still a span
// with a stable id, but that span stays intentionally empty: the link keeps the association uniform and would
// announce a future annotation without reshaping the markup, it never promises a client message today.
//
// This file pins the source-level contract of the fix, copied from the proven Contact/Register/Login/Reset forms:
//   * every visible editable input points at its own validation container through aria-describedby;
//   * every container owns a stable id prefixed by its view (dropin-*), distinct from the id jquery.validate.js
//     injects (idOrName + "-error"), which would otherwise nest two elements sharing the same id;
//   * the three server-validated inputs render aria-invalid="true" only when that exact field has a ModelState error,
//     and the attribute is omitted entirely (null) on the valid branch.
//
// The booking flow stays untouched: one form per open session, the hidden AppointmentId, the labels and the alert
// summary keep working exactly as before.
//
// These are source-level assertions, not a rendered-HTML check: they prove the attributes Razor emits and the
// ModelState condition it binds, never the computed accessibility tree. What stays for the independent verifier on a
// fresh target: that the input tag helper preserves aria-describedby/aria-invalid in the emitted HTML, and that the
// jquery.validate.js label injected into the server-rendered container nests as a child of the distinct id (no
// duplicate id) and is announced through the aria-describedby link. No host, database or browser is launched here.
public sealed class ClassDropInErrorAssociationTests
{
    private const string ClassesView = "WorkUp/Views/Reservations/Classes.cshtml";

    // One row per visible editable field of the single open drop-in form. The container id is the per-view prefix plus
    // the field name, deliberately different from jquery's "{field}-error". HasServerError mirrors the service
    // contract: Email/FirstName/LastName carry a rejected field, Phone does not.
    private static readonly (string Field, string ContainerId, bool HasServerError)[] Contract =
    [
        ("Email", "dropin-email-error", true),
        ("FirstName", "dropin-first-name-error", true),
        ("LastName", "dropin-last-name-error", true),
        ("Phone", "dropin-phone-error", false),
    ];

    // The condition the view must bind, verbatim: it consults the entry of the field it names and only claims "true"
    // when the server recorded at least one error for that exact field.
    private const string ServerMarkedInvalidBody =
        "bool ServerMarkedInvalid(string field) => ViewData.ModelState.TryGetValue(field, out var entry) && entry.Errors.Count > 0;";

    public static TheoryData<string, string> EditableFields()
    {
        var data = new TheoryData<string, string>();
        foreach (var (field, containerId, _) in Contract)
            data.Add(field, containerId);

        return data;
    }

    public static TheoryData<string, string> ServerValidatedFields()
    {
        var data = new TheoryData<string, string>();
        foreach (var (field, containerId, hasServerError) in Contract)
        {
            if (hasServerError)
                data.Add(field, containerId);
        }

        return data;
    }

    public static TheoryData<string> FieldNames()
    {
        var data = new TheoryData<string>();
        foreach (var (field, _, _) in Contract)
            data.Add(field);

        return data;
    }

    [Theory]
    [MemberData(nameof(EditableFields))]
    public void The_input_points_at_its_validation_container(string field, string containerId)
    {
        var view = ReadSource(ClassesView);

        Assert.Contains($"aria-describedby=\"{containerId}\"", InputTag(view, field));
        Assert.Equal(containerId, ContainerId(view, field));
    }

    [Fact]
    public void Every_visible_editable_input_is_associated_with_its_validation_container()
    {
        var view = ReadSource(ClassesView);
        var inputs = EditableInputs(view).ToArray();

        // The form exposes exactly the four editable fields the contract names; a new one must join the contract.
        Assert.Equal(Contract.Length, inputs.Length);
        foreach (var (field, tag) in inputs)
        {
            var describedBy = Regex.Match(tag, "aria-describedby=\"([^\"]+)\"");
            Assert.True(describedBy.Success, $"Drop-in: {field} debe declarar aria-describedby.");

            var target = describedBy.Groups[1].Value;
            Assert.Contains(Contract, row => row.ContainerId == target && row.Field == field);
            Assert.Single(Regex.Matches(view, $"id=\"{Regex.Escape(target)}\"").Cast<Match>());
        }
    }

    [Fact]
    public void The_container_ids_are_unique_and_cannot_collide_with_jquery()
    {
        var ids = Contract.Select(row => row.ContainerId).ToArray();

        // Neither id is jquery's injected "{field}-error", and no two fields ever share one container.
        Assert.All(Contract, row => Assert.NotEqual($"{row.Field}-error", row.ContainerId));
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(EditableFields))]
    public void Each_container_id_is_declared_exactly_once(string field, string containerId)
    {
        var view = ReadSource(ClassesView);

        // One form is open at a time, so the id is unique in the document and the aria-describedby link is never
        // ambiguous; a duplicate id anywhere in the view would break that.
        Assert.Single(Regex.Matches(view, $"id=\"{Regex.Escape(containerId)}\"").Cast<Match>());
        Assert.Equal(containerId, ContainerId(view, field));
    }

    [Theory]
    [MemberData(nameof(ServerValidatedFields))]
    public void Aria_invalid_is_conditioned_on_that_fields_server_error(string field, string containerId)
    {
        var input = InputTag(ReadSource(ClassesView), field);

        // Never hardcoded: a valid rerender has to keep the field out of the invalid state.
        Assert.DoesNotContain("aria-invalid=\"true\"", input);
        Assert.Contains($"aria-invalid=\"@(ServerMarkedInvalid(\"{field}\") ? \"true\" : null)\"", input);
        Assert.NotEqual($"{field}-error", containerId);
    }

    [Fact]
    public void The_phone_field_has_no_server_error_to_announce()
    {
        // Phone has neither a server rule nor a data-val rule (the view model leaves it unannotated), so there is no
        // message anywhere: the input must not claim a server-driven invalid state, and its describedby target is an
        // intentionally empty span rather than a promised jquery client message.
        var input = InputTag(ReadSource(ClassesView), "Phone");

        Assert.Contains("aria-describedby=\"dropin-phone-error\"", input);
        Assert.DoesNotContain("aria-invalid", input);
    }

    [Fact]
    public void Every_validation_container_declares_a_unique_stable_id()
    {
        // Triangulation: the association must not depend on the current four fields. Any span the view emits for a
        // field owns an id, and no two containers share one, so a future field cannot silently ship unassociated.
        var view = ReadSource(ClassesView);
        var ids = ValidationContainers(view).ToArray();

        Assert.NotEmpty(ids);
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_invalid_condition_reads_the_server_side_model_state()
    {
        // The condition is only meaningful if it consults the ModelState entry the server populated for the field.
        Assert.Contains(ServerMarkedInvalidBody, Collapse(ReadSource(ClassesView)));
    }

    [Fact]
    public void The_validation_summary_stays_an_alert()
    {
        var summary = TagContaining(ReadSource(ClassesView), "asp-validation-summary=");

        Assert.Contains("asp-validation-summary=\"All\"", summary);
        Assert.Contains("role=\"alert\"", summary);
    }

    [Theory]
    [MemberData(nameof(FieldNames))]
    public void The_generated_binding_attributes_are_preserved(string field)
    {
        var view = ReadSource(ClassesView);

        // The association is added on top of the existing asp-for / asp-validation-for / label wiring, never instead.
        Assert.Contains($"<label asp-for=\"{field}\"", view);
        Assert.Contains($"asp-for=\"{field}\"", view);
        Assert.Contains($"asp-validation-for=\"{field}\"", view);
    }

    [Fact]
    public void The_single_open_form_and_hidden_appointment_field_are_preserved()
    {
        var view = ReadSource(ClassesView);

        // One form at a time, still gated on the server-side selected session, still posting the chosen appointment
        // through the hidden field to the same action.
        Assert.Contains("Model.OpenAppointmentId == session.AppointmentId", view);
        Assert.Contains("<input type=\"hidden\" name=\"AppointmentId\" value=\"@session.AppointmentId\" />", view);
        Assert.Contains("asp-action=\"BookDropIn\"", view);
    }

    // --- Fixture -------------------------------------------------------------------------------------------------

    private static string InputTag(string view, string field) =>
        EditableInputs(view).Single(input => input.Field == field).Tag;

    // Enumerates the inputs the form lets a person edit: every <input asp-for="..."> that is not type="hidden".
    private static IEnumerable<(string Field, string Tag)> EditableInputs(string view)
    {
        foreach (Match match in Regex.Matches(view, "<input\\b[^>]*>"))
        {
            var tag = match.Value;
            var field = Regex.Match(tag, "asp-for=\"([^\"]+)\"");
            if (!field.Success || tag.Contains("type=\"hidden\"", StringComparison.Ordinal))
                continue;

            yield return (field.Groups[1].Value, tag);
        }
    }

    private static string ContainerId(string view, string field)
    {
        var span = TagContaining(view, $"asp-validation-for=\"{field}\"");
        var id = Regex.Match(span, "id=\"([^\"]+)\"");
        Assert.True(id.Success, $"El contenedor de validación de {field} debe declarar un id.");

        return id.Groups[1].Value;
    }

    // Reads the id of every server-rendered validation container, in document order.
    private static IEnumerable<string> ValidationContainers(string view)
    {
        foreach (Match match in Regex.Matches(view, "<span\\b[^>]*>"))
        {
            if (!match.Value.Contains("asp-validation-for=", StringComparison.Ordinal))
                continue;

            var id = Regex.Match(match.Value, "id=\"([^\"]+)\"");
            yield return id.Success ? id.Groups[1].Value : "";
        }
    }

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
