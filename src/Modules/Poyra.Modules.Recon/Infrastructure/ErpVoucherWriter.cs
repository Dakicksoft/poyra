using System.Globalization;
using System.Text;
using Poyra.Modules.Recon.Domain;

namespace Poyra.Modules.Recon.Infrastructure;

/// <summary>
/// Gün sonu ekstresinden muhasebe fişi kurar. Türkiye'de POS tahsilatının standart kaydı:
///
///   Borç  102 Bankalar               = NET (valör günü hesaba geçen)
///   Borç  653 Komisyon giderleri     = KOMİSYON (bankanın kestiği)
///        Alacak 108 POS alacakları   = BRÜT (satış anında borçlandırılan)
///
/// İade satırları bu kaydı TERS yönde etkiler: brüt azalır, banka girişi azalır.
/// </summary>
public static class ErpVoucherBuilder
{
    public static ErpVoucher Build(
        ErpExportSettings settings,
        DateOnly statementDate,
        string currency,
        IReadOnlyList<ReconStatementLine> lines)
    {
        // Yalnız EŞLEŞEN satırlar fişe girer: eşleşmeyen satır bir mutabakat sorunudur,
        // muhasebeye taşınmadan önce çözülmelidir (aksi halde ERP'ye hatalı kayıt gider).
        var matched = lines.Where(l => l.MatchStatus == LineMatchStatus.Matched).ToList();

        var sales = matched.Where(l => l.LineType == StatementLineType.Sale).ToList();
        var refunds = matched.Where(l => l.LineType == StatementLineType.Refund).ToList();

        var gross = sales.Sum(l => l.GrossMinor) - refunds.Sum(l => l.GrossMinor);
        var commission = matched.Sum(l => l.CommissionMinor);
        var net = matched.Sum(l => l.NetMinor);

        var voucherLines = new List<VoucherLine>();
        var no = 1;

        if (net != 0)
        {
            voucherLines.Add(net > 0
                ? new VoucherLine(no++, settings.BankAccount,
                    $"POS net tahsilat {statementDate:dd.MM.yyyy}", net, 0)
                : new VoucherLine(no++, settings.BankAccount,
                    $"POS net iade {statementDate:dd.MM.yyyy}", 0, -net));
        }

        if (commission != 0)
        {
            voucherLines.Add(commission > 0
                ? new VoucherLine(no++, settings.CommissionExpenseAccount,
                    $"POS komisyonu {statementDate:dd.MM.yyyy}", commission, 0)
                : new VoucherLine(no++, settings.CommissionExpenseAccount,
                    $"POS komisyon iadesi {statementDate:dd.MM.yyyy}", 0, -commission));
        }

        if (gross != 0)
        {
            voucherLines.Add(gross > 0
                ? new VoucherLine(no++, settings.PosReceivableAccount,
                    $"POS alacak kapama {statementDate:dd.MM.yyyy}", 0, gross)
                : new VoucherLine(no++, settings.PosReceivableAccount,
                    $"POS alacak iadesi {statementDate:dd.MM.yyyy}", -gross, 0));
        }

        return new ErpVoucher(
            $"{settings.DocumentPrefix}-{statementDate:yyyyMMdd}", statementDate, currency, voucherLines);
    }
}

/// <summary>
/// Fişi ERP'nin okuduğu metne çevirir. Üç biçim de NOKTALI VİRGÜLLE ayrılır ve tutarları
/// TR yazımıyla (virgül ondalık) yazar — Logo ve Mikro içe aktarıcıları Türkçe Windows
/// yerel ayarıyla çalışır ve "1234.56" gördüğünde 123456 okur; bu, sessiz bir 100 kat hatadır.
///
/// TODO(erp): sütun adları/sıraları müşterinin ERP sürümüne göre değişebilir; ilk kurulumda
/// muhasebe ile birebir doğrulanmalıdır. Bu yüzden 'poyra_csv' biçimi de sunulur:
/// alanları açıkça adlandırır, elle eşlemeye uygundur.
/// </summary>
public static class ErpVoucherWriter
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static string Write(ErpVoucher voucher, ErpFormat format) => format switch
    {
        ErpFormat.LogoCsv => WriteLogo(voucher),
        ErpFormat.MikroCsv => WriteMikro(voucher),
        _ => WritePoyra(voucher),
    };

    public static string FileName(ErpVoucher voucher, ErpFormat format)
        => $"{voucher.DocumentNo}-{ErpFormatMap.ToDb[format]}.csv";

    /// <summary>Poyra biçimi: alanlar açık adlandırılır — elle eşleme ve denetim içindir.</summary>
    private static string WritePoyra(ErpVoucher voucher)
    {
        var builder = new StringBuilder();
        builder.AppendLine("fis_no;tarih;sira;hesap_kodu;aciklama;borc;alacak;para_birimi");

        foreach (var line in voucher.Lines)
        {
            builder.AppendLine(string.Join(';',
                Escape(voucher.DocumentNo),
                voucher.DocumentDate.ToString("dd.MM.yyyy"),
                line.LineNo,
                Escape(line.AccountCode),
                Escape(line.Description),
                Money(line.Debit),
                Money(line.Credit),
                voucher.Currency));
        }

        return builder.ToString();
    }

    /// <summary>Logo (Tiger/Go) muhasebe fişi aktarım düzeni.</summary>
    private static string WriteLogo(ErpVoucher voucher)
    {
        var builder = new StringBuilder();
        builder.AppendLine("FISNO;TARIH;SATIRNO;HESAPKODU;ACIKLAMA;BORC;ALACAK;DOVIZ");

        foreach (var line in voucher.Lines)
        {
            builder.AppendLine(string.Join(';',
                Escape(voucher.DocumentNo),
                voucher.DocumentDate.ToString("dd.MM.yyyy"),
                line.LineNo,
                Escape(line.AccountCode),
                Escape(line.Description),
                Money(line.Debit),
                Money(line.Credit),
                voucher.Currency));
        }

        return builder.ToString();
    }

    /// <summary>Mikro (Jump/V16) muhasebe fiş aktarım düzeni.</summary>
    private static string WriteMikro(ErpVoucher voucher)
    {
        var builder = new StringBuilder();
        builder.AppendLine("EvrakNo;EvrakTarihi;SatirNo;HesapKodu;Aciklama;Borc;Alacak;DovizCinsi");

        foreach (var line in voucher.Lines)
        {
            builder.AppendLine(string.Join(';',
                Escape(voucher.DocumentNo),
                voucher.DocumentDate.ToString("dd.MM.yyyy"),
                line.LineNo,
                Escape(line.AccountCode),
                Escape(line.Description),
                Money(line.Debit),
                Money(line.Credit),
                voucher.Currency));
        }

        return builder.ToString();
    }

    /// <summary>Kuruş → TR yazımı ("14900" → "149,00"). Kuruş asla gizlenmez.</summary>
    private static string Money(long amountMinor) => (amountMinor / 100m).ToString("0.00", Tr);

    /// <summary>
    /// Ayraç HERHANGİ bir alana kaçarsa satır kayar ve ERP sütunları yanlış okur.
    /// Fiş numarası işyerinin yazdığı öneki taşır — doğrulama ilk kapı, bu ikinci kapıdır.
    /// </summary>
    private static string Escape(string value) => value.Replace(';', ' ');
}
