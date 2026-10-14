using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;

namespace WorkUp.Tests.Fakes;

public sealed class FakeOwnerDayActionsRepository : IOwnerDayActionsRepository
{
    public OwnerDayBookingState Booking { get; set; } = Default();
    public List<OwnerDayPayment> Payments { get; } = [];
    public List<string> Notes { get; } = [];

    public static OwnerDayBookingState Default() => new(7, 12, 20, 30, new DateTime(2026, 3, 10, 9, 0, 0),
        AppointmentKind.Individual, AppointmentOrigin.Reserved, AppointmentStatus.Scheduled, 1,
        BookingStatus.Pending, null, null, null, null, null, null, 100m, null, 100m);

    public Task<OwnerDayBookingState?> LockAsync(int businessId, int bookingId, CancellationToken ct = default) =>
        Task.FromResult<OwnerDayBookingState?>(Booking.BusinessId == businessId && Booking.BookingId == bookingId ? Booking : null);

    public Task<bool> HasEffectivePaymentAsync(int businessId, int bookingId, CancellationToken ct = default) =>
        Task.FromResult(Payments.Count > 0);

    public Task<bool> SetStatusAsync(int businessId, int bookingId, BookingStatus status, DateTime now, CancellationToken ct = default)
    {
        Booking = Booking with { BookingStatus = status, AttendanceRecordedAt = now,
            AbsenceSource = status == BookingStatus.Absent ? AbsenceSource.Owner : null };
        return Task.FromResult(true);
    }

    public Task InsertPaymentAsync(OwnerDayPayment payment, CancellationToken ct = default)
    {
        Payments.Add(payment);
        return Task.CompletedTask;
    }

    public Task InsertNoteAsync(int businessId, int clientId, int appointmentId, string note, DateTime now, CancellationToken ct = default)
    {
        Notes.Add(note);
        return Task.CompletedTask;
    }
}
