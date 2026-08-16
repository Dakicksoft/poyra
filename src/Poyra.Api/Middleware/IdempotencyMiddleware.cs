using Microsoft.EntityFrameworkCore;
using Npgsql;
using Poyra.Modules.Payments;
using Poyra.Modules.Payments.Domain;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Api.Middleware;

public sealed class IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> logger)
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayHeader = "Idempotency-Replayed";


    private static readonly string[] Guarded =
    [
        "/v1/payments",   // create (+confirm), confirm, confirm-direct, cancel
        "/v1/refunds",
        "/v1/subscriptions",
    ];

    private const int MaxKeyLength = 255;

    public async Task InvokeAsync(HttpContext context, IClock clock, TenantContext tenant)
    {
        if (!ShouldGuard(context) ||
            !context.Request.Headers.TryGetValue(HeaderName, out var raw) ||
            raw.ToString() is not { Length: > 0 } key)
        {
            await next(context);
            return;
        }

        if (key.Length > MaxKeyLength)
        {
            await WriteErrorAsync(context, 400, "idempotency.key_too_long",
                $"Idempotency-Key en çok {MaxKeyLength} karakter olabilir.");
            return;
        }

        if (!tenant.HasTenant)
        {
            await next(context);
            return;
        }

        var db = context.RequestServices.GetRequiredService<PaymentsDbContext>();
        var endpoint = $"{context.Request.Method} {context.Request.Path}";

        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync(context.RequestAborted);
        context.Request.Body.Position = 0;

        var requestHash = IdempotencyRecord.Hash(endpoint, body);

        var record = new IdempotencyRecord
        {
            TenantId = tenant.TenantId,
            Key = key,
            Endpoint = endpoint,
            RequestHash = requestHash,
        };

        db.IdempotencyRecords.Add(record);
        try
        {
            await db.SaveChangesAsync(context.RequestAborted);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            db.ChangeTracker.Clear();
            await ReplayOrRejectAsync(context, db, clock, key, requestHash);
            return;
        }

        await CaptureAsync(context, db, clock, record, key);
    }

    private async Task CaptureAsync(
        HttpContext context, PaymentsDbContext db, IClock clock, IdempotencyRecord record, string key)
    {
        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context);
        }
        catch (Exception ex) when (IsDeterministicRejection(ex))
        {
            await ReleaseAsync(db, record, context.RequestAborted);
            throw;
        }
        finally
        {
            context.Response.Body = original;
        }

        buffer.Position = 0;
        var responseBody = await new StreamReader(buffer).ReadToEndAsync(context.RequestAborted);

        if (context.Response.StatusCode < 500)
        {
            record.StatusCode = context.Response.StatusCode;
            record.ResponseBody = string.IsNullOrWhiteSpace(responseBody) ? null : responseBody;
            record.CompletedAt = clock.UtcNow;

            try
            {
                await db.SaveChangesAsync(context.RequestAborted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Idempotency kaydı tamamlanamadı: {Key}", key);
            }
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(original, context.RequestAborted);
    }


    private static bool IsDeterministicRejection(Exception ex)
        => ex is FluentValidation.ValidationException or SharedKernel.Errors.PoyraException;

    private static async Task ReleaseAsync(
        PaymentsDbContext db, IdempotencyRecord record, CancellationToken ct)
    {
        db.IdempotencyRecords.Remove(record);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception)
        {
            db.ChangeTracker.Clear();
        }
    }

    private static async Task ReplayOrRejectAsync(
        HttpContext context, PaymentsDbContext db, IClock clock, string key, string requestHash)
    {
        var existing = await db.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Key == key, context.RequestAborted);

        if (existing is null)
        {
            await context.Response.WriteAsJsonAsync(
                new { code = "idempotency.unavailable", message = "Tekrar deneyin.", errors = (object?)null });
            context.Response.StatusCode = 503;
            return;
        }

        if (existing.RequestHash != requestHash)
        {
            await WriteErrorAsync(context, 409, "idempotency.key_reused",
                "Bu Idempotency-Key farklı bir istek için kullanılmış. Her işlem için yeni anahtar üretin.");
            return;
        }

        if (existing.IsStale(clock.UtcNow))
        {
            await WriteErrorAsync(context, 409, "idempotency.stale",
                "Bu anahtarla başlayan istek tamamlanmadı ve durumu bilinmiyor. "
                + "Ödemenin durumunu sorgulayın; gerekirse YENİ bir anahtarla tekrar deneyin.");
            return;
        }

        if (!existing.IsCompleted)
        {
            await WriteErrorAsync(context, 409, "idempotency.in_progress",
                "Aynı anahtarla bir istek hâlâ işleniyor. Kısa süre sonra tekrar deneyin.");
            return;
        }

        context.Response.StatusCode = existing.StatusCode ?? 200;
        context.Response.Headers[ReplayHeader] = "true";
        if (existing.ResponseBody is { Length: > 0 } stored)
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(stored, context.RequestAborted);
        }
    }

    private static bool ShouldGuard(HttpContext context)
        => HttpMethods.IsPost(context.Request.Method)
           && Guarded.Any(prefix => context.Request.Path.StartsWithSegments(prefix));

    private static Task WriteErrorAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { code, message, errors = (object?)null });
    }
}
