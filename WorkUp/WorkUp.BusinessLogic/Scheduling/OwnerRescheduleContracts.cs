using WorkUp.BusinessLogic.Common;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Scheduling;

public sealed record OwnerRescheduleRequest(int WorkerId, DateTime NewStartsAt);

public sealed record OwnerRescheduleConfirmation(
    int BookingId,
    int AppointmentId,
    DateTime StartsAt,
    DateTime EndsAt);

public sealed record OwnerRescheduleAttempt(Result<OwnerRescheduleConfirmation> Result)
{
    public bool Succeeded => Result.Succeeded;
    public BusinessError? Error => Result.Error;
    public OwnerRescheduleConfirmation Confirmation => Result.Value;
}

/// <summary>Fresh, tenant-scoped state locked for one owner reschedule transaction.</summary>
public sealed record OwnerRescheduleBooking(
    int BusinessId,
    int BookingId,
    int AppointmentId,
    int WorkerId,
    AppointmentKind AppointmentKind,
    AppointmentStatus AppointmentStatus,
    AppointmentOrigin? Origin,
    int MaxCapacity,
    BookingModality? Modality,
    int? EnrollmentId,
    BookingStatus BookingStatus,
    DateTime StartsAt,
    DateTime EndsAt,
    DateTime? ConfirmedAt,
    DateTime? CancelledAt,
    CancelledBy? CancelledBy,
    DateTime? AttendanceRecordedAt,
    AbsenceSource? AbsenceSource,
    int ServiceDurationMinutes,
    string BusinessName,
    string ClientFirstName,
    string ClientEmail,
    string ServiceName,
    string WorkerName);

public sealed record OwnerRescheduleCurrentLink(int LinkId, DateTime ExpiresAt);

/// <summary>Unlocked preview of the same worker's free start times for one day; the save revalidates under lock.</summary>
public sealed record OwnerRescheduleOptions(
    DateOnly Date,
    DateTime CurrentStartsAt,
    IReadOnlyList<TimeOnly> AvailableStartTimes,
    DateTime? NearestAlternative);

/// <summary>Transaction-bound persistence for owner reprogramming of an individual reserved appointment.</summary>
public interface IOwnerRescheduleRepository
{
    /// <summary>Non-locking tenant-scoped lookup used only to identify the worker lock to acquire first.</summary>
    Task<int?> GetWorkerIdAsync(int businessId, int bookingId, CancellationToken ct = default);

    /// <summary>Acquires the same per-worker update lock used by competing booking attempts.</summary>
    Task<bool> LockWorkerAsync(int businessId, int workerId, CancellationToken ct = default);

    /// <summary>Locks and re-reads the tenant-scoped booking and its appointment after the worker lock.</summary>
    Task<OwnerRescheduleBooking?> LockOwnerBookingAsync(int businessId, int bookingId, CancellationToken ct = default);

    /// <summary>Reloads the worker's current schedules, breaks, appointments, and bookings after both locks.</summary>
    Task<BookingAvailabilitySnapshot> GetAvailabilitySnapshotAsync(
        int businessId, int workerId, DateOnly date, CancellationToken ct = default);

    /// <summary>Non-locking tenant-scoped read used only for the reschedule preview.</summary>
    Task<OwnerRescheduleBooking?> GetOwnerBookingAsync(int businessId, int bookingId, CancellationToken ct = default);

    /// <summary>Reads the worker's schedules, breaks, appointments, and bookings for an inclusive date range.</summary>
    Task<BookingAvailabilitySnapshot> GetAvailabilitySnapshotAsync(
        int businessId, int workerId, DateOnly startDate, DateOnly endDate, CancellationToken ct = default);

    /// <summary>Returns up to two unrevoked, unexpired links; more than one makes the operation fail closed.</summary>
    Task<IReadOnlyList<OwnerRescheduleCurrentLink>> GetCurrentLinksAsync(
        int businessId, int bookingId, DateTime now, CancellationToken ct = default);

    /// <summary>Moves the existing appointment and confirms its booking using conditional state/time guards.</summary>
    Task<bool> TryApplyRescheduleAsync(
        int businessId,
        OwnerRescheduleBooking booking,
        DateTime newStartsAt,
        DateTime newEndsAt,
        DateTime now,
        CancellationToken ct = default);

    /// <summary>Updates only expiry for the one previously observed active link; never changes token material.</summary>
    Task<bool> UpdateCurrentLinkExpiryAsync(
        int businessId,
        int bookingId,
        int linkId,
        DateTime expectedExpiresAt,
        DateTime newExpiresAt,
        DateTime now,
        CancellationToken ct = default);

    Task InsertNotificationAsync(Notification notification, CancellationToken ct = default);
}
