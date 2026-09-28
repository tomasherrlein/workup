using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using WorkUp.BusinessLogic.Catalog;
using WorkUp.BusinessLogic.Classes;
using WorkUp.BusinessLogic.Scheduling;
using WorkUp.BusinessLogic.Staff;
using WorkUp.Controllers;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;
using WorkUp.Tests.Fakes;
using WorkUp.ViewModels.Reservations;

namespace WorkUp.Tests.Accessibility;

/// <summary>
/// T27 (real T12 opt-in observation): an empty <c>FirstName</c> on the public drop-in POST rendered the English
/// implicit-required message <c>The FirstName field is required.</c> because <see cref="PublicDropInListViewModel"/>
/// carried no DataAnnotations. That binder message is recorded before the action runs and <c>ClassDropInService</c>
/// returns its own Spanish <c>Ingresá tu nombre.</c>, so the alert summary showed both. The fix declares the three
/// contact fields with Spanish DataAnnotations: the required and the nombre/apellido length messages match the
/// service's own copy, while the over-long email message is this application's length-specific convention, because
/// <c>ClassDropInService</c> returns the generic <c>Ingresá un email válido.</c> for both over-long and malformed
/// emails. <c>BookDropIn</c> then stops adding the service error for a field the binder already rejected, while
/// still mapping a business error on any other field (<c>AppointmentId</c>, for instance).
/// <para>
/// The model tests run DataAnnotations exactly as the MVC binder does for an empty/invalid value. The controller test
/// drives the real <c>BookDropIn</c> action with the fakes the rest of the suite uses, seeding the ModelState entry a
/// binder would have recorded, and proves the service error is not added twice. Neither test launches a host, a
/// database or a browser; the rendered-HTML end-to-end regression stays in the opt-in T12 HTTP fixture.
/// </para>
/// </summary>
public sealed class DropInSpanishValidationTests
{
    // The required and nombre/apellido length messages are the service's own (ClassDropInService.ValidateRequest);
    // EmailTooLong is this view model's own length-specific convention, since the service collapses an over-long and
    // a malformed email into the same generic invalid-email message.
    private const string FirstNameRequired = "Ingresá tu nombre.";
    private const string LastNameRequired = "Ingresá tu apellido.";
    private const string EmailRequired = "Ingresá tu email.";
    private const string EmailInvalid = "Ingresá un email válido.";
    private const string FirstNameTooLong = "El nombre no puede superar los 60 caracteres.";
    private const string LastNameTooLong = "El apellido no puede superar los 60 caracteres.";
    private const string EmailTooLong = "El email no puede superar los 254 caracteres.";

    public static TheoryData<string, string> RequiredFields() => new()
    {
        { nameof(PublicDropInListViewModel.FirstName), FirstNameRequired },
        { nameof(PublicDropInListViewModel.LastName), LastNameRequired },
        { nameof(PublicDropInListViewModel.Email), EmailRequired },
    };

    public static TheoryData<string, string, string> OverlongFields() => new()
    {
        { nameof(PublicDropInListViewModel.FirstName), new string('a', 61), FirstNameTooLong },
        { nameof(PublicDropInListViewModel.LastName), new string('a', 61), LastNameTooLong },
        { nameof(PublicDropInListViewModel.Email), new string('a', 250) + "@example.com", EmailTooLong },
    };

    [Theory]
    [MemberData(nameof(RequiredFields))]
    public void An_empty_required_drop_in_field_validates_with_the_aligned_spanish_message(string member, string expected)
    {
        var model = Valid();
        Clear(model, member);

        var messages = MessagesFor(model, member);

        // The English implicit-required copy must never come back, and the Spanish service message must be the one
        // the binder records, so the field already has its final message before the action runs.
        Assert.Contains(expected, messages);
        Assert.DoesNotContain($"The {member} field is required.", messages);
    }

    [Theory]
    [InlineData("sin-arroba")]
    [InlineData("@ejemplo.com")]
    public void A_malformed_email_validates_with_the_aligned_spanish_message(string email)
    {
        var model = Valid();
        model.Email = email;

        Assert.Contains(EmailInvalid, MessagesFor(model, nameof(PublicDropInListViewModel.Email)));
    }

    [Theory]
    [MemberData(nameof(OverlongFields))]
    public void An_overlong_drop_in_field_validates_with_the_aligned_spanish_message(
        string member, string value, string expected)
    {
        var model = Valid();
        Set(model, member, value);

        Assert.Contains(expected, MessagesFor(model, member));
    }

    [Fact]
    public void The_phone_field_stays_out_of_the_server_side_validation()
    {
        // Phone is optional and the service never validates it: no annotation, no message, whatever the client sends.
        var model = Valid();
        model.Phone = null;

        Assert.Empty(MessagesFor(model, nameof(PublicDropInListViewModel.Phone)));
    }

    [Fact]
    public async Task BookDropIn_does_not_duplicate_a_business_error_for_a_field_the_binder_already_rejected()
    {
        var fixture = await Fixture.CreateAsync();

        // The binder records the aligned Spanish message before the action runs; ClassDropInService returns the same
        // field error afterwards. Adding it again would show the same text twice in the alert summary.
        fixture.Controller.ModelState.AddModelError(
            nameof(PublicDropInListViewModel.FirstName), FirstNameRequired);

        var result = await fixture.Controller.BookDropIn("estudio", new PublicDropInListViewModel
        {
            AppointmentId = fixture.AppointmentId,
            Email = "ana@example.com",
            FirstName = "",
            LastName = "Pérez"
        }, default);

        Assert.IsType<ViewResult>(result);
        var error = Assert.Single(fixture.Controller.ModelState[nameof(PublicDropInListViewModel.FirstName)]!.Errors);
        Assert.Equal(FirstNameRequired, error.ErrorMessage);
        Assert.Equal(0, fixture.DropIns.InsertCallCount);
    }

    [Fact]
    public async Task BookDropIn_still_maps_a_business_error_on_a_field_the_binder_did_not_reject()
    {
        var fixture = await Fixture.CreateAsync();

        // AppointmentId carries no binder annotation, so the service error is the only one and must still be mapped.
        var result = await fixture.Controller.BookDropIn("estudio", new PublicDropInListViewModel
        {
            AppointmentId = 0,
            Email = "ana@example.com",
            FirstName = "Ana",
            LastName = "Pérez"
        }, default);

        Assert.IsType<ViewResult>(result);
        var error = Assert.Single(
            fixture.Controller.ModelState[nameof(PublicDropInListViewModel.AppointmentId)]!.Errors);
        Assert.Equal("Elegí una clase disponible.", error.ErrorMessage);
    }

    // --- Model validation fixture --------------------------------------------------------------------------------

    private static PublicDropInListViewModel Valid() => new()
    {
        AppointmentId = 100,
        Email = "ana@example.com",
        FirstName = "Ana",
        LastName = "Pérez",
        Phone = "11 5555 5555",
    };

    private static void Clear(PublicDropInListViewModel model, string member) => Set(model, member, "");

    private static void Set(PublicDropInListViewModel model, string member, string? value) =>
        typeof(PublicDropInListViewModel).GetProperty(member)!.SetValue(model, value);

    // Runs the same DataAnnotations validation the MVC binder runs, all properties included.
    private static IReadOnlyList<string> MessagesFor(PublicDropInListViewModel model, string member)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results
            .Where(result => result.MemberNames.Contains(member, StringComparer.Ordinal))
            .Select(result => result.ErrorMessage ?? "")
            .ToArray();
    }

    // --- Controller fixture --------------------------------------------------------------------------------------

    private sealed class Fixture
    {
        public required int AppointmentId { get; init; }
        public required FakeClassDropInRepository DropIns { get; init; }
        public required ReservationsController Controller { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var businesses = new FakeBusinessRepository();
            var businessId = (await businesses.InsertAsync(new Business { Name = "Estudio", Slug = "estudio" })).BusinessId;

            var serviceRepository = new FakeServiceRepository();
            var serviceId = await serviceRepository.InsertAsync(new Service
            {
                BusinessId = businessId,
                Name = "Yoga",
                Price = 3000m,
                DurationMinutes = 60,
                MaxCapacity = 8,
                IsActive = true
            });

            var workerRepository = new FakeWorkerRepository();
            var workerId = (await workerRepository.InsertAsync(new Worker
            {
                BusinessId = businessId,
                FirstName = "Ana",
                LastName = "Pérez",
                IsActive = true
            })).WorkerId;

            var clock = new FakeClock { Now = new DateTime(2026, 3, 10, 9, 0, 0) };
            var dropIns = new FakeClassDropInRepository();
            var unitOfWork = new FakeUnitOfWork();
            const int appointmentId = 100;
            dropIns.Sessions.Add(new FakeClassDropInSession(businessId, new ClassSessionRow(
                appointmentId,
                serviceId,
                workerId,
                new DateTime(2026, 3, 12, 10, 0, 0),
                MaxCapacity: 8,
                Kind: AppointmentKind.ClassSession,
                Status: AppointmentStatus.Scheduled,
                Bookings: [])));

            var context = new DefaultHttpContext();
            var controller = new ReservationsController(
                browseService: null!,  // BookDropIn never reads the individual booking flow.
                bookingService: null!, // BookDropIn never books an individual turn.
                dropInService: new ClassDropInService(dropIns, unitOfWork, clock),
                businesses: businesses,
                catalogService: new CatalogService(serviceRepository, unitOfWork),
                staffService: new StaffService(workerRepository, unitOfWork),
                clock: clock)
            {
                ControllerContext = new ControllerContext { HttpContext = context },
                Url = new StubUrlHelper()
            };

            return new Fixture
            {
                AppointmentId = appointmentId,
                DropIns = dropIns,
                Controller = controller
            };
        }
    }

    /// <summary>
    /// Minimal URL helper: <c>SetPublicLayout</c> falls back to <c>/r/{slug}</c> when <c>Url.Action</c> cannot resolve
    /// a route, which is exactly what a unit test without the MVC routing services gets.
    /// </summary>
    private sealed class StubUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext => new();

        public string? Action(UrlActionContext actionContext) => null;

        public string? Content(string? contentPath) => contentPath;

        public bool IsLocalUrl(string? url) => url is not null && url.StartsWith('/');

        public string? Link(string? routeName, object? values) => null;

        public string? RouteUrl(UrlRouteContext routeContext) => null;
    }
}
