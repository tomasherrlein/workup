using WorkUp.BusinessLogic.Scheduling;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Scheduling;

public sealed class OwnerAppointmentLinksServiceTests
{
    [Fact]
    public async Task Recent_list_returns_only_the_requested_business_projection_and_booking_identifiers()
    {
        var now = new DateTime(2026, 9, 22, 9, 0, 0);
        var expected = new OwnerPrivateAppointmentSummary(
            41, 73, now.AddHours(2), "Corte", "Ana Pérez", 1, now.AddDays(1));
        var repository = new FakeOwnerPrivateAppointmentRepository(
        [
            (7, expected),
            (8, new OwnerPrivateAppointmentSummary(42, 74, now, "Yoga", "Lucía Díaz", 1, now.AddDays(1)))
        ]);
        var service = new OwnerAppointmentLinksService(repository, new FakeClock { Now = now });

        var result = await service.ListRecentAsync(7);

        Assert.Equal([expected], result);
        Assert.Equal([7], repository.RequestedBusinessIds);
        var fields = typeof(OwnerPrivateAppointmentSummary).GetProperties().Select(property => property.Name);
        Assert.DoesNotContain(fields, name => name.Contains("Client", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, name => name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, name => name.Contains("Email", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, name => name.Contains("Note", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Recent_list_rejects_an_invalid_business_id_without_querying_storage()
    {
        var repository = new FakeOwnerPrivateAppointmentRepository([]);
        var service = new OwnerAppointmentLinksService(repository, new FakeClock());

        var result = await service.ListRecentAsync(0);

        Assert.Empty(result);
        Assert.Empty(repository.RequestedBusinessIds);
    }

    private sealed class FakeOwnerPrivateAppointmentRepository(
        IReadOnlyList<(int BusinessId, OwnerPrivateAppointmentSummary Summary)> rows)
        : IOwnerPrivateAppointmentRepository
    {
        public List<int> RequestedBusinessIds { get; } = [];

        public Task<IReadOnlyList<OwnerPrivateAppointmentSummary>> ListRecentIndividualBookingsAsync(
            int businessId, DateTime now, CancellationToken ct = default)
        {
            RequestedBusinessIds.Add(businessId);
            IReadOnlyList<OwnerPrivateAppointmentSummary> matches = rows
                .Where(row => row.BusinessId == businessId)
                .Select(row => row.Summary)
                .ToArray();
            return Task.FromResult(matches);
        }
    }
}
