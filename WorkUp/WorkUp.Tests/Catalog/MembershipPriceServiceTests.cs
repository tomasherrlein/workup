using WorkUp.BusinessLogic.Catalog;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Catalog;

// Plan assumption 4: the monthly membership price is set per weekly frequency (1 to 7), one price per frequency.
public class MembershipPriceServiceTests
{
    private readonly FakeMembershipPriceRepository _repository = new();
    private readonly MembershipPriceService _service;

    public MembershipPriceServiceTests()
    {
        _service = new MembershipPriceService(_repository);
    }

    [Fact]
    public async Task Sets_a_new_price_for_a_frequency()
    {
        var result = await _service.SetPriceAsync(1, weeklyFrequency: 2, price: 15000m);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Value.WeeklyFrequency);
        Assert.Equal(15000m, result.Value.Price);
        Assert.Equal(1, result.Value.BusinessId);
    }

    [Fact]
    public async Task Updates_the_price_of_an_already_set_frequency_instead_of_duplicating_it()
    {
        await _service.SetPriceAsync(1, 2, 15000m);

        var result = await _service.SetPriceAsync(1, 2, 18000m);

        Assert.True(result.Succeeded);
        var list = await _service.ListAsync(1);
        var price = Assert.Single(list);
        Assert.Equal(18000m, price.Price);
    }

    [Fact]
    public async Task Allows_the_same_frequency_priced_differently_in_another_business()
    {
        await _service.SetPriceAsync(1, 2, 15000m);

        var result = await _service.SetPriceAsync(2, 2, 20000m);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public async Task Fails_when_the_weekly_frequency_is_out_of_range(int frequency)
    {
        var result = await _service.SetPriceAsync(1, frequency, 15000m);

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_membership_frequency", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_when_the_price_is_not_greater_than_zero()
    {
        var result = await _service.SetPriceAsync(1, 2, 0m);

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_membership_price", result.Error!.Code);
    }

    [Fact]
    public async Task Lists_prices_ordered_by_weekly_frequency()
    {
        await _service.SetPriceAsync(1, 3, 20000m);
        await _service.SetPriceAsync(1, 1, 10000m);

        var list = await _service.ListAsync(1);

        Assert.Equal([1, 3], list.Select(p => p.WeeklyFrequency));
    }

    [Fact]
    public async Task Removes_an_existing_price()
    {
        await _service.SetPriceAsync(1, 2, 15000m);

        var result = await _service.RemoveAsync(1, 2);

        Assert.True(result.Succeeded);
        Assert.Empty(await _service.ListAsync(1));
    }

    [Fact]
    public async Task Fails_to_remove_a_price_that_was_never_set()
    {
        var result = await _service.RemoveAsync(1, 2);

        Assert.False(result.Succeeded);
        Assert.Equal("membership_price_not_found", result.Error!.Code);
    }

    [Fact]
    public async Task Fails_to_remove_a_price_that_belongs_to_another_business()
    {
        await _service.SetPriceAsync(1, 2, 15000m);

        var result = await _service.RemoveAsync(2, 2);

        Assert.False(result.Succeeded);
        Assert.Equal("membership_price_not_found", result.Error!.Code);
    }
}
