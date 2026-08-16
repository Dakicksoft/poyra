using Poyra.Modules.Subscriptions.Domain;
using Poyra.Modules.Payments.Domain;
using Poyra.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Kapsam ölçümünde açıkta kalan iki küçük ama sonucu ağır kural: para nesnesinin
/// doğrulaması (dal kapsamı %62,5'ti — hata yolları denenmemişti) ve aboneliğin
/// faturalanabilirliği (%0).
/// </summary>
public sealed class MoneyTests
{
    [Fact]
    public void Gecerli_tutar_ve_para_birimi_kabul_edilmeli()
    {
        var para = Money.Of(149_00, "try");

        para.AmountMinor.ShouldBe(149_00);
        para.Currency.ShouldBe("TRY"); // ISO kodu büyük harfe normalize edilir
        para.ToString().ShouldBe("14900 TRY");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Sifir_ve_negatif_tutar_reddedilmeli(long tutar)
    {
        // Sıfır tutarlı "ödeme" bankaya gidip anlamsız bir işlem doğururdu;
        // negatif tutar ise iadeyi tahsilat gibi göstermenin yoludur.
        Should.Throw<ArgumentOutOfRangeException>(() => Money.Of(tutar, "TRY"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("T")]
    [InlineData("TRYX")]
    public void Uc_harfli_olmayan_para_birimi_reddedilmeli(string kod)
        => Should.Throw<ArgumentException>(() => Money.Of(100, kod));

    [Fact]
    public void Para_birimi_null_olamaz()
        => Should.Throw<ArgumentException>(() => Money.Of(100, null!));
}

/// <summary>
/// Durum sözlüğü bekçisi. Yeni bir <see cref="PaymentStatus"/> değeri eklenip haritaya
/// yazılmazsa hata DERLEMEDE değil, o durumu ilk yazan ödemede çıkar: kayıt kaydedilemez
/// ya da okunurken KeyNotFound atar. Bu test o boşluğu saniyeler içinde söyler.
/// </summary>
public sealed class PaymentStatusMapTests
{
    [Fact]
    public void Her_durumun_veritabani_karsiligi_olmali()
    {
        foreach (var durum in Enum.GetValues<PaymentStatus>())
            PaymentStatusMap.ToDb.ShouldContainKey(durum);
    }

    [Fact]
    public void Haritalama_gidis_donus_ayni_degeri_vermeli()
    {
        foreach (var durum in Enum.GetValues<PaymentStatus>())
            PaymentStatusMap.FromDb[PaymentStatusMap.ToDb[durum]].ShouldBe(durum);
    }

    [Fact]
    public void Veritabani_degerleri_tekil_olmali()
    {
        // İki durum aynı dizeye eşlenirse okuma sırasında biri diğerine dönüşür:
        // "iptal edildi" ödeme "başarılı" görünebilirdi.
        PaymentStatusMap.ToDb.Values.ShouldBeUnique();
        PaymentStatusMap.FromDb.Count.ShouldBe(PaymentStatusMap.ToDb.Count);
    }
}

public sealed class SubscriptionBillableTests
{
    [Theory]
    [InlineData(SubscriptionStatus.Active, true)]
    [InlineData(SubscriptionStatus.Trialing, true)] // deneme dönemi de dönem ilerletir
    public void Aktif_ve_deneme_abonelikleri_faturalanabilir(SubscriptionStatus durum, bool beklenen)
        => Abonelik(durum).IsBillable.ShouldBe(beklenen);

    [Theory]
    [InlineData(SubscriptionStatus.Cancelled)]
    [InlineData(SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.Paused)]
    public void Diger_durumlar_faturalanmamali(SubscriptionStatus durum)
    {
        // Faturalanabilirlik tek satırlık bir kural ama yanlış tarafa düşerse
        // iptal etmiş müşteriden para çekilir — geri dönüşü pahalı bir hata.
        Abonelik(durum).IsBillable.ShouldBeFalse();
    }

    private static Subscription Abonelik(SubscriptionStatus durum) => new()
    {
        CustomerRef = "musteri-1",
        CardToken = "tok_test",
        Status = durum,
    };
}
