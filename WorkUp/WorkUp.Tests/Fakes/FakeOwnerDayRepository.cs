using WorkUp.BusinessLogic.Scheduling;

namespace WorkUp.Tests.Fakes;

public sealed class FakeOwnerDayRepository : IOwnerDayRepository
{
    public int TenantBusinessId { get; set; } = 7;
    public decimal PaidTotal { get; set; }
    public List<OwnerDayRow> Rows { get; } = [];
    public List<(int BusinessId, DateOnly Date)> DayRequests { get; } = [];
    public List<(int BusinessId, DateOnly Date)> PaidTotalRequests { get; } = [];

    public Task<IReadOnlyList<OwnerDayRow>> ListDayAsync(
        int businessId, DateOnly date, CancellationToken ct = default)
    {
        DayRequests.Add((businessId, date));
        IReadOnlyList<OwnerDayRow> rows = businessId == TenantBusinessId
            ? Rows.Where(row => DateOnly.FromDateTime(row.StartsAt) == date).ToArray()
            : [];
        return Task.FromResult(rows);
    }

    public Task<decimal> GetPaidTotalAsync(int businessId, DateOnly date, CancellationToken ct = default)
    {
        PaidTotalRequests.Add((businessId, date));
        return Task.FromResult(businessId == TenantBusinessId ? PaidTotal : 0m);
    }
}
