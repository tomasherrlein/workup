using WorkUp.BusinessLogic.Abstractions;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Scheduling;

/// <summary>Builds a bounded, tenant-scoped read model for the authenticated business owner's agenda.</summary>
public sealed class OwnerAgendaService(IOwnerAgendaRepository repository, IClock clock)
{
    public async Task<OwnerAgendaResult> GetAgendaAsync(
        int businessId, OwnerAgendaFilter filter, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.Now);
        var selectedDate = filter.Date > DateOnly.MaxValue.AddDays(-7) ? today : filter.Date;
        var period = Enum.IsDefined(filter.Period) ? filter.Period : OwnerAgendaPeriod.Day;
        var rangeStart = period == OwnerAgendaPeriod.Week ? MondayOf(selectedDate) : selectedDate;
        var rangeEndExclusive = rangeStart.AddDays(period == OwnerAgendaPeriod.Week ? 7 : 1);

        if (businessId <= 0)
            return new OwnerAgendaResult(selectedDate, rangeStart, rangeEndExclusive, [], []);

        var workerId = filter.WorkerId is > 0 ? filter.WorkerId : null;
        BookingStatus? bookingStatus = filter.BookingStatus is { } status && Enum.IsDefined(status) ? status : null;
        AppointmentOrigin? origin = filter.Origin is { } appointmentOrigin && Enum.IsDefined(appointmentOrigin)
            ? appointmentOrigin
            : null;

        var workers = await repository.ListWorkersAsync(businessId, ct);
        var entries = await repository.ListAgendaAsync(
            businessId, rangeStart, rangeEndExclusive, workerId, bookingStatus, origin, ct);
        return new OwnerAgendaResult(selectedDate, rangeStart, rangeEndExclusive, workers, entries);
    }

    public async Task<OwnerAgendaBookingDetail?> GetIndividualBookingAsync(
        int businessId, int bookingId, CancellationToken ct = default)
    {
        if (businessId <= 0 || bookingId <= 0)
            return null;

        var booking = await repository.GetIndividualBookingAsync(businessId, bookingId, ct);
        if (booking is null)
            return null;

        var cancellationState = new OwnerBookingCancellationState(
            booking.BookingId,
            booking.AppointmentId,
            booking.StartsAt,
            booking.AppointmentKind,
            booking.AppointmentStatus,
            booking.Origin,
            booking.MaxCapacity,
            booking.BookingStatus,
            null,
            null,
            booking.ConfirmedAt,
            booking.CancelledAt,
            booking.CancelledBy,
            booking.AttendanceRecordedAt,
            booking.AbsenceSource);
        // Cancel and reschedule share one precondition: an active reserved individual booking before its start.
        var isActiveBeforeStart = OwnerBookingActionsService.CanCancel(cancellationState, clock.Now);
        return booking with
        {
            CanCancel = isActiveBeforeStart,
            CanReschedule = isActiveBeforeStart
        };
    }

    private static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}
