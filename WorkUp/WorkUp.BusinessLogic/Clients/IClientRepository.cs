using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.BusinessLogic.Clients;

/// <summary>One row of a client's booking history (RF-11), joined from <c>Bookings</c>, <c>Appointments</c>, <c>Services</c> and <c>Workers</c>.</summary>
public sealed record ClientBookingHistoryItem(int BookingId, DateTime StartsAt, DateTime EndsAt, string ServiceName, string WorkerName, BookingStatus Status);

/// <summary>One row of a client's payment history (RF-11), joined from <c>Payments</c> and <c>PaymentAnnulments</c>.</summary>
public sealed record ClientPaymentHistoryItem(int PaymentId, DateTime PaidAt, PaymentConcept Concept, decimal Amount, bool IsAnnulled);

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

    Task<int> InsertAsync(Client client, CancellationToken ct = default);

    /// <summary>Updates FirstName, LastName, Email, Phone and HealthNotes.</summary>
    Task UpdateAsync(Client client, CancellationToken ct = default);

    /// <summary>Whether the client has a linked <c>ClientAccount</c> (D-18): drives "Con cuenta" / "Sin cuenta" on the detail screen.</summary>
    Task<bool> HasAccountAsync(int businessId, int clientId, CancellationToken ct = default);

    /// <summary>Every booking of the client, business and past alike, unsorted (the service splits and orders them using the current time).</summary>
    Task<List<ClientBookingHistoryItem>> ListBookingHistoryAsync(int businessId, int clientId, CancellationToken ct = default);

    /// <summary>Every payment of the client, newest first, with its annulment flag.</summary>
    Task<List<ClientPaymentHistoryItem>> ListPaymentHistoryAsync(int businessId, int clientId, CancellationToken ct = default);
}
