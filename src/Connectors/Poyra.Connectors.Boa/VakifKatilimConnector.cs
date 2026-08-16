using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Boa;

/// <summary>
/// <b>Vakıf Katılım</b> sanal POS — Kuveyt Türk ile aynı BOA altyapısı. Fark yalnız
/// XML kök elemanı ve gateway yolunda (<c>VirtualPOS.Gateway</c> öneki); akış, hash
/// yapısı ve provizyon teyidi ortaktır.
/// </summary>
public sealed class VakifKatilimConnector(IHttpClientFactory httpClientFactory)
    : BoaConnectorBase(httpClientFactory)
{
    public const string ConnectorKey = "vakifkatilim";

    public override string Key => ConnectorKey;

    protected override string XmlKokEleman => "VPosMessageContract";
    protected override string XmlEkVeriEleman => "VPosAdditionalData";
    protected override string GatewayOnEk => "VirtualPOS.Gateway";

    public override ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "Vakıf Katılım Sanal POS (3D) — SERTİFİKASYON BEKLİYOR",
        ConnectorType.BankVirtualPos,
        OrtakKimlikAlanlari,
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "Kuveyt Türk ile aynı BOA ailesi. Tutar KURUŞ gider. Tahsilat "
               + "ThreeDModelProvisionGate sunucu teyidiyle kesinleşir. "
               + "İptal SaleReversal, iade PartialDrawBack — bankanın kendi entegrasyon "
               + "dokümanından (v2.7). TODO(cert): hash sırası sertifikasyonda teyit edilmeli.");
}
