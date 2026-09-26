using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Domain.Enums;

namespace WorkUp.Data.Scheduling;

/// <summary>
/// Tenant- and client-scoped, read-only agenda for the authenticated client panel (T21.1). A single statement
/// returns every booking of the ficha — individual turns, fixed class dates and drop-ins, past and cancelled
/// included — with no per-row follow-up reads; <c>BusinessId</c> and <c>ClientId</c> are the only row filters and
/// both come from the authenticated session, never from a route or form value. The projection selects no health
/// observation, owner note or private link token.
/// </summary>
public sealed class SqlClientPanelRepository(DbSession session) : SqlRepositoryBase(session), IClientPanelRepository
{
    public async Task<IReadOnlyList<ClientPanelBookingRecord>> ListAgendaAsync(
        int businessId, int clientId, CancellationToken ct = default) =>
        await QueryAsync(
            """
            SELECT booking.Id AS BookingId, a.StartsAt, a.EndsAt, s.Name AS ServiceName,
                   CONCAT(w.FirstName, N' ', w.LastName) AS WorkerName,
                   a.Kind AS AppointmentKind, a.Status AS AppointmentStatus,
                   booking.Status AS BookingStatus, booking.Modality
              FROM Bookings booking
              JOIN Appointments a ON a.Id = booking.AppointmentId AND a.BusinessId = booking.BusinessId
              JOIN Services s ON s.Id = a.ServiceId AND s.BusinessId = booking.BusinessId
              JOIN Workers w ON w.Id = a.WorkerId AND w.BusinessId = booking.BusinessId
             WHERE booking.BusinessId = @businessId AND booking.ClientId = @clientId
            """,
            reader => new ClientPanelBookingRecord(
                reader.Get<int>("BookingId"),
                reader.Get<DateTime>("StartsAt"),
                reader.Get<DateTime>("EndsAt"),
                reader.Get<string>("ServiceName"),
                reader.Get<string>("WorkerName"),
                reader.GetEnum<AppointmentKind>("AppointmentKind"),
                reader.GetEnum<AppointmentStatus>("AppointmentStatus"),
                reader.GetEnum<BookingStatus>("BookingStatus"),
                reader.GetNullableEnum<BookingModality>("Modality")),
            p => p.AddValue("@businessId", businessId).AddValue("@clientId", clientId),
            ct);
}
