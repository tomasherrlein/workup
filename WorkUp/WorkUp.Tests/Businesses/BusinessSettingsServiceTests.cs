using WorkUp.Domain.Entities;
using WorkUp.BusinessLogic.Businesses;
using WorkUp.Tests.Fakes;
// See IBusinessRepository.cs for why the entity is aliased instead of imported by its bare name.

namespace WorkUp.Tests.Businesses;

// RF-01: the owner edits her own business's operating parameters.
public class BusinessSettingsServiceTests
{
    private readonly FakeBusinessRepository _businesses = new();
    private readonly BusinessSettingsService _service;
    private int _businessId;

    public BusinessSettingsServiceTests()
    {
        _service = new BusinessSettingsService(_businesses);
    }

    private async Task<int> SeedBusinessAsync(string slug = "peluqueria-de-marina")
    {
        var business = new Business { Name = "Peluquería de Marina", Slug = slug, ContactPhone = "111", CreatedAt = DateTime.UtcNow };
        await _businesses.InsertAsync(business);
        _businessId = business.Id;
        return business.Id;
    }

    private static UpdateBusinessSettingsRequest ValidRequest(string slug) => new(
        Name: "Peluquería de Marina",
        Slug: slug,
        ContactPhone: "1122334455",
        NotificationEmail: "avisos@marina.com",
        AllowsWalkIns: false,
        ReminderLeadHours: 48,
        DailySummaryTime: new TimeOnly(20, 30),
        StockReviewEveryDays: 14);

    [Fact]
    public async Task Updates_every_configured_field()
    {
        await SeedBusinessAsync();

        var result = await _service.UpdateAsync(_businessId, ValidRequest("peluqueria-de-marina"));

        Assert.True(result.Succeeded);
        var business = _businesses.All.Single(b => b.Id == _businessId);
        Assert.False(business.AllowsWalkIns);
        Assert.Equal(48, business.ReminderLeadHours);
        Assert.Equal(new TimeOnly(20, 30), business.DailySummaryTime);
        Assert.Equal(14, business.StockReviewEveryDays);
        Assert.Equal("avisos@marina.com", business.NotificationEmail);
    }

    [Fact]
    public async Task Keeping_the_businesss_own_slug_is_not_a_conflict()
    {
        await SeedBusinessAsync("peluqueria-de-marina");

        var result = await _service.UpdateAsync(_businessId, ValidRequest("peluqueria-de-marina"));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Fails_when_the_new_slug_belongs_to_another_business()
    {
        await SeedBusinessAsync("peluqueria-de-marina");
        var other = new Business { Name = "Estudio de Lucía", Slug = "estudio-lucia", ContactPhone = "222", CreatedAt = DateTime.UtcNow };
        await _businesses.InsertAsync(other);

        var result = await _service.UpdateAsync(_businessId, ValidRequest("estudio-lucia"));

        Assert.False(result.Succeeded);
        Assert.Equal("slug_taken", result.Error!.Code);
    }

    // The edit can pass the sequential slug precheck and still lose the update to a competing edit. It is reported as
    // the same slug_taken field error the precheck produces, never as an unhandled exception, and storage is not
    // changed; the SQL fixture proves the row keeps its previous slug.
    [Fact]
    public async Task Fails_when_the_new_slug_is_lost_to_a_concurrent_edit()
    {
        await SeedBusinessAsync("peluqueria-de-marina");
        var other = new Business { Name = "Estudio de Lucía", Slug = "estudio-lucia", ContactPhone = "222", CreatedAt = DateTime.UtcNow };
        await _businesses.InsertAsync(other);
        _businesses.LoseSlugRaceOnUpdate = true;

        var result = await _service.UpdateAsync(_businessId, ValidRequest("estudio-lucia"));

        Assert.False(result.Succeeded);
        Assert.Equal("slug_taken", result.Error!.Code);
        Assert.Equal(nameof(UpdateBusinessSettingsRequest.Slug), result.Error.Field);
    }

    // Only the known unique-slug collision is a business outcome. Any other storage failure must keep propagating
    // instead of being disguised as slug_taken.
    [Fact]
    public async Task Propagates_an_unexpected_storage_failure_instead_of_reporting_a_slug_conflict()
    {
        await SeedBusinessAsync();
        _businesses.UpdateFailure = new InvalidOperationException("connection reset");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.UpdateAsync(_businessId, ValidRequest("peluqueria-de-marina")));

        Assert.Equal("connection reset", exception.Message);
    }

    [Fact]
    public async Task Fails_when_the_new_slug_has_an_invalid_format()
    {
        await SeedBusinessAsync();

        var result = await _service.UpdateAsync(_businessId, ValidRequest("Con Espacios"));

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_slug", result.Error!.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(169)]
    public async Task Fails_when_reminder_lead_hours_is_out_of_the_1_to_168_range(int hours)
    {
        await SeedBusinessAsync();

        var result = await _service.UpdateAsync(_businessId, ValidRequest("peluqueria-de-marina") with { ReminderLeadHours = hours });

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_reminder_lead_hours", result.Error!.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(91)]
    public async Task Fails_when_stock_review_every_days_is_out_of_the_1_to_90_range(int days)
    {
        await SeedBusinessAsync();

        var result = await _service.UpdateAsync(_businessId, ValidRequest("peluqueria-de-marina") with { StockReviewEveryDays = days });

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_stock_review_every_days", result.Error!.Code);
    }
}
