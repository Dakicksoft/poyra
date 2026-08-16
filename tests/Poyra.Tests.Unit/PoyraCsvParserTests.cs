using Poyra.Modules.Recon.Infrastructure;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

public sealed class PoyraCsvParserTests
{
    private static StatementParseResult Parse(string csv)
        => new PoyraCsvStatementParser().Parse(new StringReader(csv));

    [Fact]
    public void Baslikli_satis_ve_iade_satirlari_cozulmeli()
    {
        var result = Parse("""
            order_id;type;gross_minor;commission_minor;net_minor;value_date
            att_abc;sale;10000;250;9750;2026-08-05
            att_abc;refund;4000;0;-4000;
            """);

        result.Errors.ShouldBeEmpty();
        result.Lines.Count.ShouldBe(2);

        var sale = result.Lines[0];
        sale.OrderId.ShouldBe("att_abc");
        sale.LineType.ShouldBe("sale");
        sale.GrossMinor.ShouldBe(10_000);
        sale.CommissionMinor.ShouldBe(250);
        sale.ValueDate.ShouldBe(new DateOnly(2026, 8, 5));

        var refund = result.Lines[1];
        refund.LineType.ShouldBe("refund");
        refund.NetMinor.ShouldBe(-4_000); // net negatif olabilir
        refund.ValueDate.ShouldBeNull();
    }

    [Fact]
    public void Type_bos_ise_sale_sayilmali_ve_bosluklar_kirpilmali()
    {
        var result = Parse("att_abc ; ;10000;250;9750");

        result.Errors.ShouldBeEmpty();
        result.Lines.ShouldHaveSingleItem().LineType.ShouldBe("sale");
        result.Lines[0].OrderId.ShouldBe("att_abc");
    }

    [Fact]
    public void Hatali_satirlar_satir_numarasiyla_raporlanmali_iyiler_korunmali()
    {
        var result = Parse("""
            att_iyi;sale;10000;250;9750
            att_kotu;sale;on-bin;250;9750
            att_kisa;sale
            att_tarih;sale;10000;250;9750;05.08.2026
            att_tip;havale;10000;250;9750
            """);

        result.Lines.ShouldHaveSingleItem().OrderId.ShouldBe("att_iyi");
        result.Errors.Count.ShouldBe(4);
        result.Errors.ShouldContain(e => e.Contains("satır 2") && e.Contains("kuruş"));
        result.Errors.ShouldContain(e => e.Contains("satır 3") && e.Contains("5 alan"));
        result.Errors.ShouldContain(e => e.Contains("satır 4") && e.Contains("yyyy-MM-dd"));
        result.Errors.ShouldContain(e => e.Contains("satır 5") && e.Contains("sale"));
    }

    [Fact]
    public void Bos_satirlar_atlanmali()
    {
        var result = Parse("\natt_abc;sale;100;1;99\n\n");
        result.Lines.ShouldHaveSingleItem();
        result.Errors.ShouldBeEmpty();
    }
}
