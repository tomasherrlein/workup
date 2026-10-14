using WorkUp.BusinessLogic.ClientAccounts;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// In-memory client-registration store (RF-25). It mirrors the guards of the SQL repository: the tenant/email keys of
/// <c>ClientAccounts</c> and <c>Clients</c>, the unique token hash, the unique notification dedup key, and the
/// single-use consumption of an unexpired <see cref="AccountTokenPurpose.EmailVerification"/> token scoped to one
/// business. Two callers can be held at a barrier, so a test proves the guards under a real interleaving.
///
/// LIMITATION: an in-memory fake never rolls back. Everything written inside a transaction that is never committed
/// stays in these lists, so a rollback test can assert the unit-of-work contract (no commit, one rollback) but never
/// the absence of the row. Only a SQL fixture can prove the rollback itself.
/// </summary>
public sealed class FakeClientRegistrationRepository : IClientRegistrationRepository
{
    private readonly List<FakeClientAccount> _accounts = [];
    private readonly List<AccountToken> _tokens = [];
    private readonly List<Client> _clients = [];
    private readonly List<Notification> _notifications = [];
    private readonly object _writeGate = new();
    private TaskCompletionSource? _emailStateBarrier;
    private int _emailStateReads;
    private TaskCompletionSource? _consumeBarrier;
    private int _consumeAttempts;
    private int _nextAccountId = 1;
    private int _nextClientId = 1;
    private int _nextTokenId = 1;

    /// <summary>When true, every ficha insert reports the guarded "email already taken" outcome.</summary>
    public bool FailClientInsert { get; set; }

    /// <summary>When true, the token insert reports the guarded "hash already stored" outcome.</summary>
    public bool FailTokenInsert { get; set; }

    /// <summary>When true, rotation reports that the account is no longer pending.</summary>
    public bool FailTokenRotation { get; set; }

    /// <summary>Runs before a ficha insert, so a test can simulate a concurrent booking that committed first.</summary>
    public Action? BeforeClientInsert { get; set; }

    /// <summary>
    /// Runs before a notice insert, so a test can simulate a technical retry that re-enqueues the same notice and
    /// therefore the same dedup key.
    /// </summary>
    public Action<Notification>? BeforeNotificationInsert { get; set; }

    public IReadOnlyList<FakeClientAccount> Accounts => _accounts;

    public IReadOnlyList<AccountToken> Tokens => _tokens;

    public IReadOnlyList<Client> Clients => _clients;

    public IReadOnlyList<Notification> Notifications => _notifications;

    /// <summary>Holds the next two email-state reads until both returned, so both callers pass the "free email" check.</summary>
    public void SynchronizeNextTwoEmailStateReads()
    {
        _emailStateReads = 0;
        _emailStateBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Holds the next two token-consume attempts at the same point, so the gate decides the single winner.</summary>
    public void SynchronizeNextTwoTokenConsumes()
    {
        _consumeAttempts = 0;
        _consumeBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public FakeClientAccount SeedPendingAccount(
        int businessId,
        string email,
        string passwordHash,
        string? pendingFirstName,
        string? pendingLastName,
        DateTime createdAt)
    {
        var account = new ClientAccount
        {
            Id = _nextAccountId++,
            BusinessId = businessId,
            ClientId = null,
            Email = email,
            PasswordHash = passwordHash,
            EmailVerifiedAt = null,
            CreatedAt = createdAt
        };
        var row = new FakeClientAccount(account, pendingFirstName, pendingLastName);
        _accounts.Add(row);
        return row;
    }

    public FakeClientAccount SeedVerifiedAccount(
        int businessId,
        string email,
        string passwordHash,
        DateTime verifiedAt,
        string firstName = "Ana",
        string lastName = "Gómez")
    {
        var client = SeedClient(businessId, firstName, lastName, email);
        var account = new ClientAccount
        {
            Id = _nextAccountId++,
            BusinessId = businessId,
            ClientId = client.Id,
            Email = email,
            PasswordHash = passwordHash,
            EmailVerifiedAt = verifiedAt,
            CreatedAt = verifiedAt
        };
        var row = new FakeClientAccount(account, null, null);
        _accounts.Add(row);
        return row;
    }

    public Client SeedClient(int businessId, string firstName, string lastName, string email)
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
        _clients.Add(client);
        return client;
    }

    public AccountToken SeedToken(
        int clientAccountId,
        AccountTokenPurpose purpose,
        string rawToken,
        DateTime expiresAt,
        DateTime createdAt)
    {
        var token = new AccountToken
        {
            Id = _nextTokenId++,
            ClientAccountId = clientAccountId,
            Purpose = purpose,
            TokenHash = SecureToken.Hash(rawToken),
            ExpiresAt = expiresAt,
            CreatedAt = createdAt
        };
        _tokens.Add(token);
        return token;
    }

    /// <summary>Puts an already queued notice in the outbox, so a repeated dedup key has to be ignored.</summary>
    public Notification SeedQueuedNotification(string dedupKey)
    {
        var notification = new Notification
        {
            BusinessId = null,
            Type = NotificationType.EmailVerification,
            Recipient = "ana@example.com",
            Subject = "Ya encolado",
            BodyHtml = "<p>Ya encolado</p>",
            DedupKey = dedupKey,
            CreatedAt = DateTime.MinValue
        };
        _notifications.Add(notification);
        return notification;
    }

    public async Task<ClientRegistrationEmailState> GetEmailStateAsync(
        int businessId, string normalizedEmail, CancellationToken ct = default)
    {
        // The read happens before the barrier on purpose: two concurrent registrations must both see a free email and
        // then race on the guarded insert, which is the case the port has to protect.
        var state = new ClientRegistrationEmailState(
            _clients.Any(client => client.BusinessId == businessId && client.Email == normalizedEmail),
            _accounts.Any(account => account.BusinessId == businessId && account.Email == normalizedEmail));

        var barrier = Volatile.Read(ref _emailStateBarrier);
        if (barrier is null)
            return state;

        if (Interlocked.Increment(ref _emailStateReads) == 2)
        {
            barrier.TrySetResult();
            Interlocked.CompareExchange(ref _emailStateBarrier, null, barrier);
        }

        await barrier.Task;
        return state;
    }

    public Task<int> InsertPendingAccountAsync(
        ClientAccount account, string pendingFirstName, string pendingLastName, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            if (_accounts.Any(row => row.BusinessId == account.BusinessId && row.Email == account.Email) ||
                _clients.Any(client => client.BusinessId == account.BusinessId && client.Email == account.Email))
                return Task.FromResult(0);

            account.Id = _nextAccountId++;
            _accounts.Add(new FakeClientAccount(account, pendingFirstName, pendingLastName));
            return Task.FromResult(account.Id);
        }
    }

    public Task<int> InsertAccountTokenAsync(AccountToken token, CancellationToken ct = default)
    {
        if (FailTokenInsert)
            return Task.FromResult(0);

        lock (_writeGate)
        {
            if (_tokens.Any(existing => existing.TokenHash.SequenceEqual(token.TokenHash)))
                return Task.FromResult(0);

            token.Id = _nextTokenId++;
            _tokens.Add(token);
            return Task.FromResult(token.Id);
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

    public async Task<PendingClientAccount?> ConsumeEmailVerificationTokenAsync(
        byte[] tokenHash, int businessId, DateTime now, CancellationToken ct = default)
    {
        var barrier = Volatile.Read(ref _consumeBarrier);
        if (barrier is not null)
        {
            if (Interlocked.Increment(ref _consumeAttempts) == 2)
            {
                barrier.TrySetResult();
                Interlocked.CompareExchange(ref _consumeBarrier, null, barrier);
            }

            await barrier.Task;
        }

        lock (_writeGate)
        {
            var token = _tokens.SingleOrDefault(candidate => candidate.TokenHash.SequenceEqual(tokenHash));
            var row = token is null ? null : _accounts.SingleOrDefault(account => account.Id == token.ClientAccountId);
            var consumed = token is not null && row is not null && row.BusinessId == businessId &&
                token.Purpose == AccountTokenPurpose.EmailVerification && token.IsUsable(now) &&
                row.ClientId is null && row.EmailVerifiedAt is null
                ? new PendingClientAccount(row.Id, row.Email, row.PendingFirstName, row.PendingLastName)
                : null;

            if (consumed is not null)
                token!.UsedAt = now;

            return consumed;
        }
    }

    public Task<Client?> GetClientByEmailAsync(int businessId, string normalizedEmail, CancellationToken ct = default) =>
        Task.FromResult(_clients.SingleOrDefault(client =>
            client.BusinessId == businessId && client.Email == normalizedEmail));

    public Task<int> InsertClientAsync(Client client, CancellationToken ct = default)
    {
        if (FailClientInsert)
            return Task.FromResult(0);

        lock (_writeGate)
        {
            BeforeClientInsert?.Invoke();

            if (_clients.Any(existing => existing.BusinessId == client.BusinessId && existing.Email == client.Email))
                return Task.FromResult(0);

            client.Id = _nextClientId++;
            _clients.Add(client);
            return Task.FromResult(client.Id);
        }
    }

    public Task<bool> ActivateVerifiedAccountAsync(
        int businessId, int clientAccountId, int clientId, DateTime verifiedAt, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            var row = _accounts.SingleOrDefault(account =>
                account.Id == clientAccountId && account.BusinessId == businessId);
            if (row is null || row.ClientId is not null || row.EmailVerifiedAt is not null)
                return Task.FromResult(false);
            if (!_clients.Any(client => client.Id == clientId && client.BusinessId == businessId))
                return Task.FromResult(false);
            if (_accounts.Any(other => other.Id != clientAccountId && other.ClientId == clientId))
                return Task.FromResult(false);

            row.Account.ClientId = clientId;
            row.Account.EmailVerifiedAt = verifiedAt;
            row.PendingFirstName = null;
            row.PendingLastName = null;
            return Task.FromResult(true);
        }
    }

    public Task<PendingClientAccount?> GetPendingAccountAsync(
        int businessId, string normalizedEmail, CancellationToken ct = default)
    {
        var row = _accounts.SingleOrDefault(account => account.BusinessId == businessId &&
            account.Email == normalizedEmail && account.ClientId is null && account.EmailVerifiedAt is null);
        return Task.FromResult(row is null
            ? null
            : new PendingClientAccount(row.Id, row.Email, row.PendingFirstName, row.PendingLastName));
    }

    public Task<bool> ReplaceVerificationTokenAsync(
        int businessId, int clientAccountId, AccountToken replacement, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            var row = _accounts.SingleOrDefault(account => account.Id == clientAccountId &&
                account.BusinessId == businessId && account.ClientId is null && account.EmailVerifiedAt is null);
            if (row is null || FailTokenRotation)
                return Task.FromResult(false);

            // Rotation: every previous unused verification link stops working at the instant of the replacement.
            foreach (var token in _tokens.Where(token => token.ClientAccountId == clientAccountId &&
                token.Purpose == AccountTokenPurpose.EmailVerification && token.UsedAt is null))
                token.UsedAt = replacement.CreatedAt;

            replacement.Id = _nextTokenId++;
            _tokens.Add(replacement);
            return Task.FromResult(true);
        }
    }
}

/// <summary>One stored account plus the temporary names of the ficha it will create when the email is verified.</summary>
public sealed class FakeClientAccount(ClientAccount account, string? pendingFirstName, string? pendingLastName)
{
    public ClientAccount Account { get; } = account;

    public string? PendingFirstName { get; set; } = pendingFirstName;

    public string? PendingLastName { get; set; } = pendingLastName;

    public int Id => Account.Id;

    public int BusinessId => Account.BusinessId;

    public string Email => Account.Email;

    public string PasswordHash => Account.PasswordHash;

    public int? ClientId => Account.ClientId;

    public DateTime? EmailVerifiedAt => Account.EmailVerifiedAt;
}
