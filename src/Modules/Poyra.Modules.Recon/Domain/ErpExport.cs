using Poyra.SharedKernel.Domain;

namespace Poyra.Modules.Recon.Domain;

public enum ErpFormat
{
    PoyraCsv,
    LogoCsv,
    MikroCsv,
}

public static class ErpFormatMap
{
    public static readonly IReadOnlyDictionary<ErpFormat, string> ToDb =
        new Dictionary<ErpFormat, string>
        {
            [ErpFormat.PoyraCsv] = "poyra_csv",
            [ErpFormat.LogoCsv] = "logo_csv",
            [ErpFormat.MikroCsv] = "mikro_csv",
        };

    public static readonly IReadOnlyDictionary<string, ErpFormat> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}

public sealed class ErpExportSettings : ITenantOwned, IAuditable
{
    public Guid TenantId { get; init; }
    public ErpFormat Format { get; set; } = ErpFormat.PoyraCsv;

    /// <summary>108 — POS'tan alacaklar (brüt tahsilat buraya borç yazılır).</summary>
    public string PosReceivableAccount { get; set; } = "108.01";

    /// <summary>102 — Bankalar (valör günü hesaba geçen net).</summary>
    public string BankAccount { get; set; } = "102.01";

    /// <summary>653/760 — POS komisyon gideri.</summary>
    public string CommissionExpenseAccount { get; set; } = "653.01";

    /// <summary>Fiş numarası öneki — muhasebede Poyra fişleri ayırt edilebilsin.</summary>
    public string DocumentPrefix { get; set; } = "POS";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static ErpExportSettings Default(Guid tenantId) => new() { TenantId = tenantId };
}

/// <param name="Debit">Borç tutarı (kuruş) — 0 ise bu satır alacaktır.</param>
/// <param name="Credit">Alacak tutarı (kuruş).</param>
public sealed record VoucherLine(
    int LineNo, string AccountCode, string Description, long Debit, long Credit);

/// <summary>
/// Muhasebe fişi. Çift taraflı kayıt: borç toplamı alacak toplamına EŞİT olmak zorundadır.
/// </summary>
public sealed record ErpVoucher(
    string DocumentNo,
    DateOnly DocumentDate,
    string Currency,
    IReadOnlyList<VoucherLine> Lines)
{
    public long DebitTotal => Lines.Sum(l => l.Debit);
    public long CreditTotal => Lines.Sum(l => l.Credit);
    public bool IsBalanced => DebitTotal == CreditTotal;
}
