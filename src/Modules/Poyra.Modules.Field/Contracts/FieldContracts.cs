namespace Poyra.Modules.Field.Contracts;

/// <summary>Saha tahsilatı için üretilen ödeme bağlantısı.</summary>
/// <param name="PublicId">lnk_…</param>
/// <param name="Url">Müşteriye gönderilecek / karekoda gömülecek adres.</param>
public sealed record FieldCheckoutLink(string PublicId, string Url);


public sealed record FieldLinkOutcome(bool Paid, string? PaymentPublicId, bool Expired);

public interface IFieldCheckoutLinks
{
    Task<FieldCheckoutLink> CreateAsync(
        long amountMinor,
        string currency,
        string description,
        string? customerRef,
        DateTimeOffset? expiresAt,
        CancellationToken ct);

    Task<FieldLinkOutcome> GetOutcomeAsync(string linkPublicId, CancellationToken ct);
}
