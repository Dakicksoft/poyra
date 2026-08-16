using Poyra.Connectors.Abstractions;
using Poyra.Connectors.PayFlex;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

public sealed class PayFlexMessagesTests
{
    private static ConnectorCredentials Credentials() => new(new Dictionary<string, string>
    {
        ["gateway_base"] = "https://onlineodeme.ornek",
        ["merchant_id"] = "000100000013",
        ["password"] = "gizli",
        ["terminal_no"] = "VP000123",
    });

    [Fact]
    public void Register_xml_zorunlu_alanlari_ve_tutar_bicimini_tasimali()
    {
        var xml = PayFlexMessages.BuildRegisterPaymentXml(Credentials(), new HostedPaymentRequest(
            "att_abc", 149_900, "TRY", 3, "http://localhost/cb", "Deneme", null));

        xml.ShouldContain("<MerchantId>000100000013</MerchantId>");
        xml.ShouldContain("<TerminalNo>VP000123</TerminalNo>");
        xml.ShouldContain("<CurrencyAmount>1499.00</CurrencyAmount>"); // nokta, kuruş DEĞİL
        xml.ShouldContain("<CurrencyCode>949</CurrencyCode>");
        xml.ShouldContain("<NumberOfInstallments>3</NumberOfInstallments>");
        xml.ShouldContain("<OrderId>att_abc</OrderId>");
        xml.ShouldContain("<PaymentType>CommonPayment</PaymentType>");
        xml.ShouldContain("<SuccessUrl>http://localhost/cb</SuccessUrl>");
    }

    [Fact]
    public void Tek_cekimde_taksit_alani_bos_olmali()
    {
        var xml = PayFlexMessages.BuildRegisterPaymentXml(Credentials(), new HostedPaymentRequest(
            "att_abc", 10_000, "TRY", 1, "http://localhost/cb", null, null));

        xml.ShouldContain("<NumberOfInstallments></NumberOfInstallments>");
    }

    [Fact]
    public void Register_yaniti_token_ve_sayfa_adresiyle_cozulmeli()
    {
        var result = PayFlexMessages.ParseRegisterResponse("""
            <VposResponse>
              <ResultCode>0000</ResultCode>
              <ResultDetail>İşlem başarılı</ResultDetail>
              <PaymentToken>tok_123</PaymentToken>
              <CommonPaymentUrl>https://cpp.ornek/Sayfa</CommonPaymentUrl>
            </VposResponse>
            """);

        result.ResultCode.ShouldBe("0000");
        result.PaymentToken.ShouldBe("tok_123");
        result.CommonPaymentUrl.ShouldBe("https://cpp.ornek/Sayfa");
    }

    [Fact]
    public void Sonuc_yaniti_basari_ve_red_yollarinda_cozulmeli()
    {
        var ok = PayFlexMessages.ParsePaymentResult("""
            <VposResponse>
              <ResultCode>0000</ResultCode>
              <OrderId>att_abc</OrderId>
              <TransactionId>trx-42</TransactionId>
              <AuthCode>123456</AuthCode>
            </VposResponse>
            """);
        ok.ResultCode.ShouldBe("0000");
        ok.OrderId.ShouldBe("att_abc");
        ok.TransactionId.ShouldBe("trx-42");

        var declined = PayFlexMessages.ParsePaymentResult("""
            <VposResponse>
              <ResultCode>0051</ResultCode>
              <ResultDetail>Limit yetersiz</ResultDetail>
              <OrderId>att_abc</OrderId>
            </VposResponse>
            """);
        declined.ResultCode.ShouldBe("0051");
        PayFlexMessages.ToUnified(declined.ResultCode).ShouldBe(UnifiedErrors.InsufficientFunds);
    }

    [Fact]
    public void Operasyon_xml_referans_islem_tasimali_iade_tutarli_olmali()
    {
        var cancel = PayFlexMessages.BuildOperationXml(Credentials(), "Cancel", "trx-42", null, "TRY");
        cancel.ShouldContain("<TransactionType>Cancel</TransactionType>");
        cancel.ShouldContain("<ReferenceTransactionId>trx-42</ReferenceTransactionId>");
        cancel.ShouldNotContain("CurrencyAmount"); // iptal tutar taşımaz

        var refund = PayFlexMessages.BuildOperationXml(Credentials(), "Refund", "trx-42", 5_000, "TRY");
        refund.ShouldContain("<TransactionType>Refund</TransactionType>");
        refund.ShouldContain("<CurrencyAmount>50.00</CurrencyAmount>");
    }
}
