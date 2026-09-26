namespace WorkUp.BusinessLogic.Scheduling;

/// <summary>Persistence boundary for the read-only client panel agenda (T21.1).</summary>
public interface IClientPanelRepository
{
    /// <summary>
    /// Every booking of <paramref name="clientId"/> inside <paramref name="businessId"/> in one read: individual
    /// turns, fixed class dates and drop-ins, future, past and cancelled alike, unordered. Both ids are the trusted
    /// identity of the authenticated client session, never a route or form value, so one client can never read
    /// another's agenda.
    /// </summary>
    Task<IReadOnlyList<ClientPanelBookingRecord>> ListAgendaAsync(int businessId, int clientId, CancellationToken ct = default);
}
