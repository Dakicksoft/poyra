using System.Text;

namespace Poyra.SharedKernel.Messaging;

public sealed record SmsMessage(Guid? TenantId, string ToPhone, string Body, string Purpose);

public interface ISmsQueue
{
    Task EnqueueAsync(SmsMessage message, CancellationToken ct);
}

public interface ISmsTransport
{
    Task SendAsync(SmsMessage message, CancellationToken ct);
}

/// <summary>
/// TR cep telefonu numarası normalleştirme ve SMS metin hesabı.
///
/// Numara biçimi işyerinden işyerine değişir: "0532 123 45 67", "+90 532 123 4567",
/// "5321234567" hepsi aynı numaradır. Sağlayıcıya farklı biçimde gitmesi, teslimatın
/// sessizce başarısız olması demektir.
/// </summary>
public static class TurkishPhone
{
    /// <summary>
    /// E.164 biçimine çevirir: +905321234567. Geçersizse null.
    ///
    /// TR cep numaraları 5 ile başlar ve 10 hanedir. Sabit hat (2xx/3xx/4xx) bilinçli
    /// olarak REDDEDİLİR: SMS gitmez ve "gönderildi" demek işyerini yanıltır.
    /// </summary>
    public static string? ToE164(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var digits = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsAsciiDigit(c))
                digits.Append(c);
        }

        var value = digits.ToString();

        // Ülke kodu ve baştaki sıfır kırpılır: 90532…, 0532…, 532… → 532…
        if (value.Length == 12 && value.StartsWith("90", StringComparison.Ordinal))
            value = value[2..];
        else if (value.Length == 11 && value.StartsWith('0'))
            value = value[1..];

        if (value.Length != 10 || value[0] != '5')
            return null;

        return "+90" + value;
    }

    public static bool IsValid(string? raw) => ToE164(raw) is not null;

    /// <summary>
    /// Kaç SMS kredisi tüketilir.
    /// </summary>
    public static int SegmentCount(string body)
    {
        if (string.IsNullOrEmpty(body))
            return 0;

        var unicode = body.Any(c => !Gsm7.Contains(c));
        var single = unicode ? 70 : 160;
        var concatenated = unicode ? 67 : 153; // çok parçalı mesajda başlık yer kaplar

        return body.Length <= single
            ? 1
            : (int)Math.Ceiling(body.Length / (double)concatenated);
    }

    public static string ToAscii(string body)
    {
        var builder = new StringBuilder(body.Length);
        foreach (var c in body)
        {
            builder.Append(c switch
            {
                'ç' => 'c', 'Ç' => 'C',
                'ğ' => 'g', 'Ğ' => 'G',
                'ı' => 'i', 'İ' => 'I',
                'ö' => 'o', 'Ö' => 'O',
                'ş' => 's', 'Ş' => 'S',
                'ü' => 'u', 'Ü' => 'U',
                _ => c,
            });
        }

        return builder.ToString();
    }

    /// <summary>GSM-7 temel alfabesi (Türkçe'ye özgü harfler burada YOKTUR).</summary>
    private static readonly HashSet<char> Gsm7 =
    [
        .. "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?"
           + "¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà"
           + "\f^{}\\[~]|€",
    ];
}
