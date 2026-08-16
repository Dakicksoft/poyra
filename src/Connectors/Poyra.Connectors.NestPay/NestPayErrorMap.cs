using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.NestPay;

public static class NestPayErrorMap
{
    public static string ToUnified(string? procReturnCode) => procReturnCode switch
    {
        "00" => "", // onaylı — hata değil
        "05" or "01" or "02" or "34" => UnifiedErrors.CardDeclined,
        "51" => UnifiedErrors.InsufficientFunds,
        "33" or "54" => UnifiedErrors.ExpiredCard,
        "14" or "15" or "56" => UnifiedErrors.InvalidCard,
        "13" => UnifiedErrors.InvalidAmount,
        "57" or "58" or "62" => UnifiedErrors.NotPermitted,
        "61" or "65" => UnifiedErrors.LimitExceeded,
        "91" or "96" => UnifiedErrors.IssuerUnavailable,
        _ => UnifiedErrors.ProcessingError,
    };

    /// <summary>mdStatus: 1=tam doğrulama, 2/3/4=yarım (yine de kabul edilir); diğerleri başarısız.</summary>
    public static bool IsThreeDsSuccessful(string? mdStatus)
        => mdStatus is "1" or "2" or "3" or "4";
}
