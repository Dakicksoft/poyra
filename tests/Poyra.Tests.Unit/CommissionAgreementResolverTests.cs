using Poyra.Modules.Recon.Domain;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// On-us oranı seçimi. Bu kural üç tüketicide birden geçerlidir — rota maliyeti, alacak
/// defteri, ekstre denetimi — ve ayrışmaları sessiz bir yanlış değil, BANKAYA HAKSIZ
/// SUÇLAMA üretir: rota %1,80 (on-us) bekler, defter %2,50 (genel) yazar, banka ekstrede
/// %1,80 keser ve denetim "banka eksik kesmiş" diye sahte bulgu açar.
/// </summary>
public sealed class CommissionAgreementResolverTests
{
    private static CommissionAgreement Anlasma(int installments, int rateBps, string? bankCode = null)
        => new()
        {
            ConnectorAccountId = Guid.Empty,
            InstallmentCount = installments,
            RateBps = rateBps,
            BankCode = bankCode,
            ValorDays = 1,
        };

    // Garanti POS: kendi kartına %1,80, gerisine %2,50
    private static readonly CommissionAgreement Genel = Anlasma(1, 250);
    private static readonly CommissionAgreement OnUs = Anlasma(1, 180, "0062");
    private static readonly CommissionAgreement[] Anlasmalar = [Genel, OnUs];

    [Fact]
    public void Kart_bankasina_ozel_oran_geneli_ezmeli()
        => CommissionAgreementResolver.Resolve(Anlasmalar, 1, "0062")!.RateBps.ShouldBe(180);

    [Fact]
    public void Baska_bankanin_karti_genel_orana_dusmeli()
        => CommissionAgreementResolver.Resolve(Anlasmalar, 1, "0064")!.RateBps.ShouldBe(250);

    [Fact]
    public void Kart_bilinmiyorsa_genel_oran_kullanilmali()
    {
        // Hosted akışta müşteri henüz kart girmemiştir. On-us varsayıp ucuz oran seçmek
        // rotayı gerçekte daha pahalı olan POS'a yollar ve deftere eksik alacak yazar.
        CommissionAgreementResolver.Resolve(Anlasmalar, 1, null)!.RateBps.ShouldBe(250);
        CommissionAgreementResolver.Resolve(Anlasmalar, 1, "")!.RateBps.ShouldBe(250);
    }

    [Fact]
    public void Banka_kodu_buyuk_kucuk_harf_duyarsiz_eslesmeli()
        => CommissionAgreementResolver.Resolve([Genel, Anlasma(1, 180, "abc")], 1, "ABC")!
            .RateBps.ShouldBe(180);

    [Fact]
    public void Taksit_sayisi_tutmayan_anlasma_hic_secilmemeli()
    {
        // 6 taksit anlaşması yok → tek çekim oranı YANLIŞLIKLA uygulanmamalı
        CommissionAgreementResolver.Resolve(Anlasmalar, 6, "0062").ShouldBeNull();

        // Doğru taksitte on-us yine kazanır
        CommissionAgreementResolver.Resolve(
            [Anlasma(6, 400), Anlasma(6, 320, "0062"), Genel, OnUs], 6, "0062")!.RateBps.ShouldBe(320);
    }

    [Fact]
    public void Genel_oran_yoksa_ve_banka_tutmuyorsa_null_donmeli()
    {
        // Anlaşma tanımsızlığı SESSİZCE sıfır sayılmaz — çağıranlar null'ı ayrıca sayar
        CommissionAgreementResolver.Resolve([OnUs], 1, "0064").ShouldBeNull();
        CommissionAgreementResolver.Resolve([OnUs], 1, null).ShouldBeNull();
    }

    [Fact]
    public void On_us_anlasmasi_listede_once_gelse_de_sonra_gelse_de_kazanmali()
    {
        // Sıra bağımlılığı olmamalı: genel oran on-us'tan sonra okunsa bile ezmemeli
        CommissionAgreementResolver.Resolve([OnUs, Genel], 1, "0062")!.RateBps.ShouldBe(180);
        CommissionAgreementResolver.Resolve([Genel, OnUs], 1, "0062")!.RateBps.ShouldBe(180);
    }
}
