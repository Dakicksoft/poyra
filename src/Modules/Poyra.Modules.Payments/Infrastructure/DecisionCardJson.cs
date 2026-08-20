using System.Text.Json;
using Poyra.Modules.Routing.Contracts;

namespace Poyra.Modules.Payments.Infrastructure;

/// <summary>
/// RoutingResultJson'daki "card" düğümünün yazım/okuma sözleşmesi: rota kararı ANINDA motorun
/// gördüğü kart fact'leri. Simülatör kararı aynı bilgiyle yeniden oynatır — MaskedPan'dan
/// sonradan öğrenilen kart, karar anında bilinmiyorduysa kurallara sızmaz (sonradan-bilgi yok).
/// Düğüm null ise kart karar anında BİLİNMİYORDU; anahtar hiç yoksa kayıt bu alandan eskidir.
/// Bin kalıcılaştırılırken İLK 6 HANEYE kırpılır ve yalnız rakam kabul edilir — MaskedPan'la
/// aynı duruş: doğrulanmamış girdi ya da PAN öneki olduğu gibi diske inmez. Bedeli bilinçli:
/// 7-8 haneli "bin starts_with" kuralları replay'de eşleşmeyebilir (motor karar anında 8 haneyi
/// görmüş olabilir) — güvenlik burada birebirlikten önce gelir.
/// </summary>
public static class DecisionCardJson
{
    public static object? From(CardFacts? card) => card is null ? null : new
    {
        bin = SanitizeBin(card.Bin),
        bank_code = card.BankCode,
        program = card.Program,
        brand = card.Brand,
        card_type = card.CardType,
        commercial = card.IsCommercial,
        country = card.Country,
    };

    /// <summary>Yalnız 6+ haneli rakam dizisi BIN sayılır; ilk 6 hanesi saklanır, gerisi atılır.</summary>
    public static string? SanitizeBin(string? bin)
        => bin is { Length: >= 6 } && bin.All(char.IsAsciiDigit) ? bin[..6] : null;

    /// <summary>
    /// "card" anahtarı varsa true; null düğüm karar anında kartın bilinmediğini söyler.
    /// Kök nesne değilse (bozuk/eski kayıt) false — çağıran MaskedPan yaklaşıklamasına düşer.
    /// </summary>
    public static bool TryRead(JsonElement root, out CardFacts? card)
    {
        card = null;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("card", out var node))
            return false; // eski/bozuk kayıt — karar-anı kartı yazılmamış

        if (node.ValueKind == JsonValueKind.Null)
            return true; // kart karar anında bilinmiyordu → null oynatılmalı

        if (node.ValueKind != JsonValueKind.Object)
            return false;

        card = new CardFacts(
            ReadString(node, "bin"),
            ReadString(node, "bank_code"),
            ReadString(node, "program"),
            ReadString(node, "brand"),
            ReadString(node, "card_type"),
            node.TryGetProperty("commercial", out var commercial)
                && commercial.ValueKind == JsonValueKind.True,
            ReadString(node, "country"));
        return true;
    }

    private static string? ReadString(JsonElement node, string name)
        => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
