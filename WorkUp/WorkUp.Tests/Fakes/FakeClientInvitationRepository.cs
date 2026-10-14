using WorkUp.BusinessLogic.ClientAccounts;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// In-memory invitation store (RF-26). It mirrors the guards of the SQL port: the tenant-scoped ficha lookup, the
/// linked-account read used before the lock, the single-writer section that revokes every live invitation of one ficha
/// and inserts the replacement (so at most one invitation of a ficha is live), and the unique notification dedup key.
/// Two issuers can be held at a barrier, so a test proves the serialization under a real interleaving.
///
/// LIMITATION: an in-memory fake never rolls back. Everything written inside a transaction that is never committed
/// stays in these lists, so a rollback test can assert the unit-of-work contract (no commit, one rollback) but never
/// the absence of the row. Only a SQL fixture can prove the rollback itself. The fake also cannot hold a row lock
/// across statements, so it proves the single-live rule only under the <c>_writeGate</c> of one process.
/// </summary>
public sealed class FakeClientInvitationRepository : IClientInvitationRepository
{
    private readonly List<FakeInvitationClient> _clients = [];
    private readonly List<AccountInvitation> _invitations = [];
    private readonly List<Notification> _notifications = [];
    private readonly object _writeGate = new();
    private TaskCompletionSource? _issueBarrier;
    private int _issueAttempts;
    private int _nextClientId = 1;
    private int _nextInvitationId = 1;

    /// <summary>Runs inside the single-writer section, before the previous invitations are revoked.</summary>
    public Action? BeforeInvitationInsert { get; set; }

    /// <summary>
    /// Runs before a notice insert, so a test can simulate a technical retry that re-enqueues the same notice (same
    /// dedup key) or a failing outbox.
    /// </summary>
    public Action<Notification>? BeforeNotificationInsert { get; set; }

    public IReadOnlyList<FakeInvitationClient> Clients => _clients;

    public IReadOnlyList<AccountInvitation> Invitations => _invitations;

    public IReadOnlyList<Notification> Notifications => _notifications;

    /// <summary>Holds the next two issuers at the same point, so the writer gate decides the single live invitation.</summary>
    public void SynchronizeNextTwoIssues()
    {
        _issueAttempts = 0;
        _issueBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public FakeInvitationClient SeedClient(
        int businessId,
        string firstName,
        string lastName,
        string email,
        bool hasLinkedAccount = false)
    {
        var client = new Client
        {
            Id = _nextClientId++,
            BusinessId = businessId,
            FirstName = firstName,
            LastName = lastName,
            Email = email,
            CreatedAt = DateTime.MinValue
        };
        var row = new FakeInvitationClient(client) { HasLinkedAccount = hasLinkedAccount };
        _clients.Add(row);
        return row;
    }

    /// <summary>Puts an already queued notice in the outbox, so a repeated dedup key has to be ignored.</summary>
    public Notification SeedQueuedNotification(string dedupKey)
    {
        var notification = new Notification
        {
            BusinessId = null,
            Type = NotificationType.AccountInvitation,
            Recipient = "ana@example.com",
            Subject = "Ya encolado",
            BodyHtml = "<p>Ya encolado</p>",
            DedupKey = dedupKey,
            CreatedAt = DateTime.MinValue
        };
        _notifications.Add(notification);
        return notification;
    }

    public Task<Client?> GetClientAsync(int businessId, int clientId, CancellationToken ct = default) =>
        Task.FromResult(FindClient(businessId, clientId)?.Client);

    public Task<bool> HasLinkedAccountAsync(int businessId, int clientId, CancellationToken ct = default) =>
        Task.FromResult(FindClient(businessId, clientId)?.HasLinkedAccount ?? false);

    public async Task<int> IssueAsync(AccountInvitation invitation, CancellationToken ct = default)
    {
        var barrier = Volatile.Read(ref _issueBarrier);
        if (barrier is not null)
        {
            if (Interlocked.Increment(ref _issueAttempts) == 2)
            {
                barrier.TrySetResult();
                Interlocked.CompareExchange(ref _issueBarrier, null, barrier);
            }

            await barrier.Task;
        }

        lock (_writeGate)
        {
            if (FindClient(invitation.BusinessId, invitation.ClientId) is null)
                return 0;

            BeforeInvitationInsert?.Invoke();

            // Reissuing invalidates the previous one: the revocation and the insert happen in the same single-writer
            // section, so a ficha never accumulates two live invitations.
            RevokeLive(invitation.ClientId, invitation.BusinessId, invitation.CreatedAt);
            invitation.Id = _nextInvitationId++;
            _invitations.Add(invitation);
            return invitation.Id;
        }
    }

    public Task<bool> RevokeAsync(int businessId, int clientId, DateTime now, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            if (FindClient(businessId, clientId) is null)
                return Task.FromResult(false);

            RevokeLive(clientId, businessId, now);
            return Task.FromResult(true);
        }
    }

    public Task<int> InsertNotificationAsync(Notification notification, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            BeforeNotificationInsert?.Invoke(notification);

            if (_notifications.Any(existing => existing.DedupKey == notification.DedupKey))
                return Task.FromResult(0);

            _notifications.Add(notification);
            return Task.FromResult(_notifications.Count);
        }
    }

    private void RevokeLive(int clientId, int businessId, DateTime now)
    {
        foreach (var invitation in _invitations.Where(invitation => invitation.ClientId == clientId &&
            invitation.BusinessId == businessId && invitation.RevokedAt is null && invitation.UsedAt is null))
            invitation.RevokedAt = now;
    }

    private FakeInvitationClient? FindClient(int businessId, int clientId) =>
        _clients.SingleOrDefault(client => client.Id == clientId && client.BusinessId == businessId);
}

/// <summary>One stored ficha plus whether it already has a linked account, which is what the invitation must reject.</summary>
public sealed class FakeInvitationClient(Client client)
{
    public Client Client { get; } = client;

    public bool HasLinkedAccount { get; set; }

    public int Id => Client.Id;

    public int BusinessId => Client.BusinessId;

    public string Email => Client.Email;
}
