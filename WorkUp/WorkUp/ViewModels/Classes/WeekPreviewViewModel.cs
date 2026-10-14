using WorkUp.BusinessLogic.Classes;
using WorkUp.Domain.Enums;
using WorkUp.ViewModels.Shared;
using SharedStatusTag = WorkUp.ViewModels.Shared.StatusTag;

namespace WorkUp.ViewModels.Classes;

/// <summary>
/// One fixed student of a previewed session (RF-06). <see cref="State"/> is the state the service derived for the
/// session date (never persisted, D-23); <see cref="AlreadyBooked"/> and <see cref="Cancelled"/> are what the preview
/// must show so a cancelled or already annotated student is never announced as a new annotation.
/// </summary>
public sealed record WeekPreviewFixedStudentViewModel(
    int EnrollmentId,
    string ClientName,
    WeekFixedStudentState State,
    bool AlreadyBooked,
    bool Cancelled)
{
    /// <summary>Friendly Spanish word for the derived state (the enum itself stays English).</summary>
    public string StateLabel => State switch
    {
        WeekFixedStudentState.Active => "Activa",
        WeekFixedStudentState.PendingPayment => "Pendiente de pago",
        WeekFixedStudentState.PendingRenewal => "Pendiente de renovación",
        _ => throw new ArgumentOutOfRangeException(nameof(State), State, null)
    };

    /// <summary>State as a word plus an icon (The Word-First Rule); never colour-only meaning.</summary>
    public StatusTag StateTag => State switch
    {
        // Activa has no shared factory; the other two reuse the shared tags so their label, class and icon cannot drift.
        WeekFixedStudentState.Active => new StatusTag(StateLabel, "tag-confirmed", "check"),
        WeekFixedStudentState.PendingPayment => SharedStatusTag.PendingPayment,
        WeekFixedStudentState.PendingRenewal => SharedStatusTag.PendingRenewal,
        _ => throw new ArgumentOutOfRangeException(nameof(State), State, null)
    };

    /// <summary>Whether the confirm flow will write this student's fixed annotation, keep it, or skip it.</summary>
    public string AnnotationLabel => Cancelled ? "Cancelada" : AlreadyBooked ? "Ya anotada" : "Sin anotar";

    public StatusTag AnnotationTag => Cancelled
        ? new StatusTag(AnnotationLabel, "tag-cancelled", "cross")
        : AlreadyBooked
            ? new StatusTag(AnnotationLabel, "tag-confirmed", "check")
            : new StatusTag(AnnotationLabel, "tag-pending", "clock");
}

/// <summary>One session of the previewed week, already resolved to the names the owner recognizes.</summary>
public sealed record WeekPreviewSessionViewModel(
    DateOnly Date,
    TimeOnly StartTime,
    string ServiceName,
    string WorkerName,
    bool IsNew,
    bool Suspended,
    IReadOnlyList<WeekPreviewFixedStudentViewModel> FixedStudents)
{
    /// <summary>Nueva, Ya generada or Suspendido, as a word plus an icon.</summary>
    public string StatusLabel => Suspended ? "Suspendido" : IsNew ? "Nueva" : "Ya generada";

    public StatusTag StatusTag => Suspended
        ? SharedStatusTag.For(AppointmentStatus.Suspended)
        : IsNew
            ? new StatusTag(StatusLabel, "tag-scheduled", "plus")
            : new StatusTag(StatusLabel, "tag-neutral", "repeat");
}

/// <summary>
/// Read-only week preview page (RF-06): the selected date, the Monday-to-Sunday bounds, the sessions with their
/// resolved service and worker names, and the fixed students of each session. Nothing here is written back until the
/// owner confirms.
/// </summary>
public sealed class WeekPreviewViewModel
{
    /// <summary>The date the owner picked, echoed back into the form; null when the default week is shown.</summary>
    public DateOnly? SelectedDate { get; init; }

    public DateOnly WeekStart { get; init; }

    public DateOnly WeekEnd { get; init; }

    public IReadOnlyList<WeekPreviewSessionViewModel> Sessions { get; init; } = [];

    /// <summary>Monday of the week in progress, or null when it is not fully representable.</summary>
    public DateOnly? CurrentWeekStart { get; init; }

    /// <summary>Monday of the week after the one in progress, or null when it is not fully representable.</summary>
    public DateOnly? NextWeekStart { get; init; }

    /// <summary>
    /// True when the previewed week ended before today: the service rejects generating it, so the page shows a note
    /// instead of a confirm button that could only fail. The week that ends today is still confirmable.
    /// </summary>
    public bool IsPastWeek { get; init; }

    /// <summary>
    /// An active grid class always projects exactly one session for the week, so an empty preview means the business
    /// has no active grid class yet and the page must point back to the grid.
    /// </summary>
    public bool HasActiveGridClasses => Sessions.Count > 0;

    public int NewSessionCount => Sessions.Count(session => session.IsNew);

    public int ExistingSessionCount => Sessions.Count(session => !session.IsNew);

    public int FixedStudentCount => Sessions.Sum(session => session.FixedStudents.Count);
}
