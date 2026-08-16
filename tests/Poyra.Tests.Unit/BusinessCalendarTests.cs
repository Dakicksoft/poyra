using Poyra.Modules.Recon.Domain;
using Poyra.Modules.Recon.Infrastructure;
using Shouldly;
using Xunit;
using Poyra.SharedKernel.Domain;

namespace Poyra.Tests.Unit;

public sealed class BusinessCalendarTests
{
    private static readonly IReadOnlySet<DateOnly> NoHolidays = new HashSet<DateOnly>();

    [Fact]
    public void Hafta_sonu_atlanmali()
    {
        var friday = new DateOnly(2026, 8, 7); // Cuma
        BusinessCalendar.AddBusinessDays(friday, 1, NoHolidays)
            .ShouldBe(new DateOnly(2026, 8, 10)); // Pazartesi
        BusinessCalendar.AddBusinessDays(friday, 2, NoHolidays)
            .ShouldBe(new DateOnly(2026, 8, 11)); // Salı
    }

    [Fact]
    public void Tatil_atlanmali_ve_zincirlenebilmeli()
    {
        var wednesday = new DateOnly(2026, 10, 28); // Çarşamba; 29 Ekim Cumhuriyet Bayramı Perşembe
        var holidays = new HashSet<DateOnly> { new(2026, 10, 29) };

        BusinessCalendar.AddBusinessDays(wednesday, 1, holidays)
            .ShouldBe(new DateOnly(2026, 10, 30)); // Cuma (Perşembe tatil)
        BusinessCalendar.AddBusinessDays(wednesday, 2, holidays)
            .ShouldBe(new DateOnly(2026, 11, 2)); // Pazartesi (hafta sonu da atlanır)
    }

    [Fact]
    public void Sifir_is_gunu_ayni_gunu_dondurmeli()
    {
        var day = new DateOnly(2026, 8, 5);
        BusinessCalendar.AddBusinessDays(day, 0, NoHolidays).ShouldBe(day);
    }

    [Fact]
    public void Is_gunu_kontrolu_dogru_olmali()
    {
        var holidays = new HashSet<DateOnly> { new(2026, 4, 23) };
        BusinessCalendar.IsBusinessDay(new DateOnly(2026, 8, 8), NoHolidays).ShouldBeFalse(); // Cumartesi
        BusinessCalendar.IsBusinessDay(new DateOnly(2026, 4, 23), holidays).ShouldBeFalse(); // tatil
        BusinessCalendar.IsBusinessDay(new DateOnly(2026, 8, 5), holidays).ShouldBeTrue(); // Çarşamba
    }
}

public sealed class TrMoneyTests
{
    [Theory]
    [InlineData("1.499,00", 149_900)]
    [InlineData("1499,5", 149_950)]
    [InlineData("1499", 149_900)]
    [InlineData("0,01", 1)]
    [InlineData("-45,90", -4_590)]
    public void Tr_bicimli_tutar_kurusa_cevrilmeli(string raw, long expected)
    {
        TrMoney.TryParseToKurus(raw, out var kurus).ShouldBeTrue();
        kurus.ShouldBe(expected);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1,234")] // 3 ondalık — kuruş altı yok
    [InlineData("")]
    public void Gecersiz_tutar_reddedilmeli(string raw)
        => TrMoney.TryParseToKurus(raw, out _).ShouldBeFalse();
}

public sealed class BankStatementParserTests
{
    [Fact]
    public void NestPay_formati_tr_tutar_ve_turkce_tiplerle_cozulmeli()
    {
        var result = new NestPayCsvStatementParser().Parse(new StringReader("""
            ORDER_ID;TRAN_TYPE;AMOUNT;COMMISSION;NET;VALOR
            att_abc;Satış;1.499,00;29,98;1.469,02;05.08.2026
            att_abc;İade;45,90;0;-45,90;
            """));

        result.Errors.ShouldBeEmpty();
        result.Lines.Count.ShouldBe(2);

        var sale = result.Lines[0];
        sale.LineType.ShouldBe("sale");
        sale.GrossMinor.ShouldBe(149_900);
        sale.CommissionMinor.ShouldBe(2_998);
        sale.NetMinor.ShouldBe(146_902);
        sale.ValueDate.ShouldBe(new DateOnly(2026, 8, 5));

        result.Lines[1].LineType.ShouldBe("refund");
        result.Lines[1].GrossMinor.ShouldBe(4_590);
    }

    [Fact]
    public void NestPay_hatali_satirlar_raporlanmali()
    {
        var result = new NestPayCsvStatementParser().Parse(new StringReader("""
            att_x;Havale;10,00;1,00;9,00
            att_y;Satış;on;1,00;9,00
            """));

        result.Lines.ShouldBeEmpty();
        result.Errors.Count.ShouldBe(2);
        result.Errors[0].ShouldContain("satır 1");
        result.Errors[1].ShouldContain("TR biçiminde");
    }

    [Fact]
    public void Gvp_formati_kurus_ve_s_i_tipleriyle_cozulmeli()
    {
        var result = new GvpCsvStatementParser().Parse(new StringReader("""
            OrderId;Type;Gross;Commission;Net;Valor
            att_abc;S;149900;2998;146902;05.08.2026
            att_abc;I;4590;0;-4590;
            """));

        result.Errors.ShouldBeEmpty();
        result.Lines[0].LineType.ShouldBe("sale");
        result.Lines[0].GrossMinor.ShouldBe(149_900); // GVP: zaten kuruş
        result.Lines[1].LineType.ShouldBe("refund");
    }

    [Fact]
    public void Gvp_ondalikli_tutar_reddedilmeli()
    {
        var result = new GvpCsvStatementParser().Parse(new StringReader("att_x;S;1499,00;29;1470"));
        result.Errors.ShouldHaveSingleItem().ShouldContain("kuruş");
    }
}
