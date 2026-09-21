using WorkUp.BusinessLogic.Businesses;
using WorkUp.Domain.Entities;
using WorkUp.Tests.Fakes;
// See IBusinessRepository.cs for why the entity is aliased instead of imported by its bare name.

namespace WorkUp.Tests.Businesses;

// RNF Seguridad: a wrong username and a wrong password show the exact same message (non-enumerating).
public class OwnerLoginServiceTests
{
    private readonly FakeBusinessRepository _businesses = new();
    private readonly FakeOwnerAccountRepository _ownerAccounts = new();
    private readonly FakePasswordHasher _passwordHasher = new();
    private readonly OwnerLoginService _service;

    public OwnerLoginServiceTests()
    {
        _service = new OwnerLoginService(_ownerAccounts, _businesses, _passwordHasher);
    }

    private async Task SeedOwnerAsync()
    {
        var business = new Business { Name = "Estudio de Lucía", Slug = "estudio-lucia", ContactPhone = "111", CreatedAt = DateTime.UtcNow };
        await _businesses.InsertAsync(business);
        await _ownerAccounts.InsertAsync(new OwnerAccount
        {
            BusinessId = business.Id,
            Username = "lucia",
            PasswordHash = _passwordHasher.Hash("correcta123"),
            CreatedAt = DateTime.UtcNow
        });
    }

    [Fact]
    public async Task Succeeds_with_the_right_username_and_password()
    {
        await SeedOwnerAsync();

        var result = await _service.LoginAsync("lucia", "correcta123");

        Assert.True(result.Succeeded);
        Assert.Equal("lucia", result.Value.Username);
        Assert.Equal("estudio-lucia", result.Value.BusinessSlug);
        Assert.Equal("Estudio de Lucía", result.Value.BusinessName);
    }

    [Fact]
    public async Task Fails_with_the_generic_message_when_the_password_is_wrong()
    {
        await SeedOwnerAsync();

        var result = await _service.LoginAsync("lucia", "incorrecta");

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_credentials", result.Error!.Code);
        Assert.Equal("Usuario o contraseña incorrectos.", result.Error.Message);
    }

    [Fact]
    public async Task Fails_with_the_same_generic_message_when_the_username_does_not_exist()
    {
        await SeedOwnerAsync();

        var result = await _service.LoginAsync("no-existe", "cualquiera");

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_credentials", result.Error!.Code);
        Assert.Equal("Usuario o contraseña incorrectos.", result.Error.Message);
    }
}
