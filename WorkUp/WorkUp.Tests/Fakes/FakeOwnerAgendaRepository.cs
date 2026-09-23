using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;

namespace WorkUp.Tests.Fakes;

public sealed class FakeOwnerAgendaRepository : IOwnerAgendaRepository
{
    public List<(int BusinessId, OwnerAgendaWorkerOption Worker)> Workers { get; } = [];
    public List<(int BusinessId, OwnerAgendaRow Entry)> Entries { get; } = [];
    public List<(int BusinessId, OwnerAgendaBookingDetail Detail)> BookingDetails { get; } = [];
    public List<int> RequestedWorkerBusinessIds { get; } = [];
    public List<AgendaRequest> AgendaRequests { get; } = [];
    public List<(int BusinessId, int BookingId)> BookingRequests { get; } = [];

    public Task<IReadOnlyList<OwnerAgendaWorkerOption>> ListWorkersAsync(int businessId, CancellationToken ct = default)
    {
        RequestedWorkerBusinessIds.Add(businessId);
        IReadOnlyList<OwnerAgendaWorkerOption> result = Workers
            .Where(row => row.BusinessId == businessId)
            .Select(row => row.Worker)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<OwnerAgendaRow>> ListAgendaAsync(
        int businessId,
        DateOnly rangeStart,
        DateOnly rangeEndExclusive,
        int? workerId,
        BookingStatus? bookingStatus,
        AppointmentOrigin? origin,
        CancellationToken ct = default)
    {
        AgendaRequests.Add(new AgendaRequest(businessId, rangeStart, rangeEndExclusive, workerId, bookingStatus, origin));
        IReadOnlyList<OwnerAgendaRow> result = Entries
            .Where(row => row.BusinessId == businessId)
            .Select(row => row.Entry)
            .Where(entry => DateOnly.FromDateTime(entry.StartsAt) >= rangeStart &&
                            DateOnly.FromDateTime(entry.StartsAt) < rangeEndExclusive)
            .Where(entry => workerId is null || entry.WorkerId == workerId)
            .Where(entry => bookingStatus is null || entry.BookingStatus == bookingStatus)
            .Where(entry => origin is null || entry.Origin == origin)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<OwnerAgendaBookingDetail?> GetIndividualBookingAsync(
        int businessId, int bookingId, CancellationToken ct = default)
    {
        BookingRequests.Add((businessId, bookingId));
        var detail = BookingDetails
            .Where(row => row.BusinessId == businessId && row.Detail.BookingId == bookingId)
            .Select(row => row.Detail)
            .FirstOrDefault();
        return Task.FromResult(detail);
    }

    public sealed record AgendaRequest(
        int BusinessId,
        DateOnly RangeStart,
        DateOnly RangeEndExclusive,
        int? WorkerId,
        BookingStatus? BookingStatus,
        AppointmentOrigin? Origin);
}
