using System.Net;
using System.Net.Mail;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Poyra.Modules.Tenancy.Domain;
using Poyra.SharedKernel.Messaging;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Tenancy.Infrastructure;

public sealed class EmailQueue(TenancyDbContext db) : IEmailQueue
{
    public async Task EnqueueAsync(EmailMessage message, CancellationToken ct)
    {
        db.EmailMessages.Add(new EmailMessageRecord
        {
            TenantId = message.TenantId,
            ToEmail = message.ToEmail,
            Subject = message.Subject,
            BodyHtml = message.BodyHtml,
            BodyText = message.BodyText,
            Purpose = message.Purpose,
        });

        await db.SaveChangesAsync(ct);
    }
}

public sealed class LoggingEmailTransport(ILogger<LoggingEmailTransport> logger) : IEmailTransport
{
    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        logger.LogWarning(
            "SMTP tanımlı değil — posta GÖNDERİLMEDİ, günlüğe yazıldı. Alıcı: {To} · Konu: {Subject}\n{Body}",
            message.ToEmail, message.Subject, message.BodyText);
        return Task.CompletedTask;
    }
}
public sealed class SmtpEmailTransport(IConfiguration configuration) : IEmailTransport
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var host = configuration["Poyra:Smtp:Host"]
                   ?? throw new InvalidOperationException("Poyra:Smtp:Host tanımlı değil.");
        var port = int.TryParse(configuration["Poyra:Smtp:Port"], out var p) ? p : 587;
        var from = configuration["Poyra:Smtp:From"] ?? "poyra@localhost";
        var fromName = configuration["Poyra:Smtp:FromName"] ?? "Poyra";

        using var client = new SmtpClient(host, port)
        {
            EnableSsl = !bool.TryParse(configuration["Poyra:Smtp:UseSsl"], out var ssl) || ssl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };

        if (configuration["Poyra:Smtp:Username"] is { Length: > 0 } username)
            client.Credentials = new NetworkCredential(username, configuration["Poyra:Smtp:Password"]);

        using var mail = new MailMessage
        {
            From = new MailAddress(from, fromName),
            Subject = message.Subject,
            Body = message.BodyText,
            IsBodyHtml = false,
        };
        mail.To.Add(message.ToEmail);
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
            message.BodyHtml, null, "text/html"));

        await client.SendMailAsync(mail, ct);
    }
}

[AutomaticRetry(Attempts = 0)]
public sealed class EmailDispatchJob(
    TenancyDbContext db,
    IEmailTransport transport,
    IClock clock,
    ILogger<EmailDispatchJob> logger)
{
    public async Task DispatchPendingAsync()
    {
        var pending = await db.EmailMessages
            .Where(m => m.Status == EmailStatus.Pending)
            .OrderBy(m => m.CreatedAt)
            .Take(50)
            .ToListAsync();

        foreach (var record in pending)
        {
            record.AttemptCount++;
            try
            {
                await transport.SendAsync(new EmailMessage(
                    record.TenantId, record.ToEmail, record.Subject,
                    record.BodyHtml, record.BodyText, record.Purpose), default);

                record.Status = EmailStatus.Sent;
                record.SentAt = clock.UtcNow;
                record.LastError = null;
                Redact(record);
            }
            catch (Exception ex)
            {
                record.LastError = ex.Message[..Math.Min(500, ex.Message.Length)];
                if (record.AttemptCount >= EmailMessageRecord.MaxAttempts)
                {
                    record.Status = EmailStatus.Failed;
                    Redact(record);
                    logger.LogError(ex, "Posta {Purpose} → {To} {Attempts} denemede gönderilemedi.",
                        record.Purpose, record.ToEmail, record.AttemptCount);
                }
            }
        }

        await db.SaveChangesAsync();
    }

    private static void Redact(EmailMessageRecord record)
    {
        record.BodyHtml = "";
        record.BodyText = "(gövde gönderim sonrası silindi — tek kullanımlık bağlantı içerir)";
    }
}
