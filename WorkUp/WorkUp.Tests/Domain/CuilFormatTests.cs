using WorkUp.Domain.Rules;

namespace WorkUp.Tests.Domain;

// RF-03: the CUIL identifies a worker uniquely per business. The format rule only normalizes and checks the digit
// count; uniqueness is checked by the repository (see StaffServiceTests).
public class CuilFormatTests
{
    [Fact]
    public void Normalizes_11_plain_digits_into_the_standard_format()
    {
        var ok = CuilFormat.TryNormalize("20304050607", out var normalized);

        Assert.True(ok);
        Assert.Equal("20-30405060-7", normalized);
    }

    [Fact]
    public void Accepts_a_cuil_already_formatted_with_dashes()
    {
        var ok = CuilFormat.TryNormalize("20-30405060-7", out var normalized);

        Assert.True(ok);
        Assert.Equal("20-30405060-7", normalized);
    }

    [Fact]
    public void Accepts_a_cuil_with_dashes_in_nonstandard_positions()
    {
        var ok = CuilFormat.TryNormalize("2-030405060-7", out var normalized);

        Assert.True(ok);
        Assert.Equal("20-30405060-7", normalized);
    }

    [Fact]
    public void Accepts_a_cuil_with_spaces()
    {
        var ok = CuilFormat.TryNormalize("20 30405060 7", out var normalized);

        Assert.True(ok);
        Assert.Equal("20-30405060-7", normalized);
    }

    [Fact]
    public void Rejects_fewer_than_11_digits()
    {
        var ok = CuilFormat.TryNormalize("2030405060", out var normalized);

        Assert.False(ok);
        Assert.Equal("", normalized);
    }

    [Fact]
    public void Rejects_more_than_11_digits()
    {
        var ok = CuilFormat.TryNormalize("203040506078", out var normalized);

        Assert.False(ok);
    }

    [Fact]
    public void Rejects_an_empty_value()
    {
        var ok = CuilFormat.TryNormalize("", out _);

        Assert.False(ok);
    }

    [Fact]
    public void Rejects_null()
    {
        var ok = CuilFormat.TryNormalize(null, out _);

        Assert.False(ok);
    }
}
