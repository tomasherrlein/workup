using System.Text.RegularExpressions;

namespace WorkUp.Tests.Accessibility;

// T27 D3 (owner-assisted invitation): the acceptance page renders TWO sibling forms -- "crear mi cuenta" (new account)
// and "ya tengo una cuenta" (existing account) -- and both post the SAME ModelState key, "Password". Before this slice
// every password field used asp-validation-for="Password", so a server error recorded for the new-account mode (its
// min-length rule, or a password mismatch on ConfirmPassword) rendered the identical message BENEATH BOTH forms, even
// though the existing-account mode never asks for a repetition and never applies the new-password minimum. A screen
// reader also had no programmatic link between a password input and its own message.
//
// This file pins the source-level contract of the fix:
//   * exactly one form is "active" per render, selected from the whitelisted mode: an unknown mode defaults to the
//     new-account form (the only one the server validates), so new-account fields stay active when the mode is unknown;
//   * each password input points at its OWN container through aria-describedby -- new-password, confirm-password and
//     existing-password -- whose ids are distinct from the id jquery.validate.js injects ("{field}-error");
//   * aria-invalid="true" is rendered only for the ACTIVE form's field error, so the inactive form never claims a
//     server-driven invalid state it does not own;
//   * the active field uses asp-validation-for (the server message), while the inactive field renders a manually empty
//     span that still carries data-valmsg-for / data-valmsg-replace so jquery unobtrusive can inject a CLIENT error
//     into whichever form the person actually submits.
//
// Because each field is written as an @if/@else pair of MUTUALLY EXCLUSIVE Razor branches, the same container id text
// appears TWICE in the source while only one of the two spans is ever emitted into the document. This file therefore
// never proves id uniqueness with a naive source occurrence count: it proves the exact guarded branch structure, and
// uniqueness of the rendered document stays with the independent verifier (a real rendered-HTML check), together with
// the tag helper preserving aria-describedby / aria-invalid as written. No host, database or browser is launched here.
public sealed class ClientInvitationErrorAssociationTests
{
    private const string InvitationView = "WorkUp/Views/ClientAccount/Invitation.cshtml";

    // One row per password field of the page: two in the new-account form, one in the existing-account form. The
    // container id is prefixed by the view and the field, deliberately different from jquery's "{field}-error".
    private static readonly (string Form, string Field, string ContainerId)[] Contract =
    [
        ("NewAccount", "Password", "account-invitation-new-password-error"),
        ("NewAccount", "ConfirmPassword", "account-invitation-confirm-password-error"),
        ("ExistingAccount", "Password", "account-invitation-existing-password-error"),
    ];

    // The condition the view must bind, verbatim modulo whitespace: it consults the entry of the field it names and
    // only claims "true" when the server recorded at least one error for that exact field.
    private const string ServerMarkedInvalidBody =
        "bool ServerMarkedInvalid(string field) => ViewData.ModelState.TryGetValue(field, out var entry) && entry.Errors.Count > 0;";

    public static TheoryData<string, string, string> PasswordFields()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (form, field, containerId) in Contract)
            data.Add(form, field, containerId);

        return data;
    }

    [Fact]
    public void The_active_form_is_selected_by_the_whitelisted_mode_so_an_unknown_mode_keeps_the_new_account_form()
    {
        var view = ReadSource(InvitationView);

        Assert.Contains("var activeExisting = Model.Mode == ClientInvitationModes.ExistingAccount;", view);

        // The new-account fields are active exactly when the mode is NOT the existing-account one, so an unknown mode
        // (which the server refuses) still shows its password error under the new-account form.
        Assert.Contains("@if (!activeExisting)", view);
        Assert.Contains("@if (activeExisting)", view);
        Assert.Contains(ServerMarkedInvalidBody, Collapse(view));
    }

    [Theory]
    [MemberData(nameof(PasswordFields))]
    public void The_input_links_its_container_and_scopes_aria_invalid_to_the_active_form(
        string form, string field, string containerId)
    {
        var view = ReadSource(InvitationView);
        var input = InputFor(view, containerId);

        Assert.Contains($"aria-describedby=\"{containerId}\"", input);

        // Never hardcoded: on the valid branch the expression is null, so the attribute is not rendered at all.
        Assert.DoesNotContain("aria-invalid=\"true\"", input);
        Assert.Contains(
            $"aria-invalid=\"@({Guard(form)} && ServerMarkedInvalid(\"{field}\") ? \"true\" : null)\"",
            input);

        // The password is never echoed as plain text.
        Assert.Contains("type=\"password\"", input);
    }

    [Theory]
    [MemberData(nameof(PasswordFields))]
    public void Each_field_renders_both_mutually_exclusive_branches_sharing_one_container_id(
        string form, string field, string containerId)
    {
        var view = ReadSource(InvitationView);

        // The ACTIVE branch renders the server message with asp-validation-for; the INACTIVE branch renders a manually
        // empty span that keeps data-valmsg-for / data-valmsg-replace so jquery unobtrusive can still target it.
        var expected = BranchBlock(Guard(form), field, containerId);
        Assert.Contains(Collapse(expected), Collapse(view));

        // Exactly one span per branch: two occurrences in source, never both emitted because @if/@else are exclusive.
        Assert.Equal(2, Occurrences(view, $"id=\"{containerId}\""));
    }

    [Theory]
    [MemberData(nameof(PasswordFields))]
    public void The_inactive_branch_renders_no_server_error(string form, string field, string containerId)
    {
        var view = ReadSource(InvitationView);
        var inactive = InactiveBranch(Guard(form), field, containerId);

        // The inactive span must not carry asp-validation-for: that is exactly what leaked the new-account server error
        // under the existing-account form before this fix.
        Assert.DoesNotContain("asp-validation-for", inactive);
        Assert.Contains($"data-valmsg-for=\"{field}\"", inactive);
        Assert.Contains("data-valmsg-replace=\"true\"", inactive);
        Assert.Equal("", InnerText(inactive));
    }

    [Fact]
    public void The_container_ids_are_distinct_and_cannot_collide_with_the_id_jquery_validate_injects()
    {
        var ids = Contract.Select(row => row.ContainerId).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(Contract, row => Assert.NotEqual($"{row.Field}-error", row.ContainerId));
    }

    [Fact]
    public void Both_password_inputs_keep_distinct_ids_so_the_two_forms_never_collide()
    {
        var view = ReadSource(InvitationView);
        var newInput = InputFor(view, "account-invitation-new-password-error");
        var existingInput = InputFor(view, "account-invitation-existing-password-error");

        // Both inputs bind Password. The new-account input lets the tag helper derive id="Password"; the existing-account
        // input overrides the generated id with its own, exactly as the hidden token field does in the same form.
        Assert.Contains("asp-for=\"Password\"", newInput);
        Assert.Contains("asp-for=\"Password\"", existingInput);

        var newId = EffectiveInputId(newInput, "Password");
        var existingId = EffectiveInputId(existingInput, "Password");
        Assert.Equal("Password", newId);
        Assert.Equal("ExistingPassword", existingId);
        Assert.NotEqual(newId, existingId);
    }

    [Fact]
    public void The_two_forms_csrf_tokens_hidden_tokens_modes_and_labels_are_preserved()
    {
        var view = ReadSource(InvitationView);

        // Two sibling forms, one anti-forgery token each.
        Assert.Equal(2, Occurrences(view, "@Html.AntiForgeryToken()"));

        // The single-use token travels back in a hidden field per form, with a distinct id, never in a query string.
        Assert.Contains("id=\"NewInvitationToken\"", view);
        Assert.Contains("id=\"ExistingInvitationToken\"", view);

        // The mode is a server-side whitelist: the body only picks one of the two values the form offers.
        Assert.Contains("name=\"Mode\" value=\"@ClientInvitationModes.NewAccount\"", view);
        Assert.Contains("name=\"Mode\" value=\"@ClientInvitationModes.ExistingAccount\"", view);

        // The labels stay exactly as they were.
        Assert.Contains("<label asp-for=\"Password\">Contraseña</label>", view);
        Assert.Contains("<label asp-for=\"ConfirmPassword\">Repetí la contraseña</label>", view);
        Assert.Contains("for=\"ExistingPassword\"", view);
    }

    [Fact]
    public void The_shared_validation_summary_stays_one_alert()
    {
        var view = ReadSource(InvitationView);

        Assert.Equal(1, Occurrences(view, "asp-validation-summary=\"All\""));
        var summary = TagContaining(view, "asp-validation-summary=");
        Assert.Contains("role=\"alert\"", summary);
    }

    [Fact]
    public void The_new_account_minimum_and_password_no_echo_are_preserved()
    {
        var view = ReadSource(InvitationView);

        // All three password inputs stay no-echo.
        var passwordInputs = Regex.Matches(view, "<input\\b[^>]*type=\"password\"[^>]*>")
            .Cast<Match>()
            .Select(match => match.Value)
            .ToArray();
        Assert.Equal(3, passwordInputs.Length);

        // The 8-character minimum is a new-account rule only: the existing-account password keeps proving itself with
        // no repetition and no new-password minimum.
        Assert.Contains("minlength=\"8\"", InputFor(view, "account-invitation-new-password-error"));
        Assert.Contains("minlength=\"8\"", InputFor(view, "account-invitation-confirm-password-error"));
        Assert.DoesNotContain("minlength", InputFor(view, "account-invitation-existing-password-error"));
    }

    // --- Fixture -------------------------------------------------------------------------------------------------

    // The guard that keeps a field honest: existing-account fields are active only in the existing mode, and
    // new-account fields in every other mode (including the unknown one the server refuses).
    private static string Guard(string form) => form == "ExistingAccount" ? "activeExisting" : "!activeExisting";

    private static string BranchBlock(string guard, string field, string containerId) =>
        $"@if ({guard}) {{ {ActiveSpan(field, containerId)} }} else {{ {InactiveSpan(field, containerId)} }}";

    private static string ActiveSpan(string field, string containerId) =>
        $"<span asp-validation-for=\"{field}\" id=\"{containerId}\" class=\"field-error\"></span>";

    private static string InactiveSpan(string field, string containerId) =>
        $"<span id=\"{containerId}\" data-valmsg-for=\"{field}\" data-valmsg-replace=\"true\" class=\"field-error\"></span>";

    private static string InactiveBranch(string guard, string field, string containerId) =>
        BranchBlock(guard, field, containerId).Split("else", StringSplitOptions.None)[1];

    private static string InnerText(string spanElement)
    {
        var open = spanElement.IndexOf('>');
        var close = spanElement.LastIndexOf("</span>", StringComparison.Ordinal);
        return open >= 0 && close > open ? spanElement[(open + 1)..close].Trim() : spanElement;
    }

    private static string InputFor(string view, string containerId)
    {
        var input = Regex.Matches(view, "<input\\b[^>]*>")
            .Cast<Match>()
            .Select(match => match.Value)
            .SingleOrDefault(tag => tag.Contains($"aria-describedby=\"{containerId}\"", StringComparison.Ordinal));

        Assert.True(input is not null, $"Ningún input declara aria-describedby=\"{containerId}\".");
        return input!;
    }

    // The effective id the tag helper emits: an explicit id wins, otherwise ASP.NET derives it from the asp-for name.
    private static string EffectiveInputId(string tag, string fallback)
    {
        var id = Regex.Match(tag, "\\bid=\"([^\"]+)\"");
        return id.Success ? id.Groups[1].Value : fallback;
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
