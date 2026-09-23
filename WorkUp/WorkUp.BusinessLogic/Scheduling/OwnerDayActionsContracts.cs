using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Scheduling;

public sealed record OwnerDayCharge(decimal Amount, string? PriceReason = null);

public sealed record OwnerDayBookingState(
    int BusinessId, int BookingId, int ClientId, int AppointmentId, DateTime StartsAt,
    AppointmentKind AppointmentKind, AppointmentOrigin? Origin, AppointmentStatus AppointmentStatus,
    int MaxCapacity, BookingStatus BookingStatus, BookingModality? Modality, int? EnrollmentId,
    DateTime? ConfirmedAt, DateTime? CancelledAt, DateTime? AttendanceRecordedAt,
    AbsenceSource? AbsenceSource, decimal? AgreedPrice, string? PriceReason, decimal ListPrice);

public sealed record OwnerDayPayment(int BusinessId, int ClientId, int BookingId,
    PaymentConcept Concept, decimal Amount, decimal ListPrice, string? PriceReason, DateTime PaidAt);

public interface IOwnerDayActionsRepository
{
    Task<OwnerDayBookingState?> LockAsync(int businessId, int bookingId, CancellationToken ct = default);
    Task<bool> HasEffectivePaymentAsync(int businessId, int bookingId, CancellationToken ct = default);
    Task<bool> SetStatusAsync(int businessId, int bookingId, BookingStatus status, DateTime now, CancellationToken ct = default);
    Task InsertPaymentAsync(OwnerDayPayment payment, CancellationToken ct = default);
    Task InsertNoteAsync(int businessId, int clientId, int appointmentId, string note, DateTime now, CancellationToken ct = default);
}
