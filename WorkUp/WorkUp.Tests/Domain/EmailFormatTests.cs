using WorkUp.Domain.Rules;

namespace WorkUp.Tests.Domain;

// RF 1: the email identifies the client (Regla 7). The format rule only checks the basic shape; normalization
// (Text.NormalizeEmail) and uniqueness are handled elsewhere (see ClientsServiceTests).
public class EmailFormatTests
{
    [Fact]
    public void Accepts_a_plain_email()
    {
        Assert.True(EmailFormat.IsValid("marina@peluqueria.com"));
    }

    [Fact]
    public void Accepts_an_email_with_a_subdomain_and_a_plus_tag()
    {
        Assert.True(EmailFormat.IsValid("marina+turnos@correo.peluqueria.com.ar"));
    }

    [Fact]
    public void Rejects_a_value_without_an_at_sign()
    {
        Assert.False(EmailFormat.IsValid("marina.peluqueria.com"));
    }

    [Fact]
    public void Rejects_a_value_without_a_domain_dot()
    {
        Assert.False(EmailFormat.IsValid("marina@peluqueria"));
    }

    [Fact]
    public void Rejects_a_value_with_an_empty_local_part()
    {
        Assert.False(EmailFormat.IsValid("@peluqueria.com"));
    }

    [Fact]
    public void Rejects_a_value_with_an_empty_domain()
    {
        Assert.False(EmailFormat.IsValid("marina@.com"));
    }

    [Fact]
    public void Rejects_a_value_with_a_space()
    {
        Assert.False(EmailFormat.IsValid("marina @peluqueria.com"));
    }

    [Fact]
    public void Rejects_an_empty_value()
    {
        Assert.False(EmailFormat.IsValid(""));
    }

    [Fact]
    public void Rejects_null()
    {
        Assert.False(EmailFormat.IsValid(null));
    }
}
