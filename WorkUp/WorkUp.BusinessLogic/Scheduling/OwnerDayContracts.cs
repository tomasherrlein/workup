using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Scheduling;

public enum OwnerDaySection
{
    Now,
    Next,
    Later,
    Finished
}

public enum OwnerDayPaymentState
{
    NotApplicable,
    Pending,
    Paid
}

public sealed record OwnerDayRow(
    int AppointmentId,
    DateTime StartsAt,
    DateTime EndsAt,
    int WorkerId,
    AppointmentKind Kind,
    AppointmentStatus AppointmentStatus,
    AppointmentOrigin? Origin,
    string ServiceName,
    string WorkerName,
    int? BookingId,
    string? ClientName,
    BookingStatus? BookingStatus,
    OwnerDayPaymentState PaymentState);

public sealed record OwnerDayAttendee(
    int BookingId,
    string ClientName,
    BookingStatus Status,
    OwnerDayPaymentState PaymentState);

public sealed record OwnerDayItem(
    int AppointmentId,
    DateTime StartsAt,
    DateTime EndsAt,
    int WorkerId,
    AppointmentKind Kind,
    AppointmentStatus AppointmentStatus,
    AppointmentOrigin? Origin,
    string ServiceName,
    string WorkerName,
    IReadOnlyList<OwnerDayAttendee> Attendees,
    OwnerDaySection Section);

public sealed record OwnerDayResult(
    DateOnly SelectedDate,
    decimal TotalPaid,
    IReadOnlyList<OwnerDayItem> Now,
    IReadOnlyList<OwnerDayItem> Next,
    IReadOnlyList<OwnerDayItem> Later,
    IReadOnlyList<OwnerDayItem> Finished,
    int PendingPaymentCount)
{
    public static OwnerDayResult Empty(DateOnly date) => new(date, 0m, [], [], [], [], 0);
}

/// <summary>Tenant-scoped read-only projections for the authenticated owner's selected business-local day.</summary>
public interface IOwnerDayRepository
{
    Task<IReadOnlyList<OwnerDayRow>> ListDayAsync(int businessId, DateOnly date, CancellationToken ct = default);

    Task<decimal> GetPaidTotalAsync(int businessId, DateOnly date, CancellationToken ct = default);
}
