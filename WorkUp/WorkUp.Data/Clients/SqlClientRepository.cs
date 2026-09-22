using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Clients;
using WorkUp.Domain.Entities;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Clients;

public sealed class SqlClientRepository(DbSession session) : SqlRepositoryBase(session), IClientRepository
{
    private const string ClientColumns = "Id, BusinessId, FirstName, LastName, Email, Phone, HealthNotes, CreatedAt";

    public Task<List<Client>> SearchAsync(int businessId, string? query, CancellationToken ct = default)
    {
        var term = string.IsNullOrWhiteSpace(query) ? null : $"%{query.Trim()}%";
        return QueryAsync(
            $"SELECT {ClientColumns} FROM Clients WHERE BusinessId = @businessId AND (@term IS NULL OR FirstName LIKE @term OR LastName LIKE @term OR Email LIKE @term OR Phone LIKE @term) ORDER BY LastName, FirstName",
            MapClient,
            p => p.AddValue("@businessId", businessId).AddValue("@term", term),
            ct);
    }

    public Task<Client?> GetByIdAsync(int businessId, int id, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync($"SELECT {ClientColumns} FROM Clients WHERE BusinessId = @businessId AND Id = @id", MapClient,
            p => p.AddValue("@businessId", businessId).AddValue("@id", id), ct);

    public Task<Client?> GetByEmailAsync(int businessId, string email, CancellationToken ct = default) =>
        QuerySingleOrDefaultAsync($"SELECT {ClientColumns} FROM Clients WHERE BusinessId = @businessId AND Email = @email", MapClient,
            p => p.AddValue("@businessId", businessId).AddValue("@email", email), ct);

    public async Task<bool> EmailExistsAsync(int businessId, string email, int? excludingId = null, CancellationToken ct = default) =>
        await ScalarAsync<int>("SELECT COUNT(*) FROM Clients WHERE BusinessId = @businessId AND Email = @email AND (@excludingId IS NULL OR Id <> @excludingId)",
            p => p.AddValue("@businessId", businessId).AddValue("@email", email).AddValue("@excludingId", excludingId), ct) > 0;

    public Task<int> InsertAsync(Client client, CancellationToken ct = default) =>
        InsertAsync(
            """
            INSERT INTO Clients (BusinessId, FirstName, LastName, Email, Phone, HealthNotes, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@businessId, @firstName, @lastName, @email, @phone, @healthNotes, @createdAt)
            """,
            p => p.AddValue("@businessId", client.BusinessId).AddValue("@firstName", client.FirstName).AddValue("@lastName", client.LastName)
                .AddValue("@email", client.Email).AddValue("@phone", client.Phone).AddValue("@healthNotes", client.HealthNotes).AddValue("@createdAt", client.CreatedAt), ct);

    public Task UpdateAsync(Client client, CancellationToken ct = default) =>
        ExecuteAsync(
            """
            UPDATE Clients
               SET FirstName = @firstName, LastName = @lastName, Email = @email, Phone = @phone, HealthNotes = @healthNotes
             WHERE Id = @id AND BusinessId = @businessId
            """,
            p => p.AddValue("@id", client.Id).AddValue("@businessId", client.BusinessId).AddValue("@firstName", client.FirstName)
                .AddValue("@lastName", client.LastName).AddValue("@email", client.Email).AddValue("@phone", client.Phone).AddValue("@healthNotes", client.HealthNotes), ct);

    public async Task<bool> HasAccountAsync(int businessId, int clientId, CancellationToken ct = default) =>
        await ScalarAsync<int>("SELECT COUNT(*) FROM ClientAccounts WHERE BusinessId = @businessId AND ClientId = @clientId",
            p => p.AddValue("@businessId", businessId).AddValue("@clientId", clientId), ct) > 0;

    public Task<List<ClientBookingHistoryItem>> ListBookingHistoryAsync(int businessId, int clientId, CancellationToken ct = default) =>
        QueryAsync(
            """
            SELECT b.Id AS BookingId, a.StartsAt, a.EndsAt, s.Name AS ServiceName,
                   CONCAT(w.FirstName, ' ', w.LastName) AS WorkerName, b.Status
              FROM Bookings b
              JOIN Clients c ON c.Id = b.ClientId AND c.BusinessId = b.BusinessId
              JOIN Appointments a ON a.Id = b.AppointmentId AND a.BusinessId = b.BusinessId
              JOIN Services s ON s.Id = a.ServiceId AND s.BusinessId = b.BusinessId
              JOIN Workers w ON w.Id = a.WorkerId AND w.BusinessId = b.BusinessId
             WHERE b.BusinessId = @businessId AND c.Id = @clientId
            """,
            reader => new ClientBookingHistoryItem(reader.Get<int>("BookingId"), reader.Get<DateTime>("StartsAt"), reader.Get<DateTime>("EndsAt"),
                reader.Get<string>("ServiceName"), reader.Get<string>("WorkerName"), reader.GetEnum<BookingStatus>("Status")),
            p => p.AddValue("@businessId", businessId).AddValue("@clientId", clientId), ct);

    public Task<List<ClientPaymentHistoryItem>> ListPaymentHistoryAsync(int businessId, int clientId, CancellationToken ct = default) =>
        QueryAsync(
            """
            SELECT p.Id AS PaymentId, p.PaidAt, p.Concept, p.Amount,
                   CAST(CASE WHEN pa.Id IS NULL THEN 0 ELSE 1 END AS bit) AS IsAnnulled
              FROM Payments p
              LEFT JOIN Bookings b ON b.Id = p.BookingId AND b.BusinessId = p.BusinessId
              LEFT JOIN Enrollments e ON e.Id = p.EnrollmentId AND e.BusinessId = p.BusinessId
              JOIN Clients c ON c.BusinessId = @businessId AND c.Id = @clientId
              LEFT JOIN PaymentAnnulments pa ON pa.PaymentId = p.Id
             WHERE p.BusinessId = @businessId
               AND (p.ClientId = c.Id OR b.ClientId = c.Id OR e.ClientId = c.Id)
             ORDER BY p.PaidAt DESC
            """,
            reader => new ClientPaymentHistoryItem(reader.Get<int>("PaymentId"), reader.Get<DateTime>("PaidAt"), reader.GetEnum<PaymentConcept>("Concept"),
                reader.Get<decimal>("Amount"), reader.Get<bool>("IsAnnulled")),
            p => p.AddValue("@businessId", businessId).AddValue("@clientId", clientId), ct);

    private static Client MapClient(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        BusinessId = reader.Get<int>("BusinessId"),
        FirstName = reader.Get<string>("FirstName"),
        LastName = reader.Get<string>("LastName"),
        Email = reader.Get<string>("Email"),
        Phone = reader.GetNullableString("Phone"),
        HealthNotes = reader.GetNullableString("HealthNotes"),
        CreatedAt = reader.Get<DateTime>("CreatedAt")
    };
}
