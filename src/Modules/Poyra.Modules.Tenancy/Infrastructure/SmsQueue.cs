using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Poyra.Modules.Tenancy.Domain;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Messaging;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Tenancy.Infrastructure;

public sealed class SmsQueue(TenancyDbContext db) : ISmsQueue
{
    public async Task EnqueueAsync(SmsMessage message, CancellationToken ct)
    {
        var phone = TurkishPhone.ToE164(message.ToPhone)
            ?? throw new PoyraException(400, "sms.invalid_phone",
                $"Geçersiz TR cep numarası: '{message.ToPhone}'. Sabit hatlara SMS gönderilemez.");

        db.SmsMessages.Add(new SmsMessageRecord
        {
            TenantId = message.TenantId,
            ToPhone = phone,
            Body = message.Body,
            Purpose = message.Purpose,
            Segments = TurkishPhone.SegmentCount(message.Body),
        });

        await db.SaveChangesAsync(ct);
    }
}

public sealed class LoggingSmsTransport(ILogger<LoggingSmsTransport> logger) : ISmsTransport
{
    public Task SendAsync(SmsMessage message, CancellationToken ct)
    {
        logger.LogWarning(
            "SMS sağlayıcısı tanımlı değil — mesaj GÖNDERİLMEDİ, günlüğe yazıldı. Alıcı: {To}\n{Body}",
            message.ToPhone, message.Body);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Netgsm taşıyıcısı (XML API'si — <c>/bulkhttppost.asmx</c>).
///
/// TR'de en yaygın sağlayıcılardan biri. Yanıt gövdesi HTTP 200 ile bile hata
/// kodu taşır ("20", "30", "40"…) — durum koduna bakıp geçmek, gönderilmemiş
/// mesajı gönderilmiş saymaktır.
/// TODO(cert): başlık (msgheader) onayı Netgsm panelinden alınmalı; onaysız
/// başlıkla gönderilen mesaj sessizce düşer.
/// </summary>
public sealed class NetgsmSmsTransport(
    IHttpClientFactory httpClientFactory, IConfiguration configuration) : ISmsTransport
{
    public const string HttpClientName = "poyra-sms-netgsm";

    public async Task SendAsync(SmsMessage message, CancellationToken ct)
    {
        var baseUrl = configuration["Sms:Netgsm:BaseUrl"] ?? "https://api.netgsm.com.tr";
        var user = Require("Sms:Netgsm:UserCode");
        var password = Require("Sms:Netgsm:Password");
        var header = Require("Sms:Netgsm:Header");

        // Numara ülke kodsuz beklenir: +905321234567 → 5321234567
        var phone = message.ToPhone.TrimStart('+');
        if (phone.StartsWith("90", StringComparison.Ordinal))
            phone = phone[2..];

        var xml = new XDocument(
            new XElement("mainbody",
                new XElement("header",
                    new XElement("company", "Netgsm"),
                    new XElement("usercode", user),
                    new XElement("password", password),
                    new XElement("type", "1:n"),
                    new XElement("msgheader", header)),
                new XElement("body",
                    new XElement("msg", new XCData(message.Body)),
                    new XElement("no", phone))));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/bulkhttppost.asmx")
        {
            Content = new StringContent(xml.ToString(), Encoding.UTF8, "text/xml"),
        };

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, ct);
        var body = (await response.Content.ReadAsStringAsync(ct)).Trim();

        if (!response.IsSuccessStatusCode)
            throw new PoyraException(502, "sms.provider_error",
                $"Netgsm HTTP {(int)response.StatusCode}.");

        // Başarı: "00 <mesajid>" ya da "01 <mesajid>". Diğer her şey hatadır.
        var code = body.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (code is not ("00" or "01" or "02"))
            throw new PoyraException(502, "sms.provider_error", $"Netgsm hata kodu: {code}");
    }

    private string Require(string key)
        => configuration[key] ?? throw new PoyraException(500, "sms.not_configured",
            $"SMS ayarı eksik: {key}");
}

/// <summary>
/// İleti Merkezi taşıyıcısı (JSON REST — <c>/send-sms</c>).
/// TODO(cert): gönderici adı (sender) İleti Merkezi panelinden onaylanmalı.
/// </summary>
public sealed class IletiMerkeziSmsTransport(
    IHttpClientFactory httpClientFactory, IConfiguration configuration) : ISmsTransport
{
    public const string HttpClientName = "poyra-sms-iletimerkezi";

    public async Task SendAsync(SmsMessage message, CancellationToken ct)
    {
        var baseUrl = configuration["Sms:IletiMerkezi:BaseUrl"] ?? "https://api.iletimerkezi.com";
        var key = Require("Sms:IletiMerkezi:Key");
        var hash = Require("Sms:IletiMerkezi:Hash");
        var sender = Require("Sms:IletiMerkezi:Sender");

        var payload = new
        {
            request = new
            {
                authentication = new { key, hash },
                order = new
                {
                    sender,
                    message = new
                    {
                        text = message.Body,
                        receipents = new { number = new[] { message.ToPhone.TrimStart('+') } },
                    },
                },
            },
        };

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.PostAsJsonAsync($"{baseUrl}/v1/send-sms/json", payload, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new PoyraException(502, "sms.provider_error",
                $"İleti Merkezi HTTP {(int)response.StatusCode}.");

        // Sağlayıcı HTTP 200 ile hata döndürebilir — gövdedeki durum kodu okunur
        if (!body.Contains("\"code\":\"200\"", StringComparison.Ordinal)
            && !body.Contains("\"code\": \"200\"", StringComparison.Ordinal))
            throw new PoyraException(502, "sms.provider_error",
                $"İleti Merkezi yanıtı başarısız: {body[..Math.Min(200, body.Length)]}");
    }

    private string Require(string key)
        => configuration[key] ?? throw new PoyraException(500, "sms.not_configured",
            $"SMS ayarı eksik: {key}");
}

/// <summary>
/// SMS teslimat işçisi — e-posta işçisiyle aynı desen ve aynı tuzak: en eski N
/// mesajı alır, tek tek dener, kalıcı hatada 'failed' damgalar.
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class SmsDispatchJob(
    TenancyDbContext db, ISmsTransport transport, IClock clock, ILogger<SmsDispatchJob> logger)
{
    public async Task DispatchPendingAsync()
    {
        var pending = await db.SmsMessages
            .Where(m => m.Status == EmailStatus.Pending)
            .OrderBy(m => m.CreatedAt)
            .Take(50)
            .ToListAsync();

        foreach (var record in pending)
        {
            record.AttemptCount++;
            try
            {
                await transport.SendAsync(
                    new SmsMessage(record.TenantId, record.ToPhone, record.Body, record.Purpose), default);

                record.Status = EmailStatus.Sent;
                record.SentAt = clock.UtcNow;
                record.LastError = null;
            }
            catch (Exception ex)
            {
                record.LastError = ex.Message[..Math.Min(500, ex.Message.Length)];
                if (record.AttemptCount >= SmsMessageRecord.MaxAttempts)
                {
                    record.Status = EmailStatus.Failed;
                    logger.LogError(ex, "SMS {Purpose} → {To} {Attempts} denemede gönderilemedi.",
                        record.Purpose, record.ToPhone, record.AttemptCount);
                }
            }
        }

        await db.SaveChangesAsync();
    }
}
