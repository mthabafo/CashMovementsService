using CashMovements.Domain.Exceptions;
using CashMovements.Domain.ValueObjects;

namespace CashMovements.UnitTests.Domain;

public class ValueObjectTests
{
    [Theory]
    [InlineData("zar", "ZAR")]
    [InlineData(" usd ", "USD")]
    [InlineData("EUR", "EUR")]
    public void Currency_is_trimmed_and_upper_cased(string input, string expected)
        => Assert.Equal(expected, Currency.From(input).Code);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ZA")]
    [InlineData("ZARR")]
    [InlineData("Z4R")]
    [InlineData("ZÄR")]
    public void Currency_rejects_non_iso_codes(string? input)
        => Assert.Throws<InvalidValueException>(() => Currency.From(input));

    [Fact]
    public void Currency_compares_by_value()
        => Assert.Equal(Currency.From("usd"), Currency.From(" USD"));

    [Fact]
    public void AccountId_is_trimmed()
        => Assert.Equal("ACC1", AccountId.From("  ACC1 ").Value);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AccountId_is_required(string? input)
        => Assert.Throws<InvalidValueException>(() => AccountId.From(input));

    [Fact]
    public void AccountId_has_a_maximum_length()
    {
        AccountId.From(new string('A', AccountId.MaxLength));
        Assert.Throws<InvalidValueException>(() => AccountId.From(new string('A', AccountId.MaxLength + 1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExternalRef_is_required(string? input)
        => Assert.Throws<InvalidValueException>(() => ExternalRef.From(input));

    [Fact]
    public void ExternalRef_has_a_maximum_length()
    {
        ExternalRef.From(new string('R', ExternalRef.MaxLength));
        Assert.Throws<InvalidValueException>(() => ExternalRef.From(new string('R', ExternalRef.MaxLength + 1)));
    }

    [Fact]
    public void ExternalRef_compares_by_value()
        => Assert.Equal(ExternalRef.From("REF-1"), ExternalRef.From(" REF-1 "));
}
