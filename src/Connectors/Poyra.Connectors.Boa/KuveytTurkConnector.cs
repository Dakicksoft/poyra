using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.Boa;

/// <summary>
/// <b>Kuveyt Türk</b> sanal POS — BOA ailesi, 3D barındırılan akış + provizyon teyidi.
/// Ortak davranış <see cref="BoaConnectorBase"/> içindedir; burada yalnız bankaya özgü
/// kimlik ve XML adları durur.
/// </summary>
public sealed class KuveytTurkConnector(IHttpClientFactory httpClientFactory)
    : BoaConnectorBase(httpClientFactory)
{
    public const string ConnectorKey = "kuveytturk";

    public override string Key => ConnectorKey;

    protected override string XmlKokEleman => "KuveytTurkVPosMessage";
    protected override string XmlEkVeriEleman => "KuveytTurkVPosAdditionalData";

    public override ConnectorDescriptor Descriptor { get; } = new(
        ConnectorKey,
        "Kuveyt Türk Sanal POS (3D) — SERTİFİKASYON BEKLİYOR",
        ConnectorType.BankVirtualPos,
        OrtakKimlikAlanlari,
        SupportsInstallments: true,
        SupportsVoid: true,
        SupportsRefund: true,
        Notes: "TODO(cert): hash yapısı iki aşamalıdır ve banka dokümanıyla doğrulanmalı. "
               + "Tutar KURUŞ gider. Tahsilat tarayıcı dönüşüyle değil, ThreeDModelProvisionGate "
               + "sunucu teyidiyle kesinleşir. İptal SaleReversal, iade PartialDrawBack. "
               + "TODO(cert): iptal/iade uç adları Vakıf Katılım'ın BOA dokümanından "
               + "alındı (aynı platform); Kuveyt Türk'ün kendi dokümanıyla teyit edilmeli.");
}
