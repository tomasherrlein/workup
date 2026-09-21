using WorkUp.Domain.Rules;

namespace WorkUp.Tests.Domain;

// RF-01: the public slug is the URL segment of the booking link /r/{slug}.
public class SlugFormatTests
{
    [Theory]
    [InlineData("peluqueria-de-marina")]
    [InlineData("yoga")]
    [InlineData("estudio-123")]
    [InlineData("a1-b2-c3")]
    public void Accepts_lowercase_letters_digits_and_single_hyphens(string slug)
    {
        Assert.True(SlugFormat.IsValid(slug));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Peluqueria")]
    [InlineData("peluqueria de marina")]
    [InlineData("peluquería")]
    [InlineData("-peluqueria")]
    [InlineData("peluqueria-")]
    [InlineData("peluqueria--marina")]
    [InlineData("peluqueria_marina")]
    public void Rejects_anything_outside_lowercase_letters_digits_and_single_hyphens(string slug)
    {
        Assert.False(SlugFormat.IsValid(slug));
    }

    [Fact]
    public void Rejects_a_slug_longer_than_the_column_size()
    {
        var tooLong = new string('a', SlugFormat.MaxLength + 1);

        Assert.False(SlugFormat.IsValid(tooLong));
    }

    [Fact]
    public void Accepts_a_slug_at_exactly_the_column_size()
    {
        var atLimit = new string('a', SlugFormat.MaxLength);

        Assert.True(SlugFormat.IsValid(atLimit));
    }
}
