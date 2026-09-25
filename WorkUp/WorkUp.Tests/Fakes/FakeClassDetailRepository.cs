using WorkUp.BusinessLogic.Classes;

namespace WorkUp.Tests.Fakes;

/// <summary>One session of the fake, owned by a business so a foreign tenant can be simulated.</summary>
public sealed record FakeClassDetailSession(int BusinessId, ClassDetailSessionRow Row);

/// <summary>
/// In-memory stand-in for the class detail read port. The tenant scope is emulated exactly like the SQL predicate:
/// a session registered under another business is invisible to the requested one. It deliberately does not filter the
/// kind, so a service that forgot the <c>ClassSession</c> discriminant is caught by a test instead of being hidden by
/// the fake — the SQL statement does carry it, and the opt-in fixture proves it against a real database.
/// </summary>
public sealed class FakeClassDetailRepository : IClassDetailRepository
{
    public List<FakeClassDetailSession> Sessions { get; } = [];

    public int GetCallCount { get; private set; }

    public int? GetBusinessId { get; private set; }

    public int? GetAppointmentId { get; private set; }

    public Task<ClassDetailSessionRow?> GetSessionAsync(
        int businessId, int appointmentId, CancellationToken ct = default)
    {
        GetCallCount++;
        GetBusinessId = businessId;
        GetAppointmentId = appointmentId;

        var session = Sessions.FirstOrDefault(candidate =>
            candidate.BusinessId == businessId && candidate.Row.AppointmentId == appointmentId);

        return Task.FromResult(session?.Row);
    }
}
