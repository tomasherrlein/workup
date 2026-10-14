using WorkUp.Domain.Rules;

namespace WorkUp.Tests.Domain;

// RF 1: the phone is extra contact data (Regla 7), but the owner still enters a plausible number. The format rule
// only checks the basic shape; it is not a real number validator.
public class PhoneFormatTests
{
    [Fact]
    public void Accepts_a_plain_local_number()
    {
        Assert.True(PhoneFormat.IsValid("1122334455"));
    }

    [Fact]
    public void Accepts_a_number_with_country_code_spaces_and_a_dash()
    {
        Assert.True(PhoneFormat.IsValid("+54 9 11 2233-4455"));
    }

    [Fact]
    public void Accepts_a_number_with_parentheses_around_the_area_code()
    {
        Assert.True(PhoneFormat.IsValid("(011) 2233-4455"));
    }

    [Fact]
    public void Rejects_letters()
    {
        Assert.False(PhoneFormat.IsValid("11-ABCD-4455"));
    }

    [Fact]
    public void Rejects_fewer_than_the_minimum_digits()
    {
        Assert.False(PhoneFormat.IsValid("12345"));
    }

    [Fact]
    public void Rejects_more_than_the_maximum_digits()
    {
        Assert.False(PhoneFormat.IsValid("123456789012345678901"));
    }

    [Fact]
    public void Rejects_an_empty_value()
    {
        Assert.False(PhoneFormat.IsValid(""));
    }

    [Fact]
    public void Rejects_only_whitespace()
    {
        Assert.False(PhoneFormat.IsValid("   "));
    }

    [Fact]
    public void Rejects_null()
    {
        Assert.False(PhoneFormat.IsValid(null));
    }
}
