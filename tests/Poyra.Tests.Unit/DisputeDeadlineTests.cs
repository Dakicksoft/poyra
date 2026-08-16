using Poyra.Modules.Disputes.Domain;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Kanıt süresi hesabı. Bu aritmetiğin bir gün şaşması, savunulabilir bir dosyayı
/// sessizce kaybettirir — bu yüzden hafta sonu, tatil ve gün sınırı ayrı ayrı sınanır.
/// </summary>
public sealed class DisputeDeadlineTests
{
    private static readonly IReadOnlySet<DateOnly> NoHolidays = new HashSet<DateOnly>();

    /// <summary>Test tarihleri TR saatiyle verilir — sistemin gün sınırı UTC+3'tür.</summary>
    private static DateTimeOffset Tr(int year, int month, int day, int hour = 10)
        => new(year, month, day, hour, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Hafta_sonu_sureden_sayilmamali()
    {
        // 3 Ağustos 2026 Pazartesi + 9 iş günü → 14 Ağustos Cuma
        // (takvimle saysaydık 12 Ağustos Çarşamba çıkardı: iki gün fazla vaat)
        var due = DisputeDeadline.Compute(Tr(2026, 8, 3), DisputeStage.Chargeback, NoHolidays);

        var dueTr = due.ToOffset(TimeSpan.FromHours(3));
        dueTr.Date.ShouldBe(new DateTime(2026, 8, 14));
        dueTr.DayOfWeek.ShouldBe(DayOfWeek.Friday);
    }

    [Fact]
    public void Resmi_tatil_sureden_sayilmamali()
    {
        // 30 Ağustos Zafer Bayramı araya girerse süre bir gün uzar
        var holidays = new HashSet<DateOnly> { new(2026, 8, 31) }; // Pazartesi tatil varsayımı

        var without = DisputeDeadline.Compute(Tr(2026, 8, 24), DisputeStage.Chargeback, NoHolidays);
        var with = DisputeDeadline.Compute(Tr(2026, 8, 24), DisputeStage.Chargeback, holidays);

        with.ShouldBeGreaterThan(without);

        // Tam BİR İŞ GÜNÜ uzar. Takvim farkı 1 gün olmak ZORUNDA DEĞİL: kayma hafta
        // sonuna denk gelirse takvimde 3 gün ileri gider.
        var withoutDay = DateOnly.FromDateTime(without.ToOffset(TimeSpan.FromHours(3)).DateTime);
        var withDay = DateOnly.FromDateTime(with.ToOffset(TimeSpan.FromHours(3)).DateTime);
        Poyra.SharedKernel.Domain.BusinessCalendar.AddBusinessDays(withoutDay, 1, holidays)
            .ShouldBe(withDay);
    }

    [Fact]
    public void Sure_TR_gun_sonunda_bitmeli()
    {
        var due = DisputeDeadline.Compute(Tr(2026, 8, 3), DisputeStage.Chargeback, NoHolidays);

        // Son gün 23:59:59'a kadar geçerli — UTC gün sonuna göre almak işyerine
        // TR'de 03:00'a kadar süre varmış gibi gösterirdi
        var dueTr = due.ToOffset(TimeSpan.FromHours(3));
        dueTr.Hour.ShouldBe(23);
        dueTr.Minute.ShouldBe(59);

        // timestamptz'e yazılacak: Npgsql yalnız UTC ofseti kabul eder
        due.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Gunun_hangi_saatinde_acildigi_sonucu_degistirmemeli()
    {
        // Banka bildirimi 09:00 da gelse 23:00 da gelse süre AYNI güne biter:
        // saat farkı yüzünden bir işyerine bir gün eksik vermek kabul edilemez
        var early = DisputeDeadline.Compute(Tr(2026, 8, 3, 1), DisputeStage.Chargeback, NoHolidays);
        var late = DisputeDeadline.Compute(Tr(2026, 8, 3, 23), DisputeStage.Chargeback, NoHolidays);

        early.ShouldBe(late);
    }

    [Theory]
    [InlineData(DisputeStage.Retrieval, 7)]
    [InlineData(DisputeStage.Chargeback, 9)]
    [InlineData(DisputeStage.PreArbitration, 7)]
    [InlineData(DisputeStage.Arbitration, 5)]
    public void Her_kademenin_kendi_penceresi_olmali(DisputeStage stage, int expectedBusinessDays)
    {
        DisputeDeadline.BusinessDaysByStage[stage].ShouldBe(expectedBusinessDays);

        var due = DisputeDeadline.Compute(Tr(2026, 8, 3), stage, NoHolidays);
        due.ShouldBeGreaterThan(Tr(2026, 8, 3));
    }

    [Fact]
    public void Ust_kademe_penceresi_daralmali()
    {
        // Üst kademe her zaman daha dar: "eskisinden geç olmalı" varsaymak yanlış
        var chargeback = DisputeDeadline.BusinessDaysByStage[DisputeStage.Chargeback];
        var arbitration = DisputeDeadline.BusinessDaysByStage[DisputeStage.Arbitration];

        arbitration.ShouldBeLessThan(chargeback);
    }

    [Fact]
    public void Uyari_penceresi_en_dar_kademeden_kisa_olmali()
    {
        // Uyarı, en dar pencerede bile dosya açıldıktan SONRA gitmeli; aksi halde
        // hakem aşamasında uyarı ile son tarih aynı anda düşerdi
        var narrowest = DisputeDeadline.BusinessDaysByStage.Values.Min();

        DisputeDeadline.WarningWindow.TotalDays.ShouldBeLessThan(narrowest);
    }
}

/// <summary>Durum makinesi: kapanmış dosya yeniden açılamaz, süre geçtiyse savunma alınmaz.</summary>
public sealed class DisputeStateTests
{
    private static Dispute Open(DateTimeOffset? dueAt = null, DisputeStage stage = DisputeStage.Chargeback)
        => Dispute.Open(
            Guid.NewGuid(), Guid.NewGuid(), "pay_test", 50_000, "TRY",
            DisputeReasons.Fraud, stage,
            new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero),
            dueAt ?? new DateTimeOffset(2026, 8, 14, 20, 59, 59, TimeSpan.Zero),
            null, null, null);

    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Savunma_gonderilince_incelemeye_gecmeli()
    {
        var dispute = Open();
        dispute.Submit(Now, "Teslim tutanağı ektedir.");

        dispute.Status.ShouldBe(DisputeStatus.UnderReview);
        dispute.SubmittedAt.ShouldBe(Now);
        dispute.EvidenceSummary.ShouldBe("Teslim tutanağı ektedir.");
    }

    [Fact]
    public void Suresi_gecmis_dosya_savunulamamali()
    {
        var dispute = Open(dueAt: Now.AddHours(-1));

        Should.Throw<Poyra.SharedKernel.Errors.PoyraException>(() => dispute.Submit(Now, "Geç."))
            .Code.ShouldBe("dispute.evidence_window_closed");
    }

    [Fact]
    public void Kapanmis_dosyaya_dokunulamamali()
    {
        var dispute = Open();
        dispute.Close(DisputeStatus.Lost, Now);

        Should.Throw<Poyra.SharedKernel.Errors.PoyraException>(() => dispute.Submit(Now, "Tekrar"))
            .Code.ShouldBe("dispute.already_closed");
        Should.Throw<Poyra.SharedKernel.Errors.PoyraException>(() => dispute.Accept(Now))
            .Code.ShouldBe("dispute.already_closed");
        Should.Throw<Poyra.SharedKernel.Errors.PoyraException>(() => dispute.Close(DisputeStatus.Won, Now))
            .Code.ShouldBe("dispute.already_closed");
    }

    [Fact]
    public void Sonuc_yalniz_kazanildi_veya_kaybedildi_olabilmeli()
    {
        var dispute = Open();

        // 'open' geçerli bir DURUM ama geçerli bir SONUÇ değil
        Should.Throw<Poyra.SharedKernel.Errors.PoyraException>(() => dispute.Close(DisputeStatus.Open, Now))
            .Code.ShouldBe("dispute.invalid_outcome");
    }

    [Fact]
    public void Suresi_gecen_acik_dosya_kapatilmali()
    {
        var dispute = Open(dueAt: Now.AddHours(-1));
        dispute.IsOverdue(Now).ShouldBeTrue();

        dispute.MarkExpired(Now);
        dispute.Status.ShouldBe(DisputeStatus.Expired);
        dispute.ClosedAt.ShouldBe(Now);
    }

    [Fact]
    public void Gonderilmis_dosya_sure_gecse_de_expired_olmamali()
    {
        // Savunma zamanında iletildi; bankanın kararı gecikiyorsa dosya kaybedilmiş
        // sayılamaz — MarkExpired yalnız 'open' dosyaya dokunur
        var dispute = Open();
        dispute.Submit(Now, "Savunma");

        dispute.MarkExpired(Now.AddDays(30));

        dispute.Status.ShouldBe(DisputeStatus.UnderReview);
    }

    [Fact]
    public void Kademe_yalniz_yukari_gidebilmeli()
    {
        var dispute = Open(stage: DisputeStage.PreArbitration);

        Should.Throw<Poyra.SharedKernel.Errors.PoyraException>(
                () => dispute.Escalate(DisputeStage.Chargeback, Now.AddDays(5)))
            .Code.ShouldBe("dispute.invalid_stage");
    }

    [Fact]
    public void Ust_kademe_dosyayi_yeniden_savunulabilir_yapmali()
    {
        var dispute = Open();
        dispute.Submit(Now, "İlk savunma");
        dispute.DueWarningSentAt = Now;

        var newDue = Now.AddDays(7);
        dispute.Escalate(DisputeStage.PreArbitration, newDue);

        dispute.Status.ShouldBe(DisputeStatus.Open);
        dispute.SubmittedAt.ShouldBeNull();
        dispute.EvidenceDueAt.ShouldBe(newDue);
        dispute.DueWarningSentAt.ShouldBeNull(); // yeni pencere yeni uyarı hakkı verir
    }
}
