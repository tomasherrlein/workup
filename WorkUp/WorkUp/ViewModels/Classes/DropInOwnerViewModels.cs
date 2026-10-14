using WorkUp.Domain.Enums;
using SharedStatusTag = WorkUp.ViewModels.Shared.StatusTag;

namespace WorkUp.ViewModels.Classes;

/// <summary>
/// One client of the business the owner can book as a drop-in (RF-11). The list comes from the business's own
/// clients, so the picker can never name a client of another tenant.
/// </summary>
public sealed record ClassDropInClientOptionViewModel(int Id, string Name);

/// <summary>
/// One drop-in annotation of a roster session (RF-22). A cancelled annotation keeps its place in the list with its
/// status: the owner has to know that the place was released, and the roster entry carries the client whose place was
/// released. What disappears when it is cancelled is only the cancel action.
/// <para>
/// <see cref="CanCancel"/> is the courtesy filter of the screen, not the guard: a session that already started, a
/// suspended session and an annotation that is already cancelled never offer the action, while the service still
/// enforces every rule.
/// </para>
/// </summary>
public sealed record ClassDropInAnnotationViewModel(
    int BookingId, string ClientName, BookingStatus Status, bool CanCancel)
{
    public bool IsCancelled => Status == BookingStatus.Cancelled;

    /// <summary>
    /// The name the owner reads. The roster keeps an annotation whose client row could not be resolved instead of
    /// hiding it, so the label falls back to the panel's word for a client: the status and the cancel action stay
    /// usable and no screen shows an empty label.
    /// </summary>
    public string DisplayName => string.IsNullOrWhiteSpace(ClientName) ? "Alumna" : ClientName;

    /// <summary>Status as a word plus an icon (The Word-First Rule); never colour-only meaning.</summary>
    public SharedStatusTag StatusTag => SharedStatusTag.For(Status);
}

/// <summary>
/// One upcoming class session of the owner roster (RF-22, RF-07 cupo &gt; 1) with its drop-in annotations, already
/// resolved to the names the owner recognizes. A session without annotations is part of the page too. The capacity is
/// not computed here: this screen shows who is booked as a drop-in, not the occupancy.
/// </summary>
public sealed record ClassDropInSessionViewModel(
    int AppointmentId,
    DateOnly Date,
    TimeOnly StartTime,
    string ServiceName,
    string TeacherName,
    bool Suspended,
    bool CanBook,
    IReadOnlyList<ClassDropInAnnotationViewModel> DropIns)
{
    /// <summary>Suspended or Programado, as a word plus an icon.</summary>
    public SharedStatusTag StatusTag => SharedStatusTag.For(
        Suspended ? AppointmentStatus.Suspended : AppointmentStatus.Scheduled);

    public bool HasDropIns => DropIns.Count > 0;
}

/// <summary>
/// Owner screen of the sueltas (RF-22, RF-07 cupo &gt; 1): the upcoming classes of the forward window with their
/// drop-in annotations, the clients the owner can book, and the single session whose booking form is open.
/// <para>
/// <see cref="OpenAppointmentId"/> is derived server-side from the selected session, never bound from the request:
/// only one booking form is rendered at a time, which keeps every field id unique and the screen down to a single
/// primary action. It is an id the screen is actually offering, so a query string never opens a form for a class the
/// owner is not seeing.
/// </para>
/// </summary>
public sealed class DropInOwnerIndexViewModel
{
    /// <summary>Business day the window starts at, which is also the day the service judged every session against.</summary>
    public DateOnly Today { get; init; }

    /// <summary>Last day of the forward window, inclusive.</summary>
    public DateOnly WindowEnd { get; init; }

    public IReadOnlyList<ClassDropInSessionViewModel> Sessions { get; init; } = [];

    public IReadOnlyList<ClassDropInClientOptionViewModel> Clients { get; init; } = [];

    public int? OpenAppointmentId { get; init; }

    public bool HasSessions => Sessions.Count > 0;

    public bool HasClients => Clients.Count > 0;

    public int ActiveDropInCount => Sessions.Sum(session => session.DropIns.Count(annotation => !annotation.IsCancelled));

    public int CancelledDropInCount => Sessions.Sum(session => session.DropIns.Count(annotation => annotation.IsCancelled));
}
