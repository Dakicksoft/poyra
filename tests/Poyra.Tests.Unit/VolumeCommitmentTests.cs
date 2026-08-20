using Poyra.Modules.Routing.Contracts;
using Poyra.Modules.Routing.Dsl;
using Poyra.Modules.Routing.Infrastructure;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Hacim taahhüdü stratejisi. TR'de daha iyi komisyon oranı genelde ciro taahhüdü
/// karşılığında alınır; taahhüt tutmazsa oran geri yükselir. Strateji, açığı olan
/// hesabı öne alır ve açık kapanınca hesap kendiliğinden normal sıraya döner.
/// </summary>
public sealed class VolumeCommitmentStrategyTests
{
    private static RoutingCandidate Aday(
        string label, long? cost = 300, CommitmentProgress? commitment = null)
        => new(Guid.NewGuid(), label, cost, AuthRate: 0.9, MedianLatencyMs: 200, commitment);

    [Fact]
    public void Acigi_olan_hesap_one_alinmali()
    {
        // Garanti: 500.000 hedef, 380.000 gerçekleşti → 120.000 açık, 18 gün kaldı
        var acikli = Aday("Garanti POS", commitment: new CommitmentProgress(500_000_00, 380_000_00, 18));
        var taahhutsuz = Aday("Diğer POS");

        RoutingStrategies.Order([taahhutsuz, acikli], RoutingStrategies.Commitment, new StrategyWeights())[0]
            .Label.ShouldBe("Garanti POS");
    }

    [Fact]
    public void Taahhut_tutmussa_hesap_one_gecmemeli()
    {
        // Hedef aşıldı → açık 0 → aciliyet 0 → geldiği sırada kalır.
        // "Açık kapanınca normal sıraya döner" davranışı için ayrı bir kural gerekmez.
        var tutmus = Aday("Garanti POS", commitment: new CommitmentProgress(500_000_00, 520_000_00, 18));
        var taahhutsuz = Aday("Diğer POS");

        RoutingStrategies.Order([taahhutsuz, tutmus], RoutingStrategies.Commitment, new StrategyWeights())
            .Select(c => c.Label).ShouldBe(["Diğer POS", "Garanti POS"]);
    }

    [Fact]
    public void Daha_acil_olan_taahhut_one_gecmeli()
    {
        // Aynı açık, farklı süre: 3 günde 60.000 ₺, 18 günde 120.000 ₺'den ACİLDİR
        var az_zaman = Aday("Acil POS", commitment: new CommitmentProgress(100_000_00, 40_000_00, 3));
        var cok_zaman = Aday("Rahat POS", commitment: new CommitmentProgress(200_000_00, 80_000_00, 18));

        RoutingStrategies.Order([cok_zaman, az_zaman], RoutingStrategies.Commitment, new StrategyWeights())
            .Select(c => c.Label).ShouldBe(["Acil POS", "Rahat POS"]);
    }

    [Fact]
    public void Taahhutsuz_hesap_elenmemeli_yalnizca_arkaya_dusmeli()
    {
        // Taahhüdü olmayan tek hesap kalırsa yine de rotada olmalı — aksi hâlde
        // taahhüt tanımlamayan işyerinde 'commitment' stratejisi rotayı boşaltırdı
        var taahhutsuz = Aday("Tek POS");

        var ordered = RoutingStrategies.Order(
            [taahhutsuz], RoutingStrategies.Commitment, new StrategyWeights());

        ordered.ShouldHaveSingleItem().Label.ShouldBe("Tek POS");
    }

    [Fact]
    public void Strateji_taninmali_ve_olcum_kotasina_girmemeli()
    {
        RoutingStrategies.IsKnown(RoutingStrategies.Commitment).ShouldBeTrue();

        // Taahhüt yapılandırılmış bir hedef + sayılan hacimdir; trafik akmasa da bayatlamaz.
        // Ölçüm kotası (F1) yalnız başarı/hız gibi ÖLÇÜLEN sinyaller içindir.
        RoutingStrategies.UsesMeasuredSignals(RoutingStrategies.Commitment).ShouldBeFalse();
    }

    [Fact]
    public void Gerekce_kalan_tutari_ve_sureyi_yazmali()
    {
        var doc = RuleDocument.Parse("""{ "strategy": "commitment" }""");
        var acikli = Aday("Garanti POS", commitment: new CommitmentProgress(500_000_00, 380_000_00, 18));
        var taahhutsuz = Aday("Diğer POS");

        var decision = RoutingEngine.DecideCore(
            doc, new RoutingFacts(Guid.NewGuid(), 50_000, "TRY", 1, 14), [taahhutsuz, acikli]);

        decision.AccountIds[0].ShouldBe(acikli.AccountId);
        decision.Reason.ShouldContain("hacim taahhüdü");
        decision.Reason.ShouldContain("kalan 120.000,00 ₺");
        decision.Reason.ShouldContain("18 gün");
        decision.Reason.ShouldContain("500.000,00 ₺"); // aylık hedef
    }

    [Fact]
    public void Aciligi_olan_taahhut_yoksa_gerekce_bunu_soylemeli()
    {
        var doc = RuleDocument.Parse("""{ "strategy": "commitment" }""");
        var tutmus = Aday("Garanti POS", commitment: new CommitmentProgress(500_000_00, 520_000_00, 18));

        RoutingEngine.DecideCore(doc, new RoutingFacts(Guid.NewGuid(), 50_000, "TRY", 1, 14), [tutmus])
            .Reason.ShouldContain("açığı olan taahhüt yok");
    }
}

/// <summary>Taahhüt dönemi: banka anlaşmaları TAKVİM AYI üzerinden konuşulur.</summary>
public sealed class CommitmentPeriodTests
{
    [Fact]
    public void Donem_turkiye_ayinin_ilk_gunu_olmali()
    {
        // 1 Ağustos 00:30 TR = 31 Temmuz 21:30 UTC. UTC ayı kullanılsaydı bu işlem
        // TEMMUZ taahhüdüne yazılır ve ağustos açığı olduğundan büyük görünürdü.
        var (start, _) = RoutingEngine.MonthWindow(
            new DateTimeOffset(2026, 7, 31, 21, 30, 0, TimeSpan.Zero));

        // Aynı an, UTC ofsetiyle: Npgsql timestamptz parametresine yalnız UTC kabul eder
        start.ShouldBe(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(3)));
        start.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(2026, 8, 1, 31)]  // ayın ilk günü: bugün dahil 31 gün
    [InlineData(2026, 8, 20, 12)]
    [InlineData(2026, 8, 31, 1)]  // son gün: en az 1 — sıfıra bölme olmaz
    [InlineData(2026, 2, 28, 1)]  // 2026 artık yıl değil
    public void Kalan_gun_bugunu_de_saymali(int year, int month, int day, int expected)
    {
        var trNoon = new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.FromHours(3));

        RoutingEngine.MonthWindow(trNoon.ToUniversalTime()).DaysLeft.ShouldBe(expected);
    }

    [Fact]
    public void Kalan_gun_sifira_bolunmeye_yol_acmamali()
    {
        var sonGun = new CommitmentProgress(100_000_00, 0, DaysLeft: 0);

        sonGun.RequiredDailyMinor.ShouldBe(100_000_00); // 0'a değil 1'e bölünür
        double.IsInfinity(sonGun.RequiredDailyMinor).ShouldBeFalse();
    }

    [Fact]
    public void Hedefi_asan_hacimde_acik_negatife_dusmemeli()
    {
        new CommitmentProgress(500_000_00, 520_000_00, 18).GapMinor.ShouldBe(0);
    }
}
