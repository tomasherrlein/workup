using WorkUp.BusinessLogic.Clients;
using WorkUp.Domain.Entities;

namespace WorkUp.Tests.Fakes;

public sealed class FakeClientRepository : IClientRepository
{
    private readonly List<Client> _clients = [];
    private readonly List<ClientBookingHistoryItem> _bookings = [];
    private readonly List<ClientPaymentHistoryItem> _payments = [];
    private readonly HashSet<(int BusinessId, int ClientId)> _accounts = [];
    private int _nextId = 1;

    public IReadOnlyList<Client> All => _clients;

    /// <summary>
    /// Deterministic model of losing the check-then-insert window: while set, the email precheck reports the email
    /// free and the insert meets the unique key because a competing create committed after the check, so it reports
    /// <see cref="ClientInsertResult.EmailTaken"/> and writes nothing.
    /// </summary>
    public bool LoseEmailRaceOnInsert { get; set; }

    /// <summary>
    /// Same lost window on the edit: while set, <see cref="EmailExistsAsync"/> reports the new email free and
    /// <see cref="UpdateAsync"/> reports <see cref="ClientUpdateResult.EmailTaken"/> because a competing edit
    /// committed after the check.
    /// </summary>
    public bool LoseEmailRaceOnUpdate { get; set; }

    /// <summary>When set, <see cref="InsertAsync"/> throws it unchanged, standing in for an unexpected storage failure.</summary>
    public Exception? InsertFailure { get; set; }

    /// <summary>When set, <see cref="UpdateAsync"/> throws it unchanged, standing in for an unexpected storage failure.</summary>
    public Exception? UpdateFailure { get; set; }

    public void AddBooking(ClientBookingHistoryItem booking) => _bookings.Add(booking);
    public void AddPayment(ClientPaymentHistoryItem payment) => _payments.Add(payment);
    public void SetHasAccount(int businessId, int clientId) => _accounts.Add((businessId, clientId));

    public Task<List<Client>> SearchAsync(int businessId, string? query, CancellationToken ct = default)
    {
        var term = query?.Trim();
        var clients = _clients
            .Where(client => client.BusinessId == businessId)
            .Where(client => string.IsNullOrWhiteSpace(term) || Matches(client, term))
            .OrderBy(client => client.LastName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(client => client.FirstName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult(clients);
    }

    public Task<Client?> GetByIdAsync(int businessId, int id, CancellationToken ct = default) =>
        Task.FromResult(_clients.SingleOrDefault(client => client.BusinessId == businessId && client.Id == id));

    public Task<Client?> GetByEmailAsync(int businessId, string email, CancellationToken ct = default) =>
        Task.FromResult(_clients.SingleOrDefault(client => client.BusinessId == businessId && client.Email == email));

    public Task<bool> EmailExistsAsync(int businessId, string email, int? excludingId = null, CancellationToken ct = default) =>
        Task.FromResult(!LoseEmailRaceOnInsert && !LoseEmailRaceOnUpdate
            && _clients.Any(client => client.BusinessId == businessId && client.Email == email && client.Id != excludingId));

    public Task<ClientInsertResult> InsertAsync(Client client, CancellationToken ct = default)
    {
        if (InsertFailure is not null)
            throw InsertFailure;

        if (LoseEmailRaceOnInsert)
            return Task.FromResult(ClientInsertResult.EmailTaken());

        client.Id = _nextId++;
        _clients.Add(client);
        return Task.FromResult(ClientInsertResult.Inserted(client.Id));
    }

    public Task<ClientUpdateResult> UpdateAsync(Client client, CancellationToken ct = default)
    {
        if (UpdateFailure is not null)
            throw UpdateFailure;

        if (LoseEmailRaceOnUpdate)
            return Task.FromResult(ClientUpdateResult.EmailTaken());

        return Task.FromResult(ClientUpdateResult.Updated());
    }

    public Task<bool> HasAccountAsync(int businessId, int clientId, CancellationToken ct = default) =>
        Task.FromResult(_accounts.Contains((businessId, clientId)));

    public Task<List<ClientBookingHistoryItem>> ListBookingHistoryAsync(int businessId, int clientId, CancellationToken ct = default) =>
        Task.FromResult(_clients.Any(client => client.BusinessId == businessId && client.Id == clientId)
            ? _bookings.ToList()
            : []);

    public Task<List<ClientPaymentHistoryItem>> ListPaymentHistoryAsync(int businessId, int clientId, CancellationToken ct = default) =>
        Task.FromResult(_clients.Any(client => client.BusinessId == businessId && client.Id == clientId)
            ? _payments.ToList()
            : []);

    private static bool Matches(Client client, string term) =>
        client.FirstName.Contains(term, StringComparison.OrdinalIgnoreCase)
        || client.LastName.Contains(term, StringComparison.OrdinalIgnoreCase)
        || client.Email.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (client.Phone?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);
}
