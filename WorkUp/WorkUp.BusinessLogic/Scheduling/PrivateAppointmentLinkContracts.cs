using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Scheduling;

/// <summary>Database projection for one exact appointment link; it deliberately contains no client data.</summary>
public sealed record PrivateAppointmentLinkRecord(
    DateTime StartsAt,
    DateTime EndsAt,
    DateTime ExpiresAt,
    string ServiceName,
    string WorkerName,
    AppointmentStatus AppointmentStatus,
    BookingStatus BookingStatus,
    DateTime? ConfirmedAt,
    DateTime? CancelledAt,
    DateTime? AttendanceRecordedAt,
    AbsenceSource? AbsenceSource,
    CancelledBy? CancelledBy);

/// <summary>Safe anonymous projection for the private-link page; never includes a token or client identity.</summary>
public sealed record PrivateAppointmentView(
    DateTime StartsAt,
    DateTime EndsAt,
    string ServiceName,
    string WorkerName,
    bool CanConfirm,
    bool CanCancel,
    bool CanReportAbsence);

/// <summary>Trusted absolute URL returned only to the authenticated business owner.</summary>
public sealed record OwnerPrivateAppointmentLink(string Url, DateTime ExpiresAt);

/// <summary>Protected current token data returned only inside owner management operations.</summary>
public sealed record OwnerPrivateAppointmentLinkRecord(
    int LinkId,
    byte[] TokenHash,
    string ProtectedToken,
    DateTime ExpiresAt);

public enum PrivateAppointmentLinkAction : byte
{
    Confirm = 1,
    Cancel = 2,
    ReportAbsence = 3
}

/// <summary>Persistence boundary for public private-link operations and tenant-scoped owner link management.</summary>
public interface IPrivateAppointmentLinkRepository
{
    Task<PrivateAppointmentLinkRecord?> GetPublicAppointmentAsync(
        byte[] tokenHash, DateTime now, CancellationToken ct = default);

    /// <summary>Applies one action using token, status, and time predicates in the current SQL transaction.</summary>
    Task<bool> ApplyPublicActionAsync(
        byte[] tokenHash, PrivateAppointmentLinkAction action, DateTime now, CancellationToken ct = default);

    /// <summary>Locks and verifies an individual booking belonging to the authenticated business.</summary>
    Task<bool> LockOwnerBookingAsync(int businessId, int bookingId, CancellationToken ct = default);

    /// <summary>Returns current links after the booking lock; multiple live links are treated as ambiguous.</summary>
    Task<IReadOnlyList<OwnerPrivateAppointmentLinkRecord>> GetActiveOwnerLinksAsync(
        int businessId, int bookingId, DateTime now, CancellationToken ct = default);

    Task<bool> RevokeOwnerLinkAsync(
        int businessId, int bookingId, int linkId, DateTime now, CancellationToken ct = default);

    Task<int> InsertOwnerLinkAsync(int businessId, AppointmentLink link, CancellationToken ct = default);
}
