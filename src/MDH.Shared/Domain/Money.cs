namespace MDH.Shared.Domain;

public readonly record struct Money(decimal Amount, string Currency = "USD")
{
    public Money Add(Money other)
    {
        if (Currency != other.Currency)
            throw new InvalidOperationException($"Cannot add {Currency} and {other.Currency}");
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        if (Currency != other.Currency)
            throw new InvalidOperationException($"Cannot subtract {Currency} and {other.Currency}");
        return new Money(Amount - other.Amount, Currency);
    }

    public Money Multiply(decimal factor) => new(Amount * factor, Currency);

    public static Money operator +(Money a, Money b) => a.Add(b);
    public static Money operator -(Money a, Money b) => a.Subtract(b);
    public static Money operator *(Money a, decimal factor) => a.Multiply(factor);
    public static bool operator >(Money a, Money b) => a.Amount > b.Amount;
    public static bool operator <(Money a, Money b) => a.Amount < b.Amount;

    public override string ToString() => $"{Currency} {Amount:F2}";
}
