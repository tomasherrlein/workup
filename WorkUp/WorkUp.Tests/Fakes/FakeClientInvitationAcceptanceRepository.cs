using System.Data;
using WorkUp.BusinessLogic.Abstractions;
using WorkUp.BusinessLogic.ClientAccounts;
using WorkUp.BusinessLogic.Security;
using WorkUp.Domain.Entities;

namespace WorkUp.Tests.Fakes;

/// <summary>
/// In-memory invitation-acceptance store (RF-26, slice 2). It mirrors the guards of the SQL port: the tenant-scoped
/// token probe, the account lookup by the invited email, the new-account key guards (invited email and invited ficha)
/// taken before the ficha, the single-use consume that revalidates the usable state under a write gate, the pending
/// bind and the guarded verified insert. Two consumers can be held at a barrier, so a test proves that one token has
/// exactly one winner.
/// <para>
/// Unlike the earlier fakes this one is transactional on purpose: every mutation is applied immediately but recorded
/// as an undo entry on the acceptance transaction of its async flow (<see cref="AsyncLocal{T}"/>), so a rollback
/// really undoes the consumed token, the bound account or the inserted account. That is what lets a test prove that a
/// failed bind or insert leaves the link live instead of only asserting the unit-of-work counters. The model is
/// still an in-memory approximation: it holds no real row lock, and a rollback restores only the mutations of its own
/// transaction, never the effects of a concurrent one.
/// </para>
/// It also keeps the T20 email-verification tokens so a test can prove that an account the invitation just verified
/// and linked can no longer be consumed by the verification guard (which requires a still-pending, still-unlinked
/// account). The verification consume mirrors <c>ConsumeEmailVerificationTokenSql</c>; the real guards and the real
/// rollback can only be proven by a SQL fixture, which this slice does not add.
/// </summary>
public sealed class FakeClientInvitationAcceptanceRepository : IClientInvitationAcceptanceRepository
{
    private readonly List<Client> _clients = [];
    private readonly List<FakeAcceptanceAccount> _accounts = [];
    private readonly List<AccountInvitation> _invitations = [];
    private readonly List<FakeVerificationToken> _verificationTokens = [];
    private readonly object _writeGate = new();
    private readonly AsyncLocal<AcceptanceTransaction?> _transaction = new();
    private TaskCompletionSource? _consumeBarrier;
    private int _consumeAttempts;
    private int _nextClientId = 1;
    private int _nextAccountId = 1;
    private int _nextInvitationId = 1;
    private int _nextVerificationTokenId = 1;

    /// <summary>When true, the verified-account insert reports the guarded "account key already taken" outcome.</summary>
    public bool FailVerifiedAccountInsert { get; set; }

    /// <summary>When true, the pending bind reports the guarded "no longer pending" outcome.</summary>
    public bool FailBind { get; set; }

    /// <summary>Runs inside the ficha lock, so a test can advance the clock while the acceptance is waiting for it.</summary>
    public Action? WhileWaitingForClientLock { get; set; }

    public IReadOnlyList<Client> Clients => _clients;

    public IReadOnlyList<FakeAcceptanceAccount> Accounts => _accounts;

    public IReadOnlyList<AccountInvitation> Invitations => _invitations;

    public FakeAcceptanceUnitOfWork CreateUnitOfWork() => new(this);

    /// <summary>Holds the next two consumes at the same point, so the write gate decides the single winner.</summary>
    public void SynchronizeNextTwoInvitationConsumes()
    {
        _consumeAttempts = 0;
        _consumeBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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

    public FakeAcceptanceAccount SeedAccount(
        int businessId, string email, string passwordHash, int? clientId, DateTime? emailVerifiedAt)
    {
        var account = new ClientAccount
        {
            Id = _nextAccountId++,
            BusinessId = businessId,
            ClientId = clientId,
            Email = email,
            PasswordHash = passwordHash,
            EmailVerifiedAt = emailVerifiedAt,
            CreatedAt = DateTime.MinValue
        };
        var row = new FakeAcceptanceAccount(account);
        _accounts.Add(row);
        return row;
    }

    public FakeAcceptanceAccount SeedPendingAccount(
        int businessId,
        string email,
        string passwordHash,
        string pendingFirstName = "Ana",
        string pendingLastName = "Gómez")
    {
        var row = SeedAccount(businessId, email, passwordHash, clientId: null, emailVerifiedAt: null);
        row.PendingFirstName = pendingFirstName;
        row.PendingLastName = pendingLastName;
        return row;
    }

    public FakeAcceptanceAccount SeedVerifiedAccount(
        int businessId, string email, string passwordHash, int clientId, DateTime verifiedAt) =>
        SeedAccount(businessId, email, passwordHash, clientId, verifiedAt);

    public AccountInvitation SeedInvitation(
        int businessId,
        int clientId,
        string email,
        byte[] tokenHash,
        DateTime expiresAt,
        DateTime? usedAt = null,
        DateTime? revokedAt = null)
    {
        var invitation = new AccountInvitation
        {
            Id = _nextInvitationId++,
            BusinessId = businessId,
            ClientId = clientId,
            Email = email,
            TokenHash = tokenHash,
            ExpiresAt = expiresAt,
            UsedAt = usedAt,
            RevokedAt = revokedAt,
            CreatedAt = DateTime.MinValue
        };
        _invitations.Add(invitation);
        return invitation;
    }

    /// <summary>Stores a T20 email-verification link, so a test can check the account stops matching its guard.</summary>
    public void SeedEmailVerificationToken(int clientAccountId, string rawToken, DateTime expiresAt) =>
        _verificationTokens.Add(new FakeVerificationToken
        {
            Id = _nextVerificationTokenId++,
            ClientAccountId = clientAccountId,
            TokenHash = SecureToken.Hash(rawToken),
            ExpiresAt = expiresAt
        });

    /// <summary>
    /// Mirrors <c>ConsumeEmailVerificationTokenSql</c>: the token is scoped to the business through its account and is
    /// usable only while that account is still pending and unlinked. Returns the account id, or null when it matches
    /// nothing.
    /// </summary>
    public int? ConsumeEmailVerificationToken(byte[] tokenHash, int businessId, DateTime now)
    {
        lock (_writeGate)
        {
            var token = _verificationTokens.SingleOrDefault(candidate => candidate.TokenHash.SequenceEqual(tokenHash));
            if (token is null || token.UsedAt is not null || token.ExpiresAt <= now)
                return null;

            var row = _accounts.SingleOrDefault(account => account.Id == token.ClientAccountId);
            if (row is null || row.BusinessId != businessId)
                return null;
            if (row.Account.ClientId is not null || row.Account.EmailVerifiedAt is not null)
                return null;

            token.UsedAt = now;
            return row.Id;
        }
    }

    public Task<InvitationAcceptanceTarget?> FindInvitationAsync(
        int businessId, byte[] tokenHash, DateTime now, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            var invitation = _invitations.SingleOrDefault(candidate =>
                candidate.BusinessId == businessId &&
                candidate.TokenHash.SequenceEqual(tokenHash) &&
                candidate.IsUsable(now));

            return Task.FromResult(invitation is null
                ? null
                : new InvitationAcceptanceTarget(
                    invitation.Id, invitation.ClientId, invitation.Email, invitation.ExpiresAt));
        }
    }

    public Task<InvitationAccountState?> LockAccountByEmailAsync(
        int businessId, string email, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            var row = _accounts.FirstOrDefault(account =>
                account.BusinessId == businessId && account.Email == email);
            return Task.FromResult(row is null
                ? null
                : new InvitationAccountState(
                    row.Id,
                    row.PasswordHash,
                    row.Account.ClientId is null && row.Account.EmailVerifiedAt is null));
        }
    }

    public Task<InvitationAccountKeyState> LockAccountKeyAsync(
        int businessId, string email, int clientId, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            var emailTaken = _accounts.Any(account =>
                account.BusinessId == businessId && account.Email == email);
            var clientLinked = _accounts.Any(account =>
                account.BusinessId == businessId && account.ClientId == clientId);
            return Task.FromResult(new InvitationAccountKeyState(emailTaken, clientLinked));
        }
    }

    public Task<Client?> LockClientAsync(int businessId, int clientId, CancellationToken ct = default)
    {
        WhileWaitingForClientLock?.Invoke();

        lock (_writeGate)
            return Task.FromResult(_clients.SingleOrDefault(client =>
                client.Id == clientId && client.BusinessId == businessId));
    }

    public async Task<bool> ConsumeInvitationAsync(
        int businessId, int invitationId, DateTime now, CancellationToken ct = default)
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
            var invitation = _invitations.SingleOrDefault(candidate =>
                candidate.Id == invitationId && candidate.BusinessId == businessId);
            if (invitation is null || !invitation.IsUsable(now))
                return false;

            var previousUsedAt = invitation.UsedAt;
            invitation.UsedAt = now;
            _transaction.Value?.Record(() => invitation.UsedAt = previousUsedAt);
            return true;
        }
    }

    public Task<bool> BindPendingAccountAsync(
        int businessId, int clientAccountId, int clientId, DateTime verifiedAt, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            if (FailBind)
                return Task.FromResult(false);

            var row = _accounts.FirstOrDefault(account =>
                account.Id == clientAccountId && account.BusinessId == businessId);
            if (row is null || row.Account.ClientId is not null || row.Account.EmailVerifiedAt is not null)
                return Task.FromResult(false);
            if (!_clients.Any(client => client.Id == clientId && client.BusinessId == businessId))
                return Task.FromResult(false);
            if (_accounts.Any(other => other.Id != clientAccountId && other.ClientId == clientId))
                return Task.FromResult(false);

            var previousClientId = row.Account.ClientId;
            var previousVerifiedAt = row.Account.EmailVerifiedAt;
            var previousFirstName = row.PendingFirstName;
            var previousLastName = row.PendingLastName;

            row.Account.ClientId = clientId;
            row.Account.EmailVerifiedAt = verifiedAt;
            row.PendingFirstName = null;
            row.PendingLastName = null;

            _transaction.Value?.Record(() =>
            {
                row.Account.ClientId = previousClientId;
                row.Account.EmailVerifiedAt = previousVerifiedAt;
                row.PendingFirstName = previousFirstName;
                row.PendingLastName = previousLastName;
            });
            return Task.FromResult(true);
        }
    }

    public Task<int> InsertVerifiedAccountAsync(ClientAccount account, CancellationToken ct = default)
    {
        lock (_writeGate)
        {
            if (FailVerifiedAccountInsert)
                return Task.FromResult(0);
            if (_accounts.Any(existing =>
                    existing.BusinessId == account.BusinessId && existing.Email == account.Email))
                return Task.FromResult(0);
            if (_accounts.Any(existing =>
                    existing.BusinessId == account.BusinessId && existing.ClientId == account.ClientId))
                return Task.FromResult(0);
            if (!_clients.Any(client =>
                    client.Id == account.ClientId && client.BusinessId == account.BusinessId))
                return Task.FromResult(0);

            account.Id = _nextAccountId++;
            var row = new FakeAcceptanceAccount(account);
            _accounts.Add(row);
            _transaction.Value?.Record(() => _accounts.Remove(row));
            return Task.FromResult(account.Id);
        }
    }

    internal void BeginTransaction() => _transaction.Value = new AcceptanceTransaction();

    internal void CommitTransaction() => _transaction.Value = null;

    internal void RollbackTransaction()
    {
        var transaction = _transaction.Value;
        _transaction.Value = null;
        if (transaction is null)
            return;

        lock (_writeGate)
            transaction.Rollback();
    }

    /// <summary>One in-flight acceptance: the undo entries of the mutations applied inside its transaction.</summary>
    private sealed class AcceptanceTransaction
    {
        private readonly List<Action> _undo = [];

        public void Record(Action undo) => _undo.Add(undo);

        public void Rollback()
        {
            for (var index = _undo.Count - 1; index >= 0; index--)
                _undo[index]();
            _undo.Clear();
        }
    }

    private sealed class FakeVerificationToken
    {
        public int Id { get; init; }

        public int ClientAccountId { get; init; }

        public byte[] TokenHash { get; init; } = [];

        public DateTime ExpiresAt { get; init; }

        public DateTime? UsedAt { get; set; }
    }
}

/// <summary>
/// Transactional unit of work for the acceptance fake: it starts the repository transaction on begin and either
/// commits it (dropping the undo entries) or rolls it back (replaying them in reverse), so a failed acceptance
/// really leaves the in-memory model as it was.
/// </summary>
public sealed class FakeAcceptanceUnitOfWork(FakeClientInvitationAcceptanceRepository repository) : IUnitOfWork
{
    public int BeginCount { get; private set; }

    public int CommitCount { get; private set; }

    public int RolledBackCount { get; private set; }

    public Task<ITransaction> BeginAsync(
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken ct = default)
    {
        BeginCount++;
        repository.BeginTransaction();
        return Task.FromResult<ITransaction>(new Transaction(this, repository));
    }

    private sealed class Transaction(
        FakeAcceptanceUnitOfWork owner, FakeClientInvitationAcceptanceRepository repository) : ITransaction
    {
        private bool _committed;

        public Task CommitAsync(CancellationToken ct = default)
        {
            _committed = true;
            owner.CommitCount++;
            repository.CommitTransaction();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                owner.RolledBackCount++;
                repository.RollbackTransaction();
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>One stored account plus the temporary names of the ficha a T20 verification would have created.</summary>
public sealed class FakeAcceptanceAccount(ClientAccount account)
{
    public ClientAccount Account { get; } = account;

    public string? PendingFirstName { get; set; }

    public string? PendingLastName { get; set; }

    public int Id => Account.Id;

    public int BusinessId => Account.BusinessId;

    public string Email => Account.Email;

    public string PasswordHash => Account.PasswordHash;

    public int? ClientId => Account.ClientId;

    public DateTime? EmailVerifiedAt => Account.EmailVerifiedAt;
}
