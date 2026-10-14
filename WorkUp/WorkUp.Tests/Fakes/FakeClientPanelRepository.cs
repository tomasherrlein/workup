using WorkUp.BusinessLogic.Scheduling;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// In-memory agenda storage keyed by business and client, so a service test proves the read is scoped to the ficha:
/// rows of another client or another tenant are never returned.
/// </summary>
public sealed class FakeClientPanelRepository : IClientPanelRepository
{
    private readonly List<(int BusinessId, int ClientId, ClientPanelBookingRecord Row)> _rows = [];

    public void Add(int businessId, int clientId, ClientPanelBookingRecord row) => _rows.Add((businessId, clientId, row));

    public Task<IReadOnlyList<ClientPanelBookingRecord>> ListAgendaAsync(int businessId, int clientId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ClientPanelBookingRecord>>(
            _rows.Where(entry => entry.BusinessId == businessId && entry.ClientId == clientId)
                .Select(entry => entry.Row)
                .ToList());
}
