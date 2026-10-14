using System.Text.RegularExpressions;

namespace WorkUp.Tests.Accessibility;

// T27 D3 (public account form): the client self-registration form (RF-25) renders five visible editable fields, and
// before this slice none of them carried a programmatic link between the input and its own error message. This file
// pins the source-level contract copied from the proven Contact form fix:
//   * every visible editable input points at its own validation container through aria-describedby;
//   * every container owns a stable, unique id that cannot collide with the id jquery.validate.js injects
//     (idOrName + "-error"), which would otherwise nest two elements sharing the same id;
//   * the input renders aria-invalid="true" only when that exact field has a server-side ModelState error, and the
//     attribute is omitted entirely (null) on the valid branch.
//
// These are source-level assertions, not a rendered-HTML check: they prove the attributes the Razor view emits and the
// ModelState condition it binds, never the computed accessibility tree. What stays for the independent verifier on a
// fresh target: that the input tag helper preserves aria-describedby/aria-invalid in the emitted HTML, and that the
// jquery.validate.js label injected into the server-rendered container nests as a child of the distinct id (no
// duplicate id) and is announced through the aria-describedby link. No host, database or browser is launched here.
public sealed class AccountRegisterErrorAssociationTests
{
    private const string RegisterView = "WorkUp/Views/ClientAccount/Register.cshtml";

    // The visible editable fields the public registration form renders today. The controller posts exactly these five,
    // so any visible editable input without an association is a gap, not a model change.
    private static readonly string[] EditableFields =
        ["FirstName", "LastName", "Email", "Password", "ConfirmPassword"];

    [Theory]
    [InlineData("FirstName")]
    [InlineData("LastName")]
    [InlineData("Email")]
    [InlineData("Password")]
    [InlineData("ConfirmPassword")]
    public void The_input_points_at_its_validation_container(string field)
    {
        var view = ReadSource(RegisterView);
        var containerId = ContainerId(view, field);

        Assert.Contains($"aria-describedby=\"{containerId}\"", InputTag(view, field));
    }

    [Fact]
    public void Every_visible_editable_input_is_associated_with_its_validation_container()
    {
        var view = ReadSource(RegisterView);
        var unassociated = EditableInputs(view)
            .Where(input => !Regex.IsMatch(input.Tag, "aria-describedby=\"[^\"]+\""))
            .Select(input => input.Field)
            .ToArray();

        Assert.Empty(unassociated);
    }

    [Fact]
    public void The_validation_containers_have_unique_ids()
    {
        var view = ReadSource(RegisterView);
        var ids = EditableFields.Select(field => ContainerId(view, field)).ToArray();

        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("FirstName")]
    [InlineData("LastName")]
    [InlineData("Email")]
    [InlineData("Password")]
    [InlineData("ConfirmPassword")]
    public void The_container_id_cannot_collide_with_the_id_jquery_validate_injects(string field)
    {
        var containerId = ContainerId(ReadSource(RegisterView), field);

        // jquery.validate.js creates the injected error element as id = idOrName + "-error"; reusing that exact id on
        // the server-rendered container would nest two elements with the same id.
        Assert.NotEqual($"{field}-error", containerId);
    }

    [Theory]
    [InlineData("FirstName")]
    [InlineData("LastName")]
    [InlineData("Email")]
    [InlineData("Password")]
    [InlineData("ConfirmPassword")]
    public void Each_container_id_is_declared_exactly_once(string field)
    {
        var view = ReadSource(RegisterView);
        var containerId = ContainerId(view, field);

        // A duplicate id anywhere in the view would make the aria-describedby link ambiguous.
        Assert.Single(Regex.Matches(view, $"id=\"{Regex.Escape(containerId)}\"").Cast<Match>());
    }

    [Theory]
    [InlineData("FirstName")]
    [InlineData("LastName")]
    [InlineData("Email")]
    [InlineData("Password")]
    [InlineData("ConfirmPassword")]
    public void Aria_invalid_is_conditioned_on_that_fields_server_error(string field)
    {
        var view = ReadSource(RegisterView);
        var input = InputTag(view, field);

        // The attribute must never be hardcoded: a valid rerender has to keep the field out of the invalid state.
        Assert.DoesNotContain("aria-invalid=\"true\"", input);
        Assert.Contains("aria-invalid=\"@(", input);
        // The condition reads the field's own server error and renders "true" or nothing (null) respectively.
        Assert.Contains($"ServerMarkedInvalid(\"{field}\")", input);
        Assert.Contains("? \"true\" : null", input);
    }

    [Fact]
    public void The_invalid_condition_reads_the_server_side_model_state()
    {
        // The condition is only meaningful if it consults the ModelState entry the server populated for the field.
        Assert.Contains("ViewData.ModelState.TryGetValue", ReadSource(RegisterView));
    }

    [Fact]
    public void The_validation_summary_stays_an_alert()
    {
        var summary = TagContaining(ReadSource(RegisterView), "asp-validation-summary=");

        Assert.Contains("role=\"alert\"", summary);
    }

    [Theory]
    [InlineData("FirstName")]
    [InlineData("LastName")]
    [InlineData("Email")]
    [InlineData("Password")]
    [InlineData("ConfirmPassword")]
    public void The_generated_binding_attributes_are_preserved(string field)
    {
        var view = ReadSource(RegisterView);

        // The association is added on top of the existing asp-for / asp-validation-for / label wiring, never instead.
        Assert.Contains($"<label asp-for=\"{field}\"", view);
        Assert.Contains($"asp-for=\"{field}\"", view);
        Assert.Contains($"asp-validation-for=\"{field}\"", view);
        Assert.Contains($"class=\"field-error\"", view);
    }

    [Fact]
    public void The_hidden_antiforgery_field_is_preserved()
    {
        // The form never edits hidden state, but the anti-forgery token it already posts must stay.
        Assert.Contains("@Html.AntiForgeryToken()", ReadSource(RegisterView));
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

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    // Mirrors the view-source convention of the rest of the suite: the resolved root is the solution folder.
    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
