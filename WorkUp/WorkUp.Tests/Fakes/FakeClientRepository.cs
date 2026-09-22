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
        Task.FromResult(_clients.Any(client => client.BusinessId == businessId && client.Email == email && client.Id != excludingId));

    public Task<int> InsertAsync(Client client, CancellationToken ct = default)
    {
        client.Id = _nextId++;
        _clients.Add(client);
        return Task.FromResult(client.Id);
    }

    public Task UpdateAsync(Client client, CancellationToken ct = default) => Task.CompletedTask;

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
