using System.Text;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Webhooks.Domain;
using Poyra.SharedKernel.Security;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Webhooks.Infrastructure;

[AutomaticRetry(Attempts = 0)]
public sealed class WebhookDeliveryJob(
    WebhooksDbContext db,
    TenantContext tenant,
    IHttpClientFactory httpClientFactory,
    ICredentialProtector protector,
    IClock clock,
    IBackgroundJobClient jobs)
{
    public const string HttpClientName = "poyra-webhooks";

    public async Task DeliverAsync(Guid tenantId, Guid deliveryId)
    {
        tenant.Set(tenantId);

        var delivery = await db.WebhookDeliveries.SingleOrDefaultAsync(d => d.Id == deliveryId);
        if (delivery is null || delivery.Status is DeliveryStatus.Succeeded or DeliveryStatus.Exhausted)
            return;

        var endpoint = await db.WebhookEndpoints.AsNoTracking()
            .SingleAsync(e => e.Id == delivery.EndpointId);

        var secret = protector.Unprotect(endpoint.SecretEncrypted)["secret"];
        var now = clock.UtcNow;

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url);
        request.Content = new StringContent(delivery.PayloadJson, Encoding.UTF8, "application/json");
        request.Headers.Add(WebhookSigner.Header,
            WebhookSigner.BuildHeader(secret, now.ToUnixTimeSeconds(), delivery.PayloadJson));
        request.Headers.Add("Poyra-Event", delivery.EventType);
        request.Headers.Add("Poyra-Delivery-Id", delivery.Id.ToString());

        int? httpStatus = null;
        string? error = null;
        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request);
            httpStatus = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                delivery.RegisterSuccess(now, httpStatus.Value);
                await db.SaveChangesAsync();
                return;
            }

            error = $"HTTP {httpStatus}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            error = ex.Message;
        }

        var nextDelay = WebhookRetryPolicy.NextDelay(delivery.AttemptCount + 1);
        delivery.RegisterFailure(httpStatus, error!, nextDelay is null ? null : now + nextDelay);
        await db.SaveChangesAsync();

        if (nextDelay is not null)
            jobs.Schedule<WebhookDeliveryJob>(j => j.DeliverAsync(tenantId, deliveryId), nextDelay.Value);
    }
}
