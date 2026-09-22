using WorkUp.Domain.Entities;

namespace WorkUp.BusinessLogic.Clients;

public interface IClientNoteRepository
{
    /// <summary>Every note of the client, newest first.</summary>
    Task<List<ClientNote>> ListByClientAsync(int clientId, CancellationToken ct = default);

    Task<int> InsertAsync(ClientNote note, CancellationToken ct = default);
}
