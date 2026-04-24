using FluentAssertions;
using MDH.Shared.Domain;
using Xunit;

namespace MDH.Shared.Tests;

public class MoneyTests
{
    [Fact]
    public void Add_SameCurrency_ReturnsSum()
    {
        var a = new Money(1000m);
        var b = new Money(250m);

        var result = a + b;

        result.Amount.Should().Be(1250m);
        result.Currency.Should().Be("USD");
    }

    [Fact]
    public void Subtract_SameCurrency_ReturnsDifference()
    {
        var a = new Money(1500m);
        var b = new Money(200m);

        var result = a - b;

        result.Amount.Should().Be(1300m);
    }

    [Fact]
    public void Multiply_ByFactor_ReturnsScaledAmount()
    {
        var money = new Money(1000m);

        var result = money * 1.05m;

        result.Amount.Should().Be(1050m);
    }

    [Fact]
    public void Add_DifferentCurrencies_ThrowsInvalidOperationException()
    {
        var usd = new Money(100m, "USD");
        var eur = new Money(100m, "EUR");

        var act = () => usd + eur;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void GreaterThan_ComparesTwoAmounts()
    {
        var higher = new Money(2000m);
        var lower = new Money(1500m);

        (higher > lower).Should().BeTrue();
        (lower > higher).Should().BeFalse();
    }

    [Fact]
    public void ToString_FormatsCorrectly()
    {
        var money = new Money(1234.56m, "USD");
        money.ToString().Should().Be("USD 1234.56");
    }
}
