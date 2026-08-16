using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.PaymentLinks.Domain;

public sealed class KarekodSettings : ITenantOwned, IAuditable
{
    public Guid TenantId { get; init; }

    /// <summary>Şemanın alan tanımlayıcısı — bankanın verdiği değer (ör. "TR.GOV.TCMB").</summary>
    public string? SchemeGuid { get; set; }

    /// <summary>Karekod şemasındaki üye işyeri numarası — banka verir.</summary>
    public string? MerchantNo { get; set; }

    /// <summary>ISO 18245 işyeri kategori kodu (MCC) — banka sözleşmesinde yazar.</summary>
    public string? CategoryCode { get; set; }

    /// <summary>Karekod'da görünen ad/şehir. Boşsa marka adına, o da yoksa işyeri adına düşer.</summary>
    public string? MerchantName { get; set; }
    public string? MerchantCity { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Karekod üretilebilir mi? Banka tanımlayıcıları olmadan üretilen yük GEÇERSİZDİR;
    /// yarım ayarla "çalışıyormuş gibi" bir QR basmak en kötü sonuçtur.
    /// </summary>
    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(SchemeGuid) && !string.IsNullOrWhiteSpace(MerchantNo);

    public static KarekodSettings Default(Guid tenantId) => new() { TenantId = tenantId };
}

/// <summary>TR Karekod yükünü EMVCo Merchant-Presented QR yapısında kurar.</summary>
public static class KarekodBuilder
{
    /// <summary>Üye işyeri hesabı şablonu — EMVCo 26-51 aralığındaki ilk serbest etiket.</summary>
    public const string MerchantAccountTag = "26";

    private const string SubTagGuid = "00";
    private const string SubTagMerchantNo = "01";

    public static string Build(
        KarekodSettings settings,
        string merchantName,
        string merchantCity,
        long? amountMinor,
        string reference)
    {
        if (!settings.IsConfigured)
            throw new InvalidOperationException(
                "Karekod ayarları eksik: şema tanımlayıcısı ve üye işyeri numarası bankanızdan alınır.");

        var payload =
            EmvQr.Field(EmvQr.TagPayloadFormat, "01")
            + EmvQr.Field(EmvQr.TagInitiationMethod,
                amountMinor is null ? EmvQr.InitiationStatic : EmvQr.InitiationDynamic)
            + EmvQr.Template(MerchantAccountTag,
                (SubTagGuid, settings.SchemeGuid),
                (SubTagMerchantNo, settings.MerchantNo))
            + Optional(EmvQr.TagMerchantCategory, settings.CategoryCode)
            + EmvQr.Field(EmvQr.TagCurrency, "949") // TRY, ISO 4217 sayısal
            + (amountMinor is { } amount ? EmvQr.Field(EmvQr.TagAmount, EmvQr.Amount(amount)) : "")
            + EmvQr.Field(EmvQr.TagCountry, "TR")
            + EmvQr.Field(EmvQr.TagMerchantName, EmvQr.Ascii(merchantName, 25))
            + EmvQr.Field(EmvQr.TagMerchantCity, EmvQr.Ascii(merchantCity, 15))
            // 62/05 = referans etiketi: dekontta görünür, mutabakat bununla eşleşir
            + EmvQr.Template(EmvQr.TagAdditionalData, ("05", EmvQr.Ascii(reference, 25)));

        return EmvQr.Seal(payload);
    }

    private static string Optional(string tag, string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : EmvQr.Field(tag, value);
}
