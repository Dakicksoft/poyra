using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Poyra.Field.Core;

public sealed record SyncRunResult(
    int Sent, int Accepted, int Duplicate, int Rejected,
    bool Reachable, TimeSpan ClockSkew, string? Error);

/// <summary>
/// Kuyruğu sunucuya taşıyan istemci.
///
/// <b>Tek kural:</b> ağ hatası kayıt KAYBETTİRMEZ. Sunucudan açık bir sonuç gelmediği
/// sürece kayıt <see cref="QueueState.Pending"/> kalır ve bir sonraki turda yeniden
/// gönderilir. <c>ClientOpId</c> değişmediği için tekrar gönderim, sunucuda ikinci bir
/// tahsilat açmaz.
/// </summary>
public sealed class FieldSyncClient(HttpClient http, FieldQueue queue, Func<DateTimeOffset> now)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Cihaz saatinin sapması.
    /// </summary>
    public static readonly TimeSpan SkewWarning = TimeSpan.FromMinutes(5);

    public async Task<SyncRunResult> RunAsync(
        string agentCode, string deviceId, CancellationToken ct = default)
    {
        var pending = await queue.PendingAsync(ct: ct);
        if (pending.Count == 0)
            return new SyncRunResult(0, 0, 0, 0, Reachable: true, TimeSpan.Zero, null);

        var payload = new
        {
            agentCode,
            deviceId,
            operations = pending.Select(p => new
            {
                clientOpId = p.ClientOpId,
                method = p.Method,
                amountMinor = p.AmountMinor,
                currency = p.Currency,
                capturedAtDevice = p.CapturedAtDevice,
                customerRef = p.CustomerRef,
                description = p.Description,
                note = p.Note,
                latitude = p.Latitude,
                longitude = p.Longitude,
            }).ToArray(),
        };

        await queue.MarkAttemptAsync(pending.Select(p => p.ClientOpId), now(), ct);

        SyncResponseDto? response;
        try
        {
            var http_ = await http.PostAsJsonAsync("/v1/field/sync", payload, Json, ct);

            if (!http_.IsSuccessStatusCode)
            {
                // Sunucu hatası da kayıt kaybettirmez: 4xx/5xx ayrımı yapmadan kuyruk
                // korunur. Yanlış bir 4xx yüzünden günün tahsilatını silmek, ağ hatasından
                // çok daha pahalıdır — kullanıcı verisi silinmez, beklenir.
                var body = await http_.Content.ReadAsStringAsync(ct);
                return new SyncRunResult(pending.Count, 0, 0, 0, Reachable: true,
                    TimeSpan.Zero, $"HTTP {(int)http_.StatusCode}: {Trim(body)}");
            }

            response = await http_.Content.ReadFromJsonAsync<SyncResponseDto>(Json, ct);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Kapsama alanı dışında olmak HATA DEĞİLDİR, sahanın normalidir.
            return new SyncRunResult(pending.Count, 0, 0, 0, Reachable: false, TimeSpan.Zero,
                exception.Message);
        }
        catch (Exception exception)
        {
            // GENİŞ yakalama, bilinçli. Senkron bir arka plan işidir; ne olursa olsun
            // uygulamayı ÖLDÜREMEZ — temsilci müşteri karşısındayken kapanan bir
            // uygulama, o günün tahsilatını yapamaması demektir.
            return new SyncRunResult(pending.Count, 0, 0, 0, Reachable: false, TimeSpan.Zero,
                $"{exception.GetType().Name}: {exception.Message}");
        }

        if (response is null)
            return new SyncRunResult(pending.Count, 0, 0, 0, true, TimeSpan.Zero, "Boş yanıt.");

        await queue.ApplyAsync(
            response.Results.Select(r => new SyncOutcome(
                r.ClientOpId, r.Outcome, r.CollectionId, r.Status, r.CheckoutUrl, r.Reason)),
            ct);

        // Sunucu zamanı OTORİTEDİR. Cihaz onu benimsemez — sistem saatini
        // değiştirmek başka uygulamaları bozar — ama farkı bilir ve temsilciyi uyarır.
        var skew = response.ServerTime - now();

        return new SyncRunResult(
            pending.Count, response.Accepted, response.Duplicate, response.Rejected,
            Reachable: true, skew, null);
    }

    private static string Trim(string body) => body.Length <= 200 ? body : body[..200];

    private sealed record SyncResponseDto(
        [property: JsonPropertyName("serverTime")] DateTimeOffset ServerTime,
        [property: JsonPropertyName("accepted")] int Accepted,
        [property: JsonPropertyName("duplicate")] int Duplicate,
        [property: JsonPropertyName("rejected")] int Rejected,
        [property: JsonPropertyName("results")] List<ResultDto> Results);

    private sealed record ResultDto(
        Guid ClientOpId, string Outcome, string? CollectionId,
        string? Status, string? CheckoutUrl, string? Reason);
}
