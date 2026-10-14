using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Scheduling;

public enum OwnerAgendaPeriod
{
    Day,
    Week
}

public sealed record OwnerAgendaFilter(
    DateOnly Date,
    OwnerAgendaPeriod Period = OwnerAgendaPeriod.Day,
    int? WorkerId = null,
    BookingStatus? BookingStatus = null,
    AppointmentOrigin? Origin = null);

public sealed record OwnerAgendaWorkerOption(int Id, string Name);

public sealed record OwnerAgendaRow(
    int AppointmentId,
    int? BookingId,
    DateTime StartsAt,
    DateTime EndsAt,
    int WorkerId,
    AppointmentKind Kind,
    AppointmentStatus AppointmentStatus,
    AppointmentOrigin? Origin,
    BookingStatus? BookingStatus,
    string ServiceName,
    string WorkerName,
    string? ClientName,
    decimal? AgreedPrice);

/// <summary>Owner-only detail; health observations and private-link tokens are deliberately excluded.</summary>
public sealed record OwnerAgendaBookingDetail(
    int BookingId,
    int AppointmentId,
    DateTime StartsAt,
    DateTime EndsAt,
    AppointmentStatus AppointmentStatus,
    AppointmentOrigin Origin,
    BookingStatus BookingStatus,
    string ServiceName,
    string WorkerName,
    string ClientName,
    string ClientEmail,
    string? ClientPhone,
    decimal? AgreedPrice,
    AppointmentKind AppointmentKind = AppointmentKind.Individual,
    int MaxCapacity = 1,
    DateTime? ConfirmedAt = null,
    DateTime? CancelledAt = null,
    CancelledBy? CancelledBy = null,
    DateTime? AttendanceRecordedAt = null,
    AbsenceSource? AbsenceSource = null,
    bool CanCancel = false,
    bool CanReschedule = false,
    int WorkerId = 0);

public sealed record OwnerAgendaResult(
    DateOnly SelectedDate,
    DateOnly RangeStart,
    DateOnly RangeEndExclusive,
    IReadOnlyList<OwnerAgendaWorkerOption> Workers,
    IReadOnlyList<OwnerAgendaRow> Entries);

/// <summary>Tenant-scoped read models for the authenticated owner's agenda.</summary>
public interface IOwnerAgendaRepository
{
    Task<IReadOnlyList<OwnerAgendaWorkerOption>> ListWorkersAsync(int businessId, CancellationToken ct = default);

    Task<IReadOnlyList<OwnerAgendaRow>> ListAgendaAsync(
        int businessId,
        DateOnly rangeStart,
        DateOnly rangeEndExclusive,
        int? workerId,
        BookingStatus? bookingStatus,
        AppointmentOrigin? origin,
        CancellationToken ct = default);

    Task<OwnerAgendaBookingDetail?> GetIndividualBookingAsync(
        int businessId, int bookingId, CancellationToken ct = default);
}
