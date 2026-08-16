namespace Poyra.SharedKernel.Domain;

/// <summary>
/// Para her zaman en küçük birim (kuruş) cinsinden long taşınır; API sınırından içeri
/// asla decimal aritmetiği girmez. Yüzdesel hesaplar ayrı hesap servislerinde yapılır,
/// sonuç yine minor unit'e döner.
/// </summary>
public readonly record struct Money(long AmountMinor, string Currency)
{
    public static Money Of(long amountMinor, string currency)
    {
        if (amountMinor <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountMinor), "Tutar 0'dan büyük olmalı.");
        if (currency is not { Length: 3 })
            throw new ArgumentException("Para birimi 3 harfli ISO 4217 kodu olmalı.", nameof(currency));

        return new Money(amountMinor, currency.ToUpperInvariant());
    }

    public override string ToString() => $"{AmountMinor} {Currency}";
}
