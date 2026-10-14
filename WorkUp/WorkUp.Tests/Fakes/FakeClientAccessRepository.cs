using WorkUp.BusinessLogic.ClientAccounts;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// In-memory client-access store (RF-15, RF-27). It mirrors the guards of the SQL port: the tenant/email key of an
/// active account, the single-use consumption of an unexpired <see cref="AccountTokenPurpose.PasswordReset"/> token
/// scoped to one business, the rotation that invalidates the previous unused recovery links under one lock, and the
/// unique notification dedup key.
///
/// LIMITATION: an in-memory fake never rolls back. Everything written inside a transaction that is never committed
/// stays in these lists, so a rollback test can assert the unit-of-work contract (no commit, one rollback) but never
/// the absence of the write or the restoration of a consumed token. Only a SQL fixture can prove the rollback itself.
/// </summary>
public sealed class FakeClientAccessRepository : IClientAccessRepository
{
    private readonly List<ClientAccount> _accounts = [];
    private readonly List<AccountToken> _tokens = [];
    private readonly List<Client> _clients = [];
    private readonly List<Notification> _notifications = [];
    private readonly object _writeGate = new();
    private int _nextAccountId = 1;
    private int _nextClientId = 1;
    private int _nextTokenId = 1;

    /// <summary>When true, the password replacement reports the guarded "account is not active" outcome.</summary>
    public bool FailPasswordUpdate { get; set; }

    /// <summary>
    /// Runs before a notice insert, so a test can simulate a technical retry that re-enqueues the same notice and
    /// therefore the same dedup key.
    /// </summary>
    public Action<Notification>? BeforeNotificationInsert { get; set; }

    public IReadOnlyList<ClientAccount> Accounts => _accounts;

    public IReadOnlyList<AccountToken> Tokens => _tokens;

    public IReadOnlyList<Client> Clients => _clients;

    public IReadOnlyList<Notification> Notifications => _notifications;

    /// <summary>Verified account linked to its own ficha: the only state allowed to sign in (RF-15).</summary>
    public ClientAccount SeedActiveAccount(
        int businessId,
        string email,
        string passwordHash,
        DateTime verifiedAt,
        string firstName = "Ana",
        string lastName = "Gómez")
    {
        var client = SeedClient(businessId, firstName, lastName, email);
        return SeedAccount(businessId, email, passwordHash, verifiedAt, client.Id);
    }

    /// <summary>Account whose email was never verified: it can neither sign in nor receive a recovery link.</summary>
    public ClientAccount SeedPendingAccount(
        int businessId, string email, string passwordHash, DateTime createdAt) =>
        SeedAccount(businessId, email, passwordHash, null, null, createdAt);

    /// <summary>Account with a verified email that is still not linked to a ficha: inactive for RF-15 and RF-27.</summary>
    public ClientAccount SeedUnlinkedAccount(
        int businessId, string email, string passwordHash, DateTime verifiedAt) =>
        SeedAccount(businessId, email, passwordHash, verifiedAt, null, verifiedAt);

    public ClientAccount SeedAccount(
        int businessId,
        string email,
        string passwordHash,
        DateTime? verifiedAt,
        int? clientId,
        DateTime? createdAt = null)
    {
        var account = new ClientAccount
        {
            Id = _nextAccountId++,
            BusinessId = businessId,
            ClientId = clientId,
            Email = email,
            PasswordHash = passwordHash,
            EmailVerifiedAt = verifiedAt,
            CreatedAt = createdAt ?? DateTime.MinValue
        };
        _accounts.Add(account);
        return account;
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
            Type = NotificationType.PasswordReset,
            Recipient = "ana@example.com",
            Subject = "Ya encolado",
            BodyHtml = "<p>Ya encolado</p>",
            DedupKey = dedupKey,
            CreatedAt = DateTime.MinValue
        };
        _notifications.Add(notification);
        return notification;
    }

    public Task<ActiveClientAccount?> GetActiveAccountByEmailAsync(
        int businessId, string normalizedEmail, CancellationToken ct = default)
    {
        var account = _accounts.SingleOrDefault(candidate => candidate.BusinessId == businessId &&
            candidate.Email == normalizedEmail && candidate.ClientId is not null && candidate.EmailVerifiedAt is not null);
        return Task.FromResult(account is null
            ? null
            : new ActiveClientAccount(account.Id, account.ClientId!.Value, account.Email, account.PasswordHash));
    }

    public Task<bool> ReplacePasswordResetTokenAsync(
        int businessId, int clientAccountId, AccountToken replacement, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            var account = FindActiveAccount(clientAccountId, businessId);
            if (account is null)
                return Task.FromResult(false);

            // Rotation: every previous unused recovery link stops working at the instant of the replacement, so two
            // simultaneous requests can never leave two live links.
            foreach (var token in _tokens.Where(token => token.ClientAccountId == clientAccountId &&
                token.Purpose == AccountTokenPurpose.PasswordReset && token.UsedAt is null))
                token.UsedAt = replacement.CreatedAt;

            replacement.Id = _nextTokenId++;
            _tokens.Add(replacement);
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

    public Task<int?> ConsumePasswordResetTokenAsync(
        byte[] tokenHash, int businessId, DateTime now, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            var token = _tokens.SingleOrDefault(candidate => candidate.TokenHash.SequenceEqual(tokenHash));
            var account = token is null ? null : FindActiveAccount(token.ClientAccountId, businessId);
            if (token is null || account is null ||
                token.Purpose != AccountTokenPurpose.PasswordReset || !token.IsUsable(now))
                return Task.FromResult<int?>(null);

            token.UsedAt = now;
            return Task.FromResult<int?>(account.Id);
        }
    }

    public Task<bool> UpdatePasswordHashAsync(
        int businessId, int clientAccountId, string passwordHash, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            var account = FindActiveAccount(clientAccountId, businessId);
            if (account is null || FailPasswordUpdate)
                return Task.FromResult(false);

            account.PasswordHash = passwordHash;
            return Task.FromResult(true);
        }
    }

    private ClientAccount? FindActiveAccount(int clientAccountId, int businessId) =>
        _accounts.SingleOrDefault(account => account.Id == clientAccountId && account.BusinessId == businessId &&
            account.ClientId is not null && account.EmailVerifiedAt is not null);
}
