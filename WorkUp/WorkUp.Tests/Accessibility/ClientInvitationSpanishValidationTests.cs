using System.ComponentModel.DataAnnotations;
using System.Reflection;
using WorkUp.ViewModels.ClientAccounts;

namespace WorkUp.Tests.Accessibility;

/// <summary>
/// T27 D3 (owner-assisted invitation, RF-26): <see cref="ClientInvitationViewModel.Password"/> was a non-nullable
/// string with no DataAnnotations, so an empty password on the public acceptance POST made the MVC implicit-required
/// binder record the English <c>The Password field is required.</c> while <c>ValidateInvitationForm</c> independently
/// added the Spanish <c>Ingresá tu contraseña.</c> under the same <c>Password</c> key. The alert summary and the
/// per-field span therefore showed the same rule twice, once in English. The fix declares the field with
/// <c>[Required(ErrorMessage = "Ingresá tu contraseña.")]</c>, the same copy the sibling forms already use
/// (<see cref="ClientLoginViewModel.Password"/>), so the binder records the final Spanish message before the action
/// runs. The controller-level half of the contract -- the form judgement never adding a second copy for a field the
/// binder already rejected -- is pinned in <c>ClientInvitationPublicControllerTests</c>, next to the invitation fixture
/// it needs.
/// <para>
/// These tests run DataAnnotations exactly as the MVC binder does for an empty value. Neither test launches a host, a
/// database or a browser; the rendered-HTML regression stays in the opt-in T22 HTTP fixture.
/// </para>
/// </summary>
public sealed class ClientInvitationSpanishValidationTests
{
    // Kept in one place on purpose: this is the exact copy the model annotation and the controller must share, so the
    // duplicated English rule of the implicit-required binder can never come back.
    private const string PasswordRequired = "Ingresá tu contraseña.";

    [Fact]
    public void An_empty_password_validates_with_the_spanish_required_message()
    {
        var model = ExistingAccountForm();
        model.Password = "";

        var messages = MessagesFor(model, nameof(ClientInvitationViewModel.Password));

        Assert.Equal(new[] { PasswordRequired }, messages);
        Assert.DoesNotContain("The Password field is required.", messages);
    }

    [Fact]
    public void The_password_annotation_carries_the_same_message_the_controller_adds_and_refuses_an_empty_string()
    {
        var required = typeof(ClientInvitationViewModel)
            .GetProperty(nameof(ClientInvitationViewModel.Password))!
            .GetCustomAttribute<RequiredAttribute>();

        Assert.NotNull(required);
        Assert.Equal(PasswordRequired, required!.ErrorMessage);
        // The default treats "" as missing; the form judgement guards on IsNullOrEmpty, so the two agree on what an
        // unsubmitted password is.
        Assert.False(required.AllowEmptyStrings);
    }

    [Fact]
    public void A_password_that_satisfies_the_form_validates_without_messages_in_either_mode()
    {
        // The annotation must not add a rule of its own beyond "not empty": the new-account minimum and the
        // confirmation stay with the service and the form judgement.
        foreach (var mode in new[] { ClientInvitationModes.NewAccount, ClientInvitationModes.ExistingAccount })
        {
            var model = ExistingAccountForm();
            model.Mode = mode;

            Assert.Empty(MessagesFor(model, nameof(ClientInvitationViewModel.Password)));
        }
    }

    [Fact]
    public void The_confirmation_stays_optional_so_the_existing_account_mode_never_asks_for_it()
    {
        var model = ExistingAccountForm();
        model.ConfirmPassword = null;

        Assert.Empty(MessagesFor(model, nameof(ClientInvitationViewModel.ConfirmPassword)));
    }

    // --- Fixture -------------------------------------------------------------------------------------------------

    private static ClientInvitationViewModel ExistingAccountForm() => new()
    {
        Token = "token-invitacion",
        Mode = ClientInvitationModes.ExistingAccount,
        Password = "clave-segura"
    };

    // Runs the same DataAnnotations validation the MVC binder runs, all properties included.
    private static IReadOnlyList<string> MessagesFor(ClientInvitationViewModel model, string member)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results
            .Where(result => result.MemberNames.Contains(member, StringComparer.Ordinal))
            .Select(result => result.ErrorMessage ?? "")
            .ToArray();
    }
}
