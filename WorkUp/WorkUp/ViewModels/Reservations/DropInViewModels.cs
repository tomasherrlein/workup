using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using WorkUp.BusinessLogic.Scheduling;

namespace WorkUp.ViewModels.Reservations;

/// <summary>
/// One bookable class session of the public drop-in screen (RF-07 cupo &gt; 1, RF-22). The business service already
/// dropped the suspended, started and full sessions, so everything here can be booked. The class and teacher names
/// are resolved by the controller from the ids the offer carries, because a public screen never receives entities.
/// </summary>
public sealed record PublicDropInSession(
    int AppointmentId,
    DateOnly Date,
    TimeOnly StartTime,
    string ServiceName,
    string TeacherName,
    int RemainingPlaces);

/// <summary>
/// The public drop-in screen: the bookable sessions of the business and the contact form of the session being booked.
/// The same instance is the view model of the list and the bound model of the booking POST, so the values the person
/// typed come back under the exact property names the contact form posted and the stable service error fields
/// (<c>Email</c>, <c>FirstName</c>, <c>LastName</c>, <c>AppointmentId</c>) land on the right control.
/// <see cref="Business"/>, <see cref="Sessions"/> and <see cref="OpenAppointmentId"/> are server-side state: they are
/// never bound from the request, because a client must not choose the business or the offer it is shown.
/// </summary>
public sealed class PublicDropInListViewModel
{
    [BindNever]
    public PublicBookingBusiness Business { get; set; } = new(0, "", "");

    [BindNever]
    public IReadOnlyList<PublicDropInSession> Sessions { get; set; } = [];

    /// <summary>
    /// Session whose contact form the screen shows, or <c>null</c> when none is open. It is derived server-side from
    /// the selected session (GET) or from the rejected booking (POST), never bound. Only one form is rendered at a
    /// time: that keeps every field id unique and the screen down to a single primary action.
    /// </summary>
    [BindNever]
    public int? OpenAppointmentId { get; set; }

    public int AppointmentId { get; set; }

    // The three contact fields carry explicit Spanish DataAnnotations so the MVC binder records a Spanish message
    // before the action runs, instead of the English implicit-required default of a non-nullable reference type. The
    // required and the nombre/apellido length messages are the ones ClassDropInService.ValidateRequest owns; the Email
    // >254 message is this application's own convention and is deliberately not copied verbatim from the service,
    // which collapses over-long and malformed emails into the same "Ingresá un email válido." error. Phone stays
    // unannotated: it is optional and the service never validates it, so it must not claim a server-side error.
    [Required(ErrorMessage = "Ingresá tu email.")]
    [EmailAddress(ErrorMessage = "Ingresá un email válido.")]
    [StringLength(254, ErrorMessage = "El email no puede superar los 254 caracteres.")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Ingresá tu nombre.")]
    [StringLength(60, ErrorMessage = "El nombre no puede superar los 60 caracteres.")]
    public string FirstName { get; set; } = "";

    [Required(ErrorMessage = "Ingresá tu apellido.")]
    [StringLength(60, ErrorMessage = "El apellido no puede superar los 60 caracteres.")]
    public string LastName { get; set; } = "";

    public string? Phone { get; set; }
}

/// <summary>
/// Confirmation of a booked drop-in (RF-07 outputs): the day, the time, the class and its teacher. No private link,
/// because RF-23 links belong to individual turns only.
/// </summary>
public sealed record PublicDropInConfirmationViewModel(
    PublicBookingBusiness Business,
    DateOnly Date,
    TimeOnly StartTime,
    string ServiceName,
    string TeacherName);
