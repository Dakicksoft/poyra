using Poyra.Modules.Recon.Domain;
using Poyra.Modules.Recon.Infrastructure;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Muhasebe fişi. Çift taraflı kayıtta borç ≠ alacak ise ERP dosyayı reddeder ve
/// muhasebecinin günü çöpe gider; ayrıca TR yerel ayarında "1234.56" yazmak sessiz
/// bir 100 kat hatadır. İkisi de burada çivilenir.
/// </summary>
public sealed class ErpVoucherTests
{
    private static readonly ErpExportSettings Settings = new()
    {
        TenantId = Guid.NewGuid(),
        PosReceivableAccount = "108.01",
        BankAccount = "102.01",
        CommissionExpenseAccount = "653.01",
        DocumentPrefix = "POS",
    };

    private static ReconStatementLine Line(
        int no, StatementLineType type, long gross, long commission, long net,
        LineMatchStatus status = LineMatchStatus.Matched)
        => new()
        {
            TenantId = Settings.TenantId,
            StatementId = Guid.NewGuid(),
            LineNo = no,
            OrderId = $"att_{no:0000}",
            LineType = type,
            GrossMinor = gross,
            CommissionMinor = commission,
            NetMinor = net,
            MatchStatus = status,
        };

    [Fact]
    public void Satis_fisi_dengeli_ve_dogru_hesaplara_yazmali()
    {
        // 1.000,00 ₺ satış, %2 komisyon → banka 980,00 alır, 20,00 gider yazılır
        var voucher = ErpVoucherBuilder.Build(Settings, new DateOnly(2026, 8, 3), "TRY",
            [Line(1, StatementLineType.Sale, 100_000, 2_000, 98_000)]);

        voucher.IsBalanced.ShouldBeTrue();
        voucher.DocumentNo.ShouldBe("POS-20260803");

        var bank = voucher.Lines.Single(l => l.AccountCode == "102.01");
        var commission = voucher.Lines.Single(l => l.AccountCode == "653.01");
        var receivable = voucher.Lines.Single(l => l.AccountCode == "108.01");

        bank.Debit.ShouldBe(98_000);        // hesaba geçen net
        commission.Debit.ShouldBe(2_000);   // gider
        receivable.Credit.ShouldBe(100_000); // POS alacağı kapanır
    }

    [Fact]
    public void Iade_satiri_brutu_ve_bankayi_azaltmali()
    {
        // 1.000,00 satış (%2) + 400,00 iade → brüt 600,00, banka 580,00, komisyon 20,00
        var voucher = ErpVoucherBuilder.Build(Settings, new DateOnly(2026, 8, 3), "TRY",
        [
            Line(1, StatementLineType.Sale, 100_000, 2_000, 98_000),
            Line(2, StatementLineType.Refund, 40_000, 0, -40_000),
        ]);

        voucher.IsBalanced.ShouldBeTrue();
        voucher.Lines.Single(l => l.AccountCode == "102.01").Debit.ShouldBe(58_000);
        voucher.Lines.Single(l => l.AccountCode == "108.01").Credit.ShouldBe(60_000);
        voucher.Lines.Single(l => l.AccountCode == "653.01").Debit.ShouldBe(2_000);
    }

    [Fact]
    public void Net_iade_gunu_yonleri_ters_cevirmeli()
    {
        // Yalnız iade olan gün: banka hesabından ÇIKIŞ olur
        var voucher = ErpVoucherBuilder.Build(Settings, new DateOnly(2026, 8, 3), "TRY",
            [Line(1, StatementLineType.Refund, 50_000, 0, -50_000)]);

        voucher.IsBalanced.ShouldBeTrue();
        voucher.Lines.Single(l => l.AccountCode == "102.01").Credit.ShouldBe(50_000);
        voucher.Lines.Single(l => l.AccountCode == "108.01").Debit.ShouldBe(50_000);
    }

    [Fact]
    public void Eslesmeyen_satirlar_fise_girmemeli()
    {
        // Eşleşmeyen satır bir MUTABAKAT SORUNUdur; muhasebeye taşınmadan çözülmelidir
        var voucher = ErpVoucherBuilder.Build(Settings, new DateOnly(2026, 8, 3), "TRY",
        [
            Line(1, StatementLineType.Sale, 100_000, 2_000, 98_000),
            Line(2, StatementLineType.Sale, 50_000, 1_000, 49_000, LineMatchStatus.MissingInPoyra),
        ]);

        voucher.Lines.Single(l => l.AccountCode == "108.01").Credit.ShouldBe(100_000);
        voucher.IsBalanced.ShouldBeTrue();
    }

    [Fact]
    public void Dengesiz_ekstre_dengesiz_fis_uretmeli_ki_uc_nokta_reddedebilsin()
    {
        // brüt(1000) ≠ net(900) + komisyon(20) → uç nokta bunu 409 ile durdurur
        var voucher = ErpVoucherBuilder.Build(Settings, new DateOnly(2026, 8, 3), "TRY",
            [Line(1, StatementLineType.Sale, 100_000, 2_000, 90_000)]);

        voucher.IsBalanced.ShouldBeFalse();
    }

    [Theory]
    [InlineData(ErpFormat.PoyraCsv, "fis_no;tarih")]
    [InlineData(ErpFormat.LogoCsv, "FISNO;TARIH")]
    [InlineData(ErpFormat.MikroCsv, "EvrakNo;EvrakTarihi")]
    public void Bicimler_kendi_baslik_satirini_yazmali(ErpFormat format, string expectedHeader)
    {
        var voucher = ErpVoucherBuilder.Build(Settings, new DateOnly(2026, 8, 3), "TRY",
            [Line(1, StatementLineType.Sale, 100_000, 2_000, 98_000)]);

        ErpVoucherWriter.Write(voucher, format).ShouldStartWith(expectedHeader);
    }

    [Fact]
    public void Tutarlar_TR_yaziminda_olmali()
    {
        var voucher = ErpVoucherBuilder.Build(Settings, new DateOnly(2026, 8, 3), "TRY",
            [Line(1, StatementLineType.Sale, 149_950, 2_999, 146_951)]);

        var csv = ErpVoucherWriter.Write(voucher, ErpFormat.LogoCsv);

        // Logo/Mikro içe aktarıcıları Türkçe Windows yerel ayarıyla çalışır:
        // "1499.50" gördüğünde 149950 okur — sessiz 100 kat hata
        csv.ShouldContain("1469,51");
        csv.ShouldContain("29,99");
        csv.ShouldContain("1499,50");
        csv.ShouldNotContain("1469.51");
    }

    [Fact]
    public void Aciklamadaki_ayrac_temizlenmeli()
    {
        var settings = new ErpExportSettings { TenantId = Guid.NewGuid(), DocumentPrefix = "A;B" };
        var voucher = ErpVoucherBuilder.Build(settings, new DateOnly(2026, 8, 3), "TRY",
            [Line(1, StatementLineType.Sale, 10_000, 200, 9_800)]);

        var csv = ErpVoucherWriter.Write(voucher, ErpFormat.PoyraCsv);
        var dataRow = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1];

        // Sütun sayısı bozulmamalı — kaçan bir ayraç tüm satırı kaydırır
        dataRow.Split(';').Length.ShouldBe(8);
    }

    [Fact]
    public void Dosya_adi_fis_no_ve_bicimi_tasimali()
    {
        var voucher = ErpVoucherBuilder.Build(Settings, new DateOnly(2026, 8, 3), "TRY",
            [Line(1, StatementLineType.Sale, 10_000, 200, 9_800)]);

        ErpVoucherWriter.FileName(voucher, ErpFormat.MikroCsv).ShouldBe("POS-20260803-mikro_csv.csv");
    }
}
