using Poyra.Modules.Subscriptions.Domain;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

public sealed class DunningPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Limit_yetersizse_maas_penceresinde_denenmeli()
    {
        var decision = DunningPolicy.Decide("poyra.insufficient_funds", attemptCount: 1, Now);

        decision.Action.ShouldBe(DunningAction.Retry);
        decision.NextAttemptAt.ShouldBe(new DateTimeOffset(2026, 8, 15, 9, 0, 0, TimeSpan.Zero));
        decision.Reason.ShouldContain("maaş");
    }

    [Fact]
    public void Maas_penceresi_ayin_15inden_sonra_sonraki_ayin_1i_olmali()
    {
        var afterMid = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);

        DunningPolicy.NextSalaryWindow(afterMid)
            .ShouldBe(new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Maas_penceresi_tam_15inde_bir_sonrakine_gecmeli()
    {
        // 15'i saat 12:00 — o günün 09:00 penceresi geçti, sıradaki 1 Eylül
        var onWindowDay = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        DunningPolicy.NextSalaryWindow(onWindowDay)
            .ShouldBe(new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Suresi_dolmus_kartta_kor_deneme_yapilmamali()
    {
        var decision = DunningPolicy.Decide("poyra.expired_card", attemptCount: 1, Now);

        decision.Action.ShouldBe(DunningAction.RequestCardUpdate);
        decision.NextAttemptAt.ShouldBeNull();
    }

    [Theory]
    [InlineData("poyra.invalid_card")]
    [InlineData("poyra.transaction_not_permitted")]
    [InlineData("poyra.limit_exceeded")]
    public void Kalici_retlerde_birakilmali(string code)
        => DunningPolicy.Decide(code, attemptCount: 1, Now).Action.ShouldBe(DunningAction.Abandon);

    [Fact]
    public void Gecici_retlerde_ustel_geri_cekilme_uygulanmali()
    {
        DunningPolicy.Decide("poyra.card_declined", 1, Now).NextAttemptAt.ShouldBe(Now.AddDays(1));
        DunningPolicy.Decide("poyra.card_declined", 2, Now).NextAttemptAt.ShouldBe(Now.AddDays(3));
        DunningPolicy.Decide("poyra.issuer_unavailable", 3, Now).NextAttemptAt.ShouldBe(Now.AddDays(7));
    }

    [Fact]
    public void Deneme_hakki_bitince_birakilmali()
    {
        var decision = DunningPolicy.Decide("poyra.insufficient_funds", DunningPolicy.MaxAttempts, Now);

        decision.Action.ShouldBe(DunningAction.Abandon);
        decision.Reason.ShouldContain("hakkı");
    }

    [Fact]
    public void Bilinmeyen_hata_temkinli_denenmeli()
        => DunningPolicy.Decide("poyra.bilinmeyen_kod", 1, Now).Action.ShouldBe(DunningAction.Retry);
}

public sealed class BillingIntervalTests
{
    [Fact]
    public void Periyot_ilerletme_dogru_olmali()
    {
        var start = new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero);

        BillingIntervalMap.Advance(start, BillingInterval.Day, 10)
            .ShouldBe(new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.Zero));
        BillingIntervalMap.Advance(start, BillingInterval.Week, 2)
            .ShouldBe(new DateTimeOffset(2026, 2, 14, 0, 0, 0, TimeSpan.Zero));
        // 31 Ocak + 1 ay → 28 Şubat (ay sonuna kırpılır, "ayın 31'i" tuzağı yok)
        BillingIntervalMap.Advance(start, BillingInterval.Month, 1)
            .ShouldBe(new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero));
        BillingIntervalMap.Advance(start, BillingInterval.Year, 1)
            .ShouldBe(new DateTimeOffset(2027, 1, 31, 0, 0, 0, TimeSpan.Zero));
    }
}
