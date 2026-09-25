using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Classes;

/// <summary>
/// One annotation of a generated class session as the owner cancellation reads it under the lock (RF-22). It carries
/// the annotation, its session and the facts the rules need: the class must still be <c>Programado</c> and not started,
/// and the annotation must be a fixed or drop-in annotation that is not cancelled. The tenant rides on the row, so a
/// booking of another business is simply not found, and <see cref="Kind"/> and <see cref="Modality"/> are what let the
/// caller refuse a booking that is not a class annotation with the same answer as a missing one.
/// </summary>
public sealed record ClassBookingCancellationState(
    int BusinessId,
    int BookingId,
    int AppointmentId,
    DateTime StartsAt,
    AppointmentKind Kind,
    AppointmentStatus AppointmentStatus,
    BookingStatus BookingStatus,
    BookingModality? Modality);

/// <summary>
/// One annotation of a class session as the suspension reads it (RF-10). The suspension is a property of the class,
/// not of the annotation, so nothing here changes when the class is suspended: the annotation keeps its status, its
/// modality and its enrollment untouched (D-15, D-24), and this record exists only so the caller can decide who is
/// notified. <see cref="ClientEmail"/> is the recipient of the suspension notice and is empty when the client row
/// could not be resolved: the annotation still belongs to the session and is never dropped from the suspension.
/// </summary>
public sealed record ClassSuspensionBooking(
    int BookingId,
    int ClientId,
    string ClientEmail,
    string ClientName,
    BookingModality? Modality,
    BookingStatus Status)
{
    /// <summary>
    /// Whether the annotation is cancelled, which is the only reason it receives no suspension notice (RF-22: a
    /// cancelled annotation already released its place, so there is nothing left to suspend for that student). Every
    /// other status — including a pre-recorded <c>Atendido</c> or <c>Ausente</c> — is notified, because the class did
    /// not happen for anyone.
    /// </summary>
    public bool IsCancelled => Status == BookingStatus.Cancelled;
}

/// <summary>
/// The class session the suspension read under the lock (RF-10): its identity, its start, its kind, its current
/// status and every annotation it holds, cancelled ones included so the caller — and not the query — decides who is
/// notified. <see cref="Kind"/> travels with the row so the caller can refuse an individual turn even if an
/// implementation forgot to filter it, and <see cref="Status"/> is the state that decides whether the class can still
/// be suspended.
/// </summary>
public sealed record ClassSuspensionSession(
    int BusinessId,
    int AppointmentId,
    DateTime StartsAt,
    AppointmentKind Kind,
    AppointmentStatus Status,
    IReadOnlyList<ClassSuspensionBooking> Bookings)
{
    public DateOnly Date => DateOnly.FromDateTime(StartsAt);

    public TimeOnly StartTime => TimeOnly.FromDateTime(StartsAt);
}

/// <summary>
/// Result of suspending a class (RF-10 outputs): the session that was suspended and how many suspension notices this
/// operation actually queued. <see cref="NoticesQueued"/> counts the notices that landed in the outbox, not the
/// non-cancelled annotations: a notice the outbox already held for the same student and the same class is a
/// deduplication hit and is not counted twice (RF-10 error handling).
/// </summary>
public sealed record ClassSuspensionOutcome(int AppointmentId, int NoticesQueued);

/// <summary>
/// The input rule of the suspension reason (RF-10), shared by the service and the screen that will collect it: a
/// mandatory reason of 1 to 300 characters, taken from the trimmed text so a reason of only spaces is not a reason and
/// a reason that fits after trimming is not rejected for the spaces around it.
/// </summary>
public static class ClassSuspensionInput
{
    /// <summary>Longest reason the stored column accepts (<c>Appointments.SuspensionReason NVARCHAR(300)</c>).</summary>
    public const int MaxReasonLength = 300;

    /// <summary>The reason as it is judged and stored: trimmed, never <c>null</c>.</summary>
    public static string NormalizeReason(string? reason) => reason?.Trim() ?? "";

    /// <summary>Whether the normalized reason is present and fits the stored column.</summary>
    public static bool IsValid(string reason) => reason.Length is > 0 and <= MaxReasonLength;
}
