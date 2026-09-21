using WorkUp.BusinessLogic.Businesses;
using WorkUp.Tests.Fakes;

namespace WorkUp.Tests.Businesses;

// RF-01, RNF Seguridad: registering a business creates it and its single owner account in one transaction.
public class RegisterBusinessServiceTests
{
    private readonly FakeBusinessRepository _businesses = new();
    private readonly FakeOwnerAccountRepository _ownerAccounts = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakePasswordHasher _passwordHasher = new();
    private readonly FakeClock _clock = new();
    private readonly RegisterBusinessService _service;

    public RegisterBusinessServiceTests()
    {
        _service = new RegisterBusinessService(_businesses, _ownerAccounts, _unitOfWork, _passwordHasher, _clock);
    }

    private static RegisterBusinessRequest ValidRequest() => new(
        Name: "Peluquería de Marina",
        Slug: "peluqueria-de-marina",
        ContactPhone: "1122334455",
        NotificationEmail: "avisos@marina.com",
        Username: "marina",
        Password: "correcta123",
        ConfirmPassword: "correcta123");

    [Fact]
    public async Task Creates_the_business_and_its_owner_account_in_one_transaction()
    {
        var result = await _service.RegisterAsync(ValidRequest());

        Assert.True(result.Succeeded);
        Assert.Equal(1, _unitOfWork.CommitCount);
        Assert.Single(_businesses.All);
        Assert.Single(_ownerAccounts.All);

        var business = _businesses.All[0];
        Assert.Equal("Peluquería de Marina", business.Name);
        Assert.Equal("peluqueria-de-marina", business.Slug);
        Assert.Equal("1122334455", business.ContactPhone);
        Assert.Equal("avisos@marina.com", business.NotificationEmail);

        var owner = _ownerAccounts.All[0];
        Assert.Equal(business.Id, owner.BusinessId);
        Assert.Equal("marina", owner.Username);
    }

    [Fact]
    public async Task Applies_the_schema_defaults_to_the_new_business()
    {
        await _service.RegisterAsync(ValidRequest());

        var business = _businesses.All[0];
        Assert.True(business.AllowsWalkIns);
        Assert.Equal(24, business.ReminderLeadHours);
        Assert.Equal(new TimeOnly(21, 0), business.DailySummaryTime);
        Assert.Equal(7, business.StockReviewEveryDays);
    }

    [Fact]
    public async Task Hashes_the_password_instead_of_storing_it_in_clear()
    {
        await _service.RegisterAsync(ValidRequest());

        var owner = _ownerAccounts.All[0];
        Assert.NotEqual("correcta123", owner.PasswordHash);
        Assert.True(_passwordHasher.Verify(owner.PasswordHash, "correcta123"));
    }

    [Fact]
    public async Task Fails_when_the_slug_does_not_match_the_required_format()
    {
        var result = await _service.RegisterAsync(ValidRequest() with { Slug = "Peluqueria Marina" });

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_slug", result.Error!.Code);
        Assert.Empty(_businesses.All);
    }

    [Fact]
    public async Task Fails_when_the_slug_is_already_taken()
    {
        await _service.RegisterAsync(ValidRequest());

        var result = await _service.RegisterAsync(ValidRequest() with { Username = "otra" });

        Assert.False(result.Succeeded);
        Assert.Equal("slug_taken", result.Error!.Code);
        Assert.Single(_businesses.All);
    }

    [Fact]
    public async Task Fails_when_the_username_is_already_taken()
    {
        await _service.RegisterAsync(ValidRequest());

        var result = await _service.RegisterAsync(ValidRequest() with { Slug = "otro-negocio" });

        Assert.False(result.Succeeded);
        Assert.Equal("username_taken", result.Error!.Code);
        Assert.Single(_ownerAccounts.All);
    }

    [Fact]
    public async Task Fails_when_the_password_is_shorter_than_8_characters()
    {
        var result = await _service.RegisterAsync(ValidRequest() with { Password = "corta1", ConfirmPassword = "corta1" });

        Assert.False(result.Succeeded);
        Assert.Equal("password_too_short", result.Error!.Code);
        Assert.Empty(_businesses.All);
    }

    [Fact]
    public async Task Fails_when_the_password_confirmation_does_not_match()
    {
        var result = await _service.RegisterAsync(ValidRequest() with { ConfirmPassword = "otradistinta1" });

        Assert.False(result.Succeeded);
        Assert.Equal("password_mismatch", result.Error!.Code);
        Assert.Empty(_businesses.All);
    }

    [Fact]
    public async Task Nothing_is_written_when_registration_fails()
    {
        await _service.RegisterAsync(ValidRequest() with { Slug = "MAYUSCULAS" });

        Assert.Empty(_businesses.All);
        Assert.Empty(_ownerAccounts.All);
        Assert.Equal(0, _unitOfWork.CommitCount);
    }
}
