using WorkUp.BusinessLogic.Clients;
using WorkUp.Domain.Entities;

namespace WorkUp.Tests.Fakes;

public sealed class FakeClientNoteRepository : IClientNoteRepository
{
    private readonly List<ClientNote> _notes = [];
    private int _nextId = 1;

    public IReadOnlyList<ClientNote> All => _notes;

    public Task<List<ClientNote>> ListByClientAsync(int clientId, CancellationToken ct = default) =>
        Task.FromResult(_notes.Where(note => note.ClientId == clientId).OrderByDescending(note => note.CreatedAt).ToList());

    public Task<int> InsertAsync(ClientNote note, CancellationToken ct = default)
    {
        note.Id = _nextId++;
        _notes.Add(note);
        return Task.FromResult(note.Id);
    }
}
