using System.Text.RegularExpressions;

namespace WorkUp.Tests.Accessibility;

// T27 D3 (public account reset): the new-password screen reached from a recovery link renders the server-side
// ModelState error of its two editable fields, yet before this slice neither field carried a programmatic link between
// the input and its own message: every <span asp-validation-for="..."> had no id and every input had
// aria-describedby=null and aria-invalid=null. A screen reader had no way to associate "Las contraseñas no coinciden."
// with the confirmation field, and nothing announced the invalid state.
//
// This file pins the source-level contract of the fix, copied from the proven Contact/Register/Login forms:
//   * every visible editable input points at its own validation container through aria-describedby;
//   * every container owns a stable id prefixed by its view, distinct from the id jquery.validate.js injects
//     (idOrName + "-error"), which would otherwise nest two elements sharing the same id;
//   * the input renders aria-invalid="true" only when that exact field has a server-side ModelState error, and the
//     attribute is omitted entirely (null) on the valid branch.
//
// The reset flow spends a single-use token on POST, so this slice touches nothing else: the hidden token, the labels,
// the alert summary, the anti-forgery field and the password semantics all stay exactly as they were.
//
// These are source-level assertions, not a rendered-HTML check: they prove the attributes Razor emits and the
// ModelState condition it binds, never the computed accessibility tree. What stays for the independent verifier on a
// fresh target: that the input tag helper preserves aria-describedby/aria-invalid in the emitted HTML, and that the
// jquery.validate.js label injected into the server-rendered container nests as a child of the distinct id (no
// duplicate id) and is announced through the aria-describedby link. No host, database or browser is launched here.
public sealed class AccountResetErrorAssociationTests
{
    private const string ResetView = "WorkUp/Views/ClientAccount/Reset.cshtml";

    // One row per visible editable field of the reset form. The expected id is the per-view prefix plus the field name,
    // deliberately different from jquery's "{field}-error".
    private static readonly (string Field, string ContainerId)[] Contract =
    [
        ("NewPassword", "account-reset-new-password-error"),
        ("ConfirmPassword", "account-reset-confirm-password-error"),
    ];

    // The condition the view must bind, verbatim modulo whitespace: it consults the entry of the field it names and
    // only claims "true" when the server recorded at least one error for that exact field.
    private const string ServerMarkedInvalidBody =
        "bool ServerMarkedInvalid(string field) => ViewData.ModelState.TryGetValue(field, out var entry) && entry.Errors.Count > 0;";

    public static TheoryData<string, string> EditableFields()
    {
        var data = new TheoryData<string, string>();
        foreach (var (field, containerId) in Contract)
            data.Add(field, containerId);

        return data;
    }

    [Theory]
    [MemberData(nameof(EditableFields))]
    public void The_field_links_its_input_to_its_own_container(string field, string containerId)
    {
        var source = ReadSource(ResetView);

        Assert.Contains($"aria-describedby=\"{containerId}\"", InputTag(source, field));
        Assert.Equal(containerId, ContainerId(source, field));

        // The association rides on top of the generated attributes, never instead of them.
        Assert.Contains($"<label asp-for=\"{field}\"", source);
        Assert.Contains($"asp-validation-for=\"{field}\"", source);
        Assert.Contains("class=\"field-error\"", source);
    }

    [Theory]
    [MemberData(nameof(EditableFields))]
    public void Aria_invalid_is_conditioned_on_that_fields_server_error(string field, string containerId)
    {
        var input = InputTag(ReadSource(ResetView), field);

        // Never hardcoded: a valid rerender has to keep the field out of the invalid state.
        Assert.DoesNotContain("aria-invalid=\"true\"", input);
        Assert.Contains($"aria-invalid=\"@(ServerMarkedInvalid(\"{field}\") ? \"true\" : null)\"", input);
        Assert.NotEqual($"{field}-error", containerId);
    }

    [Fact]
    public void The_container_ids_are_unique_and_cannot_collide_with_jquery()
    {
        var source = ReadSource(ResetView);
        var ids = Contract.Select(row => row.ContainerId).ToArray();

        // Neither id is jquery's injected "{field}-error", and the two fields never share one container.
        Assert.All(Contract, row => Assert.NotEqual($"{row.Field}-error", row.ContainerId));
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        // Declared exactly once, so the aria-describedby link is never ambiguous.
        Assert.All(ids, id => Assert.Single(Regex.Matches(source, $"id=\"{Regex.Escape(id)}\"").Cast<Match>()));
    }

    [Fact]
    public void Every_visible_editable_input_points_at_a_container_declared_once()
    {
        var source = ReadSource(ResetView);
        var inputs = EditableInputs(source).ToArray();

        Assert.Equal(Contract.Length, inputs.Length);
        foreach (var (field, tag) in inputs)
        {
            var describedBy = Regex.Match(tag, "aria-describedby=\"([^\"]+)\"");
            Assert.True(describedBy.Success, $"Reset: {field} debe declarar aria-describedby.");

            var target = describedBy.Groups[1].Value;
            Assert.Single(Regex.Matches(source, $"id=\"{Regex.Escape(target)}\"").Cast<Match>());
        }
    }

    [Fact]
    public void The_invalid_condition_reads_only_that_fields_server_errors()
    {
        // Business-level errors (a spent token) are stored under the empty key, so a condition scoped to the field it
        // names can never leak them onto a field: they stay in the summary.
        Assert.Contains(ServerMarkedInvalidBody, Collapse(ReadSource(ResetView)));
    }

    [Fact]
    public void The_hidden_token_summary_antiforgery_and_password_semantics_are_preserved()
    {
        var source = ReadSource(ResetView);
        var summary = TagContaining(source, "asp-validation-summary=");

        // The single-use token keeps travelling in the hidden field and the form keeps its alert summary and CSRF field.
        Assert.Contains("<input asp-for=\"Token\" type=\"hidden\" />", source);
        Assert.Contains("@Html.AntiForgeryToken()", source);
        Assert.Contains("asp-validation-summary=\"All\"", summary);
        Assert.Contains("role=\"alert\"", summary);

        // Password semantics are untouched: masked, new-password autocomplete and the same length bounds on both fields.
        foreach (var (field, _) in Contract)
        {
            var input = InputTag(source, field);
            Assert.Contains("type=\"password\"", input);
            Assert.Contains("autocomplete=\"new-password\"", input);
            Assert.Contains("minlength=\"8\"", input);
            Assert.Contains("maxlength=\"100\"", input);
            Assert.Contains("required", input);
        }

        Assert.Contains("<p class=\"hint\">Al menos 8 caracteres.</p>", source);
    }

    // --- Fixture -------------------------------------------------------------------------------------------------

    private static string InputTag(string source, string field) =>
        EditableInputs(source).Single(input => input.Field == field).Tag;

    // Enumerates the inputs the form lets a person edit: every <input asp-for="..."> that is not type="hidden".
    private static IEnumerable<(string Field, string Tag)> EditableInputs(string source)
    {
        foreach (Match match in Regex.Matches(source, "<input\\b[^>]*>"))
        {
            var tag = match.Value;
            var field = Regex.Match(tag, "asp-for=\"([^\"]+)\"");
            if (!field.Success || tag.Contains("type=\"hidden\"", StringComparison.Ordinal))
                continue;

            yield return (field.Groups[1].Value, tag);
        }
    }

    private static string ContainerId(string source, string field)
    {
        var span = TagContaining(source, $"asp-validation-for=\"{field}\"");
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

    private static string Collapse(string source) =>
        Regex.Replace(source, "\\s+", " ").Trim();

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    // Mirrors the view-source convention of the rest of the suite: the resolved root is the solution folder.
    private static string RepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}
