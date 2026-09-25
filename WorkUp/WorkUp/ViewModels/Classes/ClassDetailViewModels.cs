using WorkUp.BusinessLogic.Classes;
using WorkUp.Domain.Enums;
using SharedStatusTag = WorkUp.ViewModels.Shared.StatusTag;

namespace WorkUp.ViewModels.Classes;

/// <summary>
/// One annotation of the class detail roster (RF-10, RF-11, RF-12, RF-22) as the owner reads it: the client, the
/// modality, the booking status and — for a fixed student — the derived membership state of the class date (never
/// persisted, D-16, D-23). A cancelled annotation stays on the roster so the owner sees whose place was released, and
/// it is shown as «Cancelada (lugar liberado)»; an absence is shown as «Ausente», and only that status feeds the
/// absence count. The four <c>Can…</c> flags are the courtesy of the screen, never the rule: the service revalidates
/// every action under its own lock.
/// <para>
/// <see cref="CanCharge"/> is the late charge of a drop-in (T18, D-20): the owner marked the attendance and forgot the
/// money, and no other path can register it. It is a courtesy of the same kind as the others — the screen only
/// offers it for an already attended drop-in of a non-suspended class, and the charge service refuses everything
/// else. Nothing here can know whether an effective payment already exists, because the detail read carries no
/// payment: the duplicate is rejected by the server and the sentence the owner reads says so.
/// </para>
/// </summary>
public sealed record ClassDetailStudentViewModel(
    int BookingId,
    string ClientName,
    BookingStatus Status,
    BookingModality? Modality,
    ClassFixedStudentState? FixedState,
    bool CanAttend,
    bool CanMarkAbsent,
    bool CanCancel,
    bool CanCharge)
{
    /// <summary>The label the roster shows for an annotation whose place was released (RF-22).</summary>
    public const string CancelledLabel = "Cancelada (lugar liberado)";

    public bool IsCancelled => Status == BookingStatus.Cancelled;

    /// <summary>Only <see cref="BookingStatus.Absent"/> is an absence; a cancelled annotation is not one.</summary>
    public bool IsAbsent => Status == BookingStatus.Absent;

    public bool IsDropIn => Modality == BookingModality.DropIn;

    public bool IsFixed => Modality == BookingModality.Fixed;

    /// <summary>
    /// The name the owner reads. The detail keeps an annotation whose client row could not be resolved instead of
    /// hiding it, so the label falls back to the panel's word for a client.
    /// </summary>
    public string DisplayName => string.IsNullOrWhiteSpace(ClientName) ? "Alumna" : ClientName;

    public string ModalityLabel => Modality switch
    {
        BookingModality.Fixed => "Fija",
        BookingModality.DropIn => "Suelta",
        _ => "Sin modalidad"
    };

    /// <summary>Status as a word plus an icon (The Word-First Rule); a cancelled one reads «Cancelada (lugar liberado)».</summary>
    public SharedStatusTag StatusTag => Status == BookingStatus.Cancelled
        ? SharedStatusTag.For(Status) with { Label = CancelledLabel }
        : SharedStatusTag.For(Status);

    /// <summary>Derived renewal state of a fixed student, or <c>null</c> for a drop-in, which has none.</summary>
    public SharedStatusTag? FixedStateTag => FixedState switch
    {
        ClassFixedStudentState.Active => SharedStatusTag.For(MembershipState.Current),
        ClassFixedStudentState.PendingPayment => SharedStatusTag.PendingPayment,
        ClassFixedStudentState.PendingRenewal => SharedStatusTag.PendingRenewal,
        _ => null
    };
}

/// <summary>
/// The class session the detail page shows (RF-10, RF-11, RF-12, RF-22), with the occupancy the read already resolved
/// through <c>ClassCapacity</c> and the two flags of the date-level actions. <see cref="EndsAt"/> and its
/// <see cref="EndTime"/> are the persisted end of the generated session — never recomputed from the tariff of the
/// moment, so an edited tariff neither shortens the displayed class nor closes the walk-in window the server still
/// accepts. <see cref="CanSuspend"/> is true only while the class can still be suspended — <c>Programado</c> and
/// strictly before its start — and <see cref="CanRegisterWalkIn"/> only inside the arrival window and when the
/// business admits attention without a booking; the service revalidates both.
/// </summary>
public sealed record ClassDetailSessionViewModel(
    int AppointmentId,
    DateOnly Date,
    TimeOnly StartTime,
    TimeOnly EndTime,
    DateTime EndsAt,
    string ServiceName,
    string TeacherName,
    int MaxCapacity,
    int PlacesTaken,
    int RemainingPlaces,
    bool Suspended,
    string? SuspensionReason,
    bool CanSuspend,
    bool CanRegisterWalkIn)
{
    /// <summary>Programado or Suspendido, as a word plus an icon.</summary>
    public SharedStatusTag StatusTag => SharedStatusTag.For(
        Suspended ? AppointmentStatus.Suspended : AppointmentStatus.Scheduled);

    public bool IsFull => RemainingPlaces == 0;

    /// <summary>The occupancy as one sentence, always from the read's numbers and never from a second count.</summary>
    public string OccupancyLabel => $"{PlacesTaken} de {MaxCapacity} lugares ocupados";
}

/// <summary>
/// The whole detail page of one generated class (RF-10, RF-11, RF-12, RF-22): the session with its occupancy, every
/// annotation it holds — fixed, drop-in and cancelled — and the business's clients the walk-in picker offers. The
/// counts are projections of <see cref="Students"/>; the occupancy is never recomputed here.
/// </summary>
public sealed record ClassDetailViewModel(
    ClassDetailSessionViewModel Session,
    IReadOnlyList<ClassDetailStudentViewModel> Students,
    IReadOnlyList<ClassDropInClientOptionViewModel> Clients)
{
    public bool HasStudents => Students.Count > 0;

    public bool HasClients => Clients.Count > 0;

    /// <summary>Absences the roster records. Only <c>Ausente</c> feeds it; a cancelled annotation is not an absence.</summary>
    public int AbsentCount => Students.Count(student => student.IsAbsent);

    public int CancelledCount => Students.Count(student => student.IsCancelled);

    /// <summary>Fixed students whose membership does not cover the class date and who keep their place (D-23).</summary>
    public int PendingRenewalCount =>
        Students.Count(student => student.FixedState == ClassFixedStudentState.PendingRenewal);
}
