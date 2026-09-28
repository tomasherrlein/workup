using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Clients;

/// <summary>One row of a client's booking history (RF-11), joined from <c>Bookings</c>, <c>Appointments</c>, <c>Services</c> and <c>Workers</c>.</summary>
public sealed record ClientBookingHistoryItem(int BookingId, DateTime StartsAt, DateTime EndsAt, string ServiceName, string WorkerName, BookingStatus Status);

/// <summary>One row of a client's payment history (RF-11), joined from <c>Payments</c> and <c>PaymentAnnulments</c>.</summary>
public sealed record ClientPaymentHistoryItem(int PaymentId, DateTime PaidAt, PaymentConcept Concept, decimal Amount, bool IsAnnulled);

/// <summary>
/// Outcome of inserting a client. The email is unique per business (Regla 7), so a create that passed the sequential
/// precheck can still lose the insert to a competing create that committed in between. That lost race is a business
/// outcome, not a 500: the repository reports <see cref="ClientInsertResultType.EmailTaken"/> instead of letting the
/// unique-key violation escape as an unhandled <c>SqlException</c>.
/// </summary>
public enum ClientInsertResultType
{
    Inserted = 1,
    EmailTaken = 2
}

/// <summary>Result of the guarded client insert. <see cref="ClientId"/> is only meaningful when it inserted.</summary>
public sealed record ClientInsertResult(ClientInsertResultType Result, int ClientId)
{
    public static ClientInsertResult Inserted(int clientId) => new(ClientInsertResultType.Inserted, clientId);

    /// <summary>No row was written: the unique email was taken by a competing create that committed after the precheck.</summary>
    public static ClientInsertResult EmailTaken() => new(ClientInsertResultType.EmailTaken, 0);

    public bool IsInserted => Result == ClientInsertResultType.Inserted;
}

/// <summary>
/// Outcome of updating a client. Changing the email can lose the same check-then-write window; the repository reports
/// <see cref="ClientUpdateResultType.EmailTaken"/> so the caller maps it to the existing <c>client_email_taken</c> field
/// error without inventing a different ficha or a different account.
/// </summary>
public enum ClientUpdateResultType
{
    Updated = 1,
    EmailTaken = 2
}

/// <summary>Result of the guarded client update.</summary>
public sealed record ClientUpdateResult(ClientUpdateResultType Result)
{
    public static ClientUpdateResult Updated() => new(ClientUpdateResultType.Updated);

    public static ClientUpdateResult EmailTaken() => new(ClientUpdateResultType.EmailTaken);

    public bool IsUpdated => Result == ClientUpdateResultType.Updated;
}

public interface IClientRepository
{
    /// <summary>
    /// Every client of the business whose first name, last name, email or phone contains <paramref name="query"/>
    /// (case-insensitive), ordered alphabetically by last name then first name. A null or blank query returns every
    /// client.
    /// </summary>
    Task<List<Client>> SearchAsync(int businessId, string? query, CancellationToken ct = default);

    /// <summary>A client, or null when it does not exist or belongs to another business.</summary>
    Task<Client?> GetByIdAsync(int businessId, int id, CancellationToken ct = default);

    /// <summary>A client by its normalized email, or null when there is none in this business (Regla 7).</summary>
    Task<Client?> GetByEmailAsync(int businessId, string email, CancellationToken ct = default);

    /// <summary>Email uniqueness check scoped to the business. Pass the client's own id when editing it.</summary>
    Task<bool> EmailExistsAsync(int businessId, string email, int? excludingId = null, CancellationToken ct = default);

    /// <summary>
    /// Inserts the ficha and returns the new id, or <see cref="ClientInsertResult.EmailTaken"/> when the per-business
    /// <c>UQ_Clients_Email</c> unique constraint rejects the row because a concurrent create took the email first.
    /// Only that constraint is reported as a business outcome; every other storage failure — including a foreign key
    /// violation — propagates.
    /// </summary>
    Task<ClientInsertResult> InsertAsync(Client client, CancellationToken ct = default);

    /// <summary>
    /// Updates FirstName, LastName, Email, Phone and HealthNotes, or returns
    /// <see cref="ClientUpdateResult.EmailTaken"/> when <c>UQ_Clients_Email</c> rejects the new email. The statement is
    /// atomic: a rejected update leaves the stored row unchanged. Only that constraint is reported as a business
    /// outcome; every other storage failure propagates.
    /// </summary>
    Task<ClientUpdateResult> UpdateAsync(Client client, CancellationToken ct = default);

    /// <summary>Whether the client has a linked <c>ClientAccount</c> (D-18): drives "Con cuenta" / "Sin cuenta" on the detail screen.</summary>
    Task<bool> HasAccountAsync(int businessId, int clientId, CancellationToken ct = default);

    /// <summary>Every booking of the client, business and past alike, unsorted (the service splits and orders them using the current time).</summary>
    Task<List<ClientBookingHistoryItem>> ListBookingHistoryAsync(int businessId, int clientId, CancellationToken ct = default);

    /// <summary>Every payment of the client, newest first, with its annulment flag.</summary>
    Task<List<ClientPaymentHistoryItem>> ListPaymentHistoryAsync(int businessId, int clientId, CancellationToken ct = default);
}
