using Microsoft.Data.SqlClient;
using WorkUp.BusinessLogic.Clients;
using WorkUp.Domain.Entities;

namespace WorkUp.Data.Clients;

public sealed class SqlClientNoteRepository(DbSession session) : SqlRepositoryBase(session), IClientNoteRepository
{
    public Task<List<ClientNote>> ListByClientAsync(int clientId, CancellationToken ct = default) =>
        QueryAsync(
            "SELECT Id, ClientId, AppointmentId, Text, CreatedAt FROM ClientNotes WHERE ClientId = @clientId ORDER BY CreatedAt DESC, Id DESC",
            MapNote,
            p => p.AddValue("@clientId", clientId), ct);

    public Task<int> InsertAsync(ClientNote note, CancellationToken ct = default) =>
        InsertAsync(
            """
            INSERT INTO ClientNotes (ClientId, AppointmentId, Text, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@clientId, @appointmentId, @text, @createdAt)
            """,
            p => p.AddValue("@clientId", note.ClientId).AddValue("@appointmentId", note.AppointmentId).AddValue("@text", note.Text).AddValue("@createdAt", note.CreatedAt), ct);

    private static ClientNote MapNote(SqlDataReader reader) => new()
    {
        Id = reader.Get<int>("Id"),
        ClientId = reader.Get<int>("ClientId"),
        AppointmentId = reader.GetNullable<int>("AppointmentId"),
        Text = reader.Get<string>("Text"),
        CreatedAt = reader.Get<DateTime>("CreatedAt")
    };
}
