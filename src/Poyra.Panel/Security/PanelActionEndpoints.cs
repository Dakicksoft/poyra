using Microsoft.AspNetCore.Mvc;
using Poyra.Modules.Compliance.Features;
using Poyra.Modules.Field.Features;
using Poyra.Modules.Connectors.Features;
using Poyra.Modules.Customers.Features;
using Poyra.Modules.Disputes.Features;
using Poyra.Modules.Recon.Features;
using Poyra.Modules.Risk.Features;
using Poyra.Modules.Tenancy.Features.Branding;
using Poyra.Modules.PaymentLinks.Features;
using Poyra.Modules.Payments.Features.CancelPayment;
using Poyra.Modules.Payments.Features.Refunds;
using Poyra.Modules.Subscriptions.Features;
using Poyra.Modules.Tenancy.Features.ApiKeyManagement;
using Poyra.Modules.Tenancy.Features.Users;
using Poyra.Modules.Webhooks.Features;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Domain;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Notifications;
using Poyra.SharedKernel.Tenancy;

namespace Poyra.Panel.Security;

public static class PanelActionEndpoints
{
    public static void MapPanelActionEndpoints(this WebApplication app)
    {
        app.MapPost("/odemeler/{id}/iptal", async (
            string id,
            HttpContext http,
            IDispatcher dispatcher,
            UserContext user,
            TenantContext tenant,
            INotificationPublisher notifications) =>
        {
            if (!user.HasRank(TenantRole.Operations))
                return Redirect(id, hata: "Bu işlem için en az 'operations' rolü gerekli.");

            try
            {
                await dispatcher.Send(new CancelPaymentCommand(id));
                await notifications.PublishAsync(new PoyraNotification(
                    tenant.TenantId, PoyraNotificationTypes.PaymentCancelled, id));
                return Redirect(id, sonuc: "Ödeme iptal edildi (void) — komisyon doğmadı.");
            }
            catch (PoyraException ex)
            {
                return Redirect(id, hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/odemeler/{id}/iade", async (
            string id,
            [FromForm] long? amountMinor,
            IDispatcher dispatcher,
            UserContext user,
            TenantContext tenant,
            INotificationPublisher notifications) =>
        {
            if (!user.HasRank(TenantRole.Operations))
                return Redirect(id, hata: "Bu işlem için en az 'operations' rolü gerekli.");

            try
            {
                var refund = await dispatcher.Send(new CreateRefundCommand(id, amountMinor));
                await notifications.PublishAsync(new PoyraNotification(
                    tenant.TenantId, PoyraNotificationTypes.RefundSucceeded, id));

                return Redirect(id, sonuc: refund.Status == Modules.Payments.Domain.RefundStatus.Succeeded
                    ? $"İade tamam: {refund.AmountMinor / 100m:N2} ₺ ({refund.Id})."
                    : $"İade başarısız: {refund.Error?.Code}");
            }
            catch (PoyraException ex)
            {
                return Redirect(id, hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/baglantilar-odeme/olustur", async (
            HttpContext http,
            IDispatcher dispatcher,
            UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Operations))
                return LinkRedirect(hata: "Bu işlem için en az 'operations' rolü gerekli.");

            var form = await http.Request.ReadFormAsync();
            string? Field(string key) => form.TryGetValue(key, out var v) ? v.ToString() : null;

            try
            {
                var expiresAt = UserInput.ToDate(Field("expiresOn")) is { } day
                    ? new DateTimeOffset(day.ToDateTime(TimeOnly.MaxValue), TimeSpan.FromHours(3))
                    : (DateTimeOffset?)null;

                var amountMinor = UserInput.ToKurus(Field("amountLira"));

                var link = await dispatcher.Send(new CreatePaymentLinkCommand(
                    Field("description") ?? "",
                    amountMinor > 0 ? amountMinor : null,
                    "TRY",
                    UserInput.ToInt(Field("maxInstallments"), 1),
                    expiresAt,
                    UserInput.ToInt(Field("maxUsage"), 0)));

                return LinkRedirect(sonuc: $"Bağlantı hazır: {link.Url}");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return LinkRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/baglantilar-odeme/{id}/kapat", async (
            string id, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Operations))
                return LinkRedirect(hata: "Bu işlem için en az 'operations' rolü gerekli.");

            try
            {
                await dispatcher.Send(new DisablePaymentLinkCommand(id));
                return LinkRedirect(sonuc: "Bağlantı kapatıldı — geçmiş tahsilatlar duruyor.");
            }
            catch (PoyraException ex)
            {
                return LinkRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        MapConnectorActions(app);
        MapSettingsActions(app);
        MapKarekodActions(app);
        MapWebhookActions(app);
        MapTeamActions(app);
        MapSubscriptionActions(app);
        MapDisputeActions(app);
        MapRiskActions(app);
        MapComplianceActions(app);
        MapCustomerActions(app);
        MapDownloadEndpoints(app);
    }

    private static void MapCustomerActions(WebApplication app)
    {
        app.MapPost("/musteriler/kaydet", async (HttpContext http, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Operations))
                return CustomerRedirect(null, hata: "Müşteri kaydı için en az 'operations' rolü gerekli.");

            var form = await http.Request.ReadFormAsync();
            string? Field(string key)
                => form.TryGetValue(key, out var v) && v.ToString() is { Length: > 0 } s ? s : null;

            try
            {
                var customer = await dispatcher.Send(new SaveCustomerCommand(
                    form["ref"].ToString(), Field("name"), Field("email"),
                    Field("phone"), Field("notes")));

                return CustomerRedirect(null, sonuc: $"'{customer.Ref}' kaydedildi.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return CustomerRedirect(null, hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/musteriler/{reference}/kvkk-sil", async (
            string reference, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Operations))
                return CustomerRedirect(reference, hata: "KVKK silme için en az 'operations' rolü gerekli.");

            try
            {
                await dispatcher.Send(new EraseCustomerCommand(reference));
                return CustomerRedirect(reference, sonuc:
                    "Kişisel veri silindi. Mali kayıtlar saklama yükümlülüğü gereği duruyor; "
                    + "açık ödeme talimatları iptal edildi.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return CustomerRedirect(reference, hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/musteriler/talimat/{id}/iptal", async (
            string id, HttpContext http, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Operations))
                return CustomerRedirect(null, hata: "Talimat iptali için en az 'operations' rolü gerekli.");

            try
            {
                var mandate = await dispatcher.Send(new RevokeMandateCommand(id, "Panelden iptal edildi."));
                return CustomerRedirect(mandate.CustomerRef, sonuc:
                    "Talimat iptal edildi — kayıt duruyor, iptalden önceki çekimlerin dayanağı korunur.");
            }
            catch (PoyraException ex)
            {
                return CustomerRedirect(null, hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();
    }


    private static void MapComplianceActions(WebApplication app)
    {
        app.MapPost("/saha/temsilci", async (HttpContext http, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Admin))
                return FieldRedirect(hata: "Temsilci tanımlamak için en az 'admin' rolü gerekli.");

            var form = await http.Request.ReadFormAsync();
            try
            {
                var agent = await dispatcher.Send(new CreateFieldAgentCommand(
                    form["code"].ToString(),
                    form["name"].ToString(),
                    form["region"].ToString() is { Length: > 0 } r ? r : null,
                    UserId: null));

                return FieldRedirect(sonuc: $"Temsilci eklendi: {agent.Code}.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return FieldRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/saha/temsilci/{id:guid}/cihaz-birak", async (
            Guid id, [FromForm] string reason, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Admin))
                return FieldRedirect(hata: "Cihaz bırakmak için en az 'admin' rolü gerekli.");

            try
            {
                var agent = await dispatcher.Send(new ReleaseDeviceCommand(id, reason));
                return FieldRedirect(sonuc:
                    $"{agent.Code}: cihaz kaydı bırakıldı. Sonraki senkron yeni cihazı bağlar.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return FieldRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/saha/temsilci/{id:guid}/kapat", async (
            Guid id, [FromForm] string reason, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Admin))
                return FieldRedirect(hata: "Temsilci kapatmak için en az 'admin' rolü gerekli.");

            try
            {
                var agent = await dispatcher.Send(new DisableAgentCommand(id, reason));
                return FieldRedirect(sonuc:
                    $"{agent.Code} kapatıldı. Geçmiş tahsilatları durur — kayıt silinmez.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return FieldRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/uyum/inceleme", async (HttpContext http, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Auditor))
                return ComplianceRedirect(hata: "Uyum kaydı için en az 'auditor' rolü gerekli.");

            var form = await http.Request.ReadFormAsync();
            try
            {
                var paymentIds = form["paymentIds"].ToString()
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                var report = await dispatcher.Send(new OpenSuspiciousReportCommand(
                    paymentIds,
                    form["rationale"].ToString(),
                    form["customerRef"].ToString() is { Length: > 0 } c ? c : null));

                return ComplianceRedirect(sonuc: $"İnceleme açıldı ({report.Id}).");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return ComplianceRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/uyum/inceleme/{id}/sonuclandir", async (
            string id, [FromForm] string status, [FromForm] string resolution,
            IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Auditor))
                return ComplianceRedirect(hata: "Uyum kaydı için en az 'auditor' rolü gerekli.");

            try
            {
                await dispatcher.Send(new ResolveSuspiciousReportCommand(id, status, resolution));
                return ComplianceRedirect(sonuc: "İnceleme sonuçlandırıldı — karar ve gerekçe deftere işlendi.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return ComplianceRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapGet("/uyum/disa-aktar", async (HttpContext http, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Auditor))
                return Results.Redirect("/uyum?hata="
                    + Uri.EscapeDataString("Dışa aktarma için en az 'auditor' rolü gerekli."));

            var query = http.Request.Query;
            DateOnly? Day(string key)
                => DateOnly.TryParse(query[key], out var day) ? day : null;

            var csv = await dispatcher.Ask(new ExportAuditQuery(new ListAuditQuery(
                query["actor"].ToString() is { Length: > 0 } actor ? actor : null,
                null,
                query["resourceType"].ToString() is { Length: > 0 } resource ? resource : null,
                null, Day("from"), Day("to"), ExportAuditHandler.MaxRows)));

            // UTF-8 BOM: Excel BOM'suz dosyada Türkçe karakterleri bozar
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
                .Concat(System.Text.Encoding.UTF8.GetBytes(csv)).ToArray();

            return Results.File(bytes, "text/csv; charset=utf-8", "denetim-defteri.csv");
        }).RequireAuthorization();
    }

    private static void MapRiskActions(WebApplication app)
    {
        app.MapPost("/risk/yayinla", async (HttpContext http, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Admin))
                return RiskRedirect(hata: "Risk kuralı yayınlamak için 'admin' rolü gerekli.");

            var form = await http.Request.ReadFormAsync();
            try
            {
                var published = await dispatcher.Send(
                    new PublishRiskRulesCommand(form["document"].ToString()));

                return RiskRedirect(sonuc:
                    $"Sürüm {published.Version} yayında. Önceki sürüm pasifleşti (silinmedi).");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return RiskRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/risk/dene", async (HttpContext http, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Admin))
                return RiskRedirect(hata: "Kural denemek için 'admin' rolü gerekli.");

            var form = await http.Request.ReadFormAsync();
            try
            {
                var result = await dispatcher.Send(new TestRiskRulesCommand(new TestRiskRulesRequest(
                    Document: form["document"].ToString(),
                    AmountMinor: UserInput.ToKurus(form["amountLira"]),
                    Flow: form["flow"].ToString() is { Length: > 0 } flow ? flow : "hosted",
                    Hour: UserInput.ToInt(form["hour"], 12),
                    Attempts1h: UserInput.ToInt(form["attempts1h"], 0),
                    Declines1h: UserInput.ToInt(form["declines1h"], 0))));

                var rule = result.RuleName is { Length: > 0 } name ? $" ('{name}')" : "";
                return RiskRedirect(deneme: $"{result.Outcome}{rule} — {result.Reason}");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return RiskRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/risk/kara-liste", async (HttpContext http, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Admin))
                return RiskRedirect(hata: "Kara liste yönetimi için 'admin' rolü gerekli.");

            var form = await http.Request.ReadFormAsync();
            try
            {
                var expiresAt = UserInput.ToDate(form["expiresOn"]) is { } day
                    ? new DateTimeOffset(day.ToDateTime(TimeOnly.MaxValue), TimeSpan.FromHours(3))
                    : (DateTimeOffset?)null;

                var entry = await dispatcher.Send(new AddBlocklistCommand(
                    form["kind"].ToString(), form["value"].ToString(),
                    form["reason"].ToString(), expiresAt));

                return RiskRedirect(sonuc: $"'{entry.Value}' kara listeye eklendi.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return RiskRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/risk/kara-liste/{id:guid}/kaldir", async (
            Guid id, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Admin))
                return RiskRedirect(hata: "Kara liste yönetimi için 'admin' rolü gerekli.");

            try
            {
                await dispatcher.Send(new RemoveBlocklistCommand(id));
                return RiskRedirect(sonuc: "Kara listeden çıkarıldı — kayıt silinmedi, kaldırıldı işaretlendi.");
            }
            catch (PoyraException ex)
            {
                return RiskRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();
    }

    private static void MapDisputeActions(WebApplication app)
    {
        app.MapPost("/itirazlar/{id}/kanit", async (
            string id, HttpContext http, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Operations))
                return DisputeRedirect(id, hata: "Kanıt eklemek için en az 'operations' rolü gerekli.");

            var form = await http.Request.ReadFormAsync();
            if (form.Files.GetFile("file") is not { Length: > 0 } file)
                return DisputeRedirect(id, hata: "Dosya seçilmedi.");

            using var memory = new MemoryStream();
            await file.CopyToAsync(memory);

            try
            {
                var kind = form["kind"].ToString() is { Length: > 0 } k ? k : "other";
                var evidence = await dispatcher.Send(new AddEvidenceCommand(
                    id, file.FileName, file.ContentType ?? "application/octet-stream",
                    memory.ToArray(), kind));

                return DisputeRedirect(id, sonuc: $"'{evidence.FileName}' eklendi.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return DisputeRedirect(id, hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/itirazlar/{id}/kanit/{evidenceId:guid}/iptal", async (
            string id, Guid evidenceId, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Operations))
                return DisputeRedirect(id, hata: "Kanıt iptali için en az 'operations' rolü gerekli.");

            try
            {
                await dispatcher.Send(new RevokeEvidenceCommand(id, evidenceId));
                return DisputeRedirect(id, sonuc: "Belge iptal edildi — listede kalır, bankaya gitmez.");
            }
            catch (PoyraException ex)
            {
                return DisputeRedirect(id, hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/itirazlar/{id}/gonder", async (
            string id, [FromForm] string? summary, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Operations))
                return DisputeRedirect(id, hata: "Savunma göndermek için en az 'operations' rolü gerekli.");

            try
            {
                await dispatcher.Send(new SubmitEvidenceCommand(id, summary));
                return DisputeRedirect(id, sonuc:
                    "Savunma hazırlandı ve dosya 'incelemede'ye alındı. Bankanın kararını "
                    + "aldığınızda buradan işleyin.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return DisputeRedirect(id, hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/itirazlar/{id}/vazgec", async (
            string id, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Operations))
                return DisputeRedirect(id, hata: "Bu işlem için en az 'operations' rolü gerekli.");

            try
            {
                await dispatcher.Send(new AcceptDisputeCommand(id));
                return DisputeRedirect(id, sonuc: "Savunmadan vazgeçildi — tutar müşteride kalır.");
            }
            catch (PoyraException ex)
            {
                return DisputeRedirect(id, hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/itirazlar/{id}/sonuc", async (
            string id, [FromForm] string outcome, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Operations))
                return DisputeRedirect(id, hata: "Bu işlem için en az 'operations' rolü gerekli.");

            try
            {
                var dispute = await dispatcher.Send(new CloseDisputeCommand(id, outcome));
                return DisputeRedirect(id, sonuc: dispute.Status == "won"
                    ? "Kazanıldı — tutar hesabınıza geri döner."
                    : "Kaybedildi olarak işlendi.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return DisputeRedirect(id, hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapGet("/itirazlar/{id}/kanit/{evidenceId:guid}", async (
            string id, Guid evidenceId, IDispatcher dispatcher) =>
        {
            try
            {
                var (fileName, contentType, content) =
                    await dispatcher.Ask(new DownloadEvidenceQuery(id, evidenceId));
                return Results.File(content, contentType, fileName);
            }
            catch (PoyraException ex)
            {
                return Results.Redirect($"/itirazlar/{id}?hata={Uri.EscapeDataString(PanelFlash.Protect(ex.Message))}");
            }
        }).RequireAuthorization();
    }

    private static void MapSubscriptionActions(WebApplication app)
    {
        app.MapPost("/abonelikler/plan", async (HttpContext http, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Admin))
                return SubscriptionRedirect(null, hata: "Plan tanımlamak için 'admin' rolü gerekli.");

            var form = await http.Request.ReadFormAsync();
            try
            {
                var plan = await dispatcher.Send(new CreatePlanCommand(
                    form["name"].ToString().Trim(),
                    UserInput.ToKurus(form["amountLira"]),
                    "TRY",
                    form["interval"].ToString() is { Length: > 0 } interval ? interval : "month",
                    UserInput.ToInt(form["intervalCount"], 1),
                    UserInput.ToInt(form["trialDays"], 0)));

                return SubscriptionRedirect(null, sonuc: $"'{plan.Name}' planı hazır ({plan.Id}).");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return SubscriptionRedirect(null, hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/abonelikler/plan/{id}/durum", async (
            string id, [FromForm] bool active, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Admin))
                return SubscriptionRedirect(null, hata: "Plan kapatmak için 'admin' rolü gerekli.");

            try
            {
                var plan = await dispatcher.Send(new SetPlanStatusCommand(id, active));
                return SubscriptionRedirect(null, sonuc: plan.Active
                    ? $"'{plan.Name}' yeniden açıldı."
                    : $"'{plan.Name}' kapatıldı — yeni abonelik alınmaz, mevcut aboneler etkilenmez.");
            }
            catch (PoyraException ex)
            {
                return SubscriptionRedirect(null, hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/abonelikler/{id}/iptal", async (
            string id, [FromForm] bool atPeriodEnd, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Operations))
                return SubscriptionRedirect(id, hata: "İptal için en az 'operations' rolü gerekli.");

            try
            {
                await dispatcher.Send(new CancelSubscriptionCommand(id, atPeriodEnd));
                return SubscriptionRedirect(id, sonuc: atPeriodEnd
                    ? "Dönem sonunda kapanacak — müşteri ödediği süreyi kullanmaya devam eder."
                    : "Abonelik hemen iptal edildi. Kalan gün için iade GEÇMİŞ ÖDEMEDEN yapılır.");
            }
            catch (PoyraException ex)
            {
                return SubscriptionRedirect(id, hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/abonelikler/{id}/kart", async (
            string id, [FromForm] string cardToken, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Operations))
                return SubscriptionRedirect(id, hata: "Kart değişimi için en az 'operations' rolü gerekli.");

            try
            {
                await dispatcher.Send(new UpdateSubscriptionCardCommand(id, cardToken.Trim()));
                return SubscriptionRedirect(id, sonuc:
                    "Kart güncellendi — bekleyen fatura varsa hemen yeniden denenir.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return SubscriptionRedirect(id, hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();
    }

    private static void MapWebhookActions(WebApplication app)
    {
        app.MapPost("/webhooklar/ekle", async (
            HttpContext http, IDispatcher dispatcher, UserContext user, OneTimeSecretStash stash) =>
        {
            if (!user.HasRank(TenantRole.Developer))
                return WebhookRedirect(hata: "Webhook yönetimi için en az 'developer' rolü gerekli.");

            var form = await http.Request.ReadFormAsync();
            try
            {
                var eventTypes = form["eventTypes"].Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v!.Trim()).ToArray();

                if (eventTypes.Length == 0)
                    return WebhookRedirect(hata:
                        "En az bir olay seçin — hiçbiri seçilmezse bu adrese bildirim gitmez.");

                var endpoint = await dispatcher.Send(new CreateWebhookEndpointCommand(
                    form["url"].ToString().Trim(), eventTypes));

                return endpoint.Secret is { Length: > 0 } secret
                    ? SecretRedirect(http, stash, "/webhooklar", secret,
                        sonuc: $"'{endpoint.Url}' eklendi. İmza sırrını şimdi kaydedin.")
                    : WebhookRedirect(sonuc: $"'{endpoint.Url}' eklendi.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return WebhookRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/webhooklar/{id:guid}/sir", async (
            Guid id, HttpContext http, IDispatcher dispatcher, UserContext user,
            OneTimeSecretStash stash) =>
        {
            if (!user.HasRank(TenantRole.Developer))
                return WebhookRedirect(hata: "Webhook yönetimi için en az 'developer' rolü gerekli.");

            try
            {
                var endpoint = await dispatcher.Send(new RotateWebhookSecretCommand(id));
                return endpoint.Secret is { Length: > 0 } secret
                    ? SecretRedirect(http, stash, "/webhooklar", secret,
                        sonuc: "Sır yenilendi — ESKİSİ ANINDA GEÇERSİZ. Sunucunuzu güncelleyene kadar "
                               + "gelen bildirimlerin imzası doğrulanmaz.")
                    : WebhookRedirect(sonuc: "Sır yenilendi.");
            }
            catch (PoyraException ex)
            {
                return WebhookRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/webhooklar/{id:guid}/durum", async (
            Guid id, [FromForm] bool active, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Developer))
                return WebhookRedirect(hata: "Webhook yönetimi için en az 'developer' rolü gerekli.");

            try
            {
                var endpoint = await dispatcher.Send(new SetWebhookEndpointStatusCommand(id, active));
                return WebhookRedirect(sonuc: endpoint.Active
                    ? $"'{endpoint.Url}' açıldı."
                    : $"'{endpoint.Url}' kapatıldı — yeni olaylar bu adrese gönderilmeyecek.");
            }
            catch (PoyraException ex)
            {
                return WebhookRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/webhooklar/teslimat/{id:guid}/yeniden", async (
            Guid id, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Developer))
                return WebhookRedirect(hata: "Webhook yönetimi için en az 'developer' rolü gerekli.");

            try
            {
                var replay = await dispatcher.Send(new ReplayWebhookDeliveryCommand(id));
                return WebhookRedirect(sonuc:
                    $"'{replay.EventType}' yeniden kuyruğa alındı ({replay.Id}).");
            }
            catch (PoyraException ex)
            {
                return WebhookRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();
    }

    private static void MapTeamActions(WebApplication app)
    {
        app.MapPost("/ekip/ekle", async (HttpContext http, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Admin))
                return TeamRedirect(hata: "Kullanıcı eklemek için 'admin' rolü gerekli.");

            var form = await http.Request.ReadFormAsync();
            try
            {
                var created = await dispatcher.Send(new CreateUserCommand(
                    form["email"].ToString().Trim(),
                    form["password"].ToString(),
                    form["displayName"].ToString().Trim(),
                    form["role"].ToString()));

                return TeamRedirect(sonuc:
                    $"{created.Email} '{created.Role}' rolüyle eklendi. Geçici parolayı kendisine "
                    + "güvenli bir kanaldan iletin; ilk girişte değiştirmesini isteyin.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return TeamRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/ekip/{id:guid}/rol", async (
            Guid id, [FromForm] string role, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Admin))
                return TeamRedirect(hata: "Rol değiştirmek için 'admin' rolü gerekli.");

            try
            {
                var updated = await dispatcher.Send(new ChangeUserRoleCommand(id, role));
                return TeamRedirect(sonuc: $"{updated.Email} artık '{updated.Role}'.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return TeamRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/ekip/{id:guid}/kaldir", async (
            Guid id, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Admin))
                return TeamRedirect(hata: "Erişim kaldırmak için 'admin' rolü gerekli.");

            try
            {
                await dispatcher.Send(new RemoveUserCommand(id));
                return TeamRedirect(sonuc:
                    "Erişim kaldırıldı ve açık oturumları kapatıldı. Geçmiş işlemlerdeki imzası duruyor.");
            }
            catch (PoyraException ex)
            {
                return TeamRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/ekip/anahtar", async (
            HttpContext http, IDispatcher dispatcher, UserContext user, OneTimeSecretStash stash) =>
        {
            if (!user.HasRank(TenantRole.Owner))
                return TeamRedirect(hata: "API anahtarı üretmek için 'owner' rolü gerekli.");

            var form = await http.Request.ReadFormAsync();
            try
            {
                var key = await dispatcher.Send(new CreateApiKeyCommand(
                    form["name"].ToString().Trim(), form["live"].Count > 0));

                return SecretRedirect(http, stash, "/ekip", key.Key,
                    sonuc: $"'{key.Name}' üretildi. Anahtarı ŞİMDİ kaydedin — bir daha gösterilmez.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return TeamRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/ekip/anahtar/{id:guid}/iptal", async (
            Guid id, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Owner))
                return TeamRedirect(hata: "API anahtarı iptal etmek için 'owner' rolü gerekli.");

            try
            {
                var key = await dispatcher.Send(new RevokeApiKeyCommand(id));
                return TeamRedirect(sonuc:
                    $"'{key.Name}' iptal edildi — bu anahtarla gelen çağrılar artık 401 alır.");
            }
            catch (PoyraException ex)
            {
                return TeamRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();
    }

    private static void MapConnectorActions(WebApplication app)
    {
        app.MapPost("/baglantilar/ekle", async (HttpContext http, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Admin))
                return AccountRedirect(hata: "POS hesabı eklemek için 'admin' rolü gerekli.");

            var form = await http.Request.ReadFormAsync();
            try
            {
                var account = await dispatcher.Send(new AddConnectorAccountCommand(
                    form["connectorKey"].ToString(),
                    form["label"].ToString(),
                    Credentials(form),
                    UserInput.ToInt(form["priority"], 100),
                    form["testMode"].Count > 0));

                return AccountRedirect(sonuc:
                    $"'{account.Label}' eklendi. Kimlikleri sınamak için 'Test et' düğmesini kullanın.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return AccountRedirect(hata: ex.Message, connectorKey: form["connectorKey"].ToString());
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/baglantilar/{id:guid}/anahtar", async (
            Guid id, HttpContext http, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Admin))
                return AccountRedirect(hata: "Anahtar yenilemek için 'admin' rolü gerekli.");

            var form = await http.Request.ReadFormAsync();
            try
            {
                var account = await dispatcher.Send(new RotateCredentialsCommand(id, Credentials(form)));
                return AccountRedirect(sonuc: account.Health == "healthy"
                    ? $"'{account.Label}' anahtarları yenilendi ve bağlantı doğrulandı."
                    : $"'{account.Label}' anahtarları yenilendi — ancak bağlantı sınaması başarısız ({account.Health}).");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return AccountRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/baglantilar/{id:guid}/ayarlar", async (
            Guid id, HttpContext http, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Admin))
                return AccountRedirect(hata: "Ayarları değiştirmek için 'admin' rolü gerekli.");

            var form = await http.Request.ReadFormAsync();
            try
            {
                await dispatcher.Send(new UpdateAccountSettingsCommand(
                    id,
                    form["label"].ToString() is { Length: > 0 } label ? label : null,
                    form.ContainsKey("priority") ? UserInput.ToInt(form["priority"], 100) : null,
                    form.ContainsKey("testMode") || form.ContainsKey("priority") ? form["testMode"].Count > 0 : null));

                return AccountRedirect(sonuc: "Ayarlar kaydedildi.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return AccountRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/baglantilar/{id:guid}/test", async (Guid id, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Admin))
                return AccountRedirect(hata: "Yoklama için 'admin' rolü gerekli.");

            try
            {
                var probe = await dispatcher.Send(new ProbeAccountCommand(id));
                if (!probe.Supported)
                    return AccountRedirect(sonuc: $"'{probe.Label}': {probe.Detail}");

                return probe.Healthy
                    ? AccountRedirect(sonuc: $"'{probe.Label}' bankaya ulaşıyor ✓ {probe.Detail}")
                    : AccountRedirect(hata: $"'{probe.Label}' bankaya ulaşamıyor: {probe.Detail}");
            }
            catch (PoyraException ex)
            {
                return AccountRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/baglantilar/{id:guid}/durum", async (
            Guid id, [FromForm] string status, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Admin))
                return AccountRedirect(hata: "Durumu değiştirmek için 'admin' rolü gerekli.");

            try
            {
                var account = await dispatcher.Send(new SetConnectorAccountStatusCommand(id, status, null));
                return AccountRedirect(sonuc: account.Status == "active"
                    ? $"'{account.Label}' açıldı."
                    : $"'{account.Label}' devre dışı bırakıldı — rota artık bu POS'a yollamaz.");
            }
            catch (PoyraException ex)
            {
                return AccountRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();
    }

    private static void MapSettingsActions(WebApplication app)
    {
        app.MapPost("/ayarlar/marka", async (HttpContext http, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Admin))
                return SettingsRedirect(hata: "Marka ayarı için 'admin' rolü gerekli.");

            var form = await http.Request.ReadFormAsync();
            string? Field(string key) => form.TryGetValue(key, out var v) && v.ToString() is { Length: > 0 } s
                ? s
                : null;

            string? logoBase64 = null;
            string? logoType = null;
            if (form.Files.GetFile("logo") is { Length: > 0 } file)
            {
                if (file.Length > SaveBrandingValidator.MaxLogoBytes)
                    return SettingsRedirect(hata:
                        $"Logo en çok {SaveBrandingValidator.MaxLogoBytes / 1024} KB olabilir.");

                using var memory = new MemoryStream();
                await file.CopyToAsync(memory);
                logoBase64 = Convert.ToBase64String(memory.ToArray());
                logoType = file.ContentType;
            }

            try
            {
                await dispatcher.Send(new SaveBrandingCommand(
                    Field("displayName"), Field("primaryColor"), Field("supportEmail"),
                    Field("supportPhone"), Field("checkoutDomain"), logoBase64, logoType));

                return SettingsRedirect(sonuc: "Marka kaydedildi — ödeme sayfası güncellendi.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return SettingsRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/ayarlar/marka/logo-sil", async (IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Admin))
                return SettingsRedirect(hata: "Marka ayarı için 'admin' rolü gerekli.");

            await dispatcher.Send(new SaveBrandingCommand(null, null, null, null, null, "-", null));
            return SettingsRedirect(sonuc: "Logo kaldırıldı.");
        }).RequireAuthorization().WithPanelAntiforgery();

        app.MapPost("/ayarlar/erp", async (HttpContext http, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Finance))
                return SettingsRedirect(hata: "ERP ayarı için en az 'finance' rolü gerekli.");

            var form = await http.Request.ReadFormAsync();
            string Field(string key, string fallback)
                => form.TryGetValue(key, out var v) && v.ToString() is { Length: > 0 } s ? s : fallback;

            try
            {
                await dispatcher.Send(new SaveErpSettingsCommand(
                    Field("format", "poyra_csv"),
                    Field("posReceivableAccount", "108.01"),
                    Field("bankAccount", "102.01"),
                    Field("commissionExpenseAccount", "653.01"),
                    Field("documentPrefix", "POS")));

                return SettingsRedirect(sonuc: "ERP ayarı kaydedildi.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return SettingsRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();
    }

    private static void MapDownloadEndpoints(WebApplication app)
    {
        app.MapGet("/baglantilar-odeme/{id}/qr.svg", async (string id, IDispatcher dispatcher) =>
        {
            var svg = await dispatcher.Ask(new LinkQrQuery(id));
            return Results.Content(svg, "image/svg+xml; charset=utf-8");
        }).RequireAuthorization();

        app.MapGet("/baglantilar-odeme/{id}/karekod.svg", async (string id, IDispatcher dispatcher) =>
        {
            try
            {
                var payload = await dispatcher.Ask(new LinkKarekodQuery(id));
                return Results.Content(
                    Poyra.SharedKernel.Domain.QrCode.ToSvg(payload), "image/svg+xml; charset=utf-8");
            }
            catch (PoyraException ex)
            {
                return Results.Redirect($"/baglantilar-odeme?hata={Uri.EscapeDataString(PanelFlash.Protect(ex.Message))}");
            }
        }).RequireAuthorization();

        app.MapGet("/mutabakat/{id:guid}/erp", async (
            Guid id, HttpContext http, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Finance))
                return Results.Redirect($"/mutabakat/{id}?hata="
                    + Uri.EscapeDataString("ERP fişi için en az 'finance' rolü gerekli."));

            try
            {
                var format = http.Request.Query["format"].ToString();
                var export = await dispatcher.Ask(new ExportStatementQuery(
                    id, format is { Length: > 0 } ? format : null));

                var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
                    .Concat(System.Text.Encoding.UTF8.GetBytes(export.Content)).ToArray();

                return Results.File(bytes, export.ContentType, export.FileName);
            }
            catch (PoyraException ex)
            {
                return Results.Redirect($"/mutabakat/{id}?hata={Uri.EscapeDataString(PanelFlash.Protect(ex.Message))}");
            }
        }).RequireAuthorization();


        app.MapPost("/mutabakat/ekstre-yukle", async (
            HttpContext http, IDispatcher dispatcher, UserContext user,
            IEnumerable<Poyra.Modules.Recon.Infrastructure.IStatementParser> parsers) =>
        {
            if (!user.HasRank(TenantRole.Finance))
                return ReconRedirect(hata: "Ekstre yüklemek için en az 'finance' rolü gerekli.");

            var form = await http.Request.ReadFormAsync();
            try
            {
                var file = form.Files["file"];
                if (file is null || file.Length == 0)
                    return ReconRedirect(hata: "Ekstre dosyası seçilmedi ya da boş.");

                if (!Guid.TryParse(form["connectorAccountId"], out var accountId))
                    return ReconRedirect(hata: "POS hesabı seçin.");

                if (!DateOnly.TryParse(form["statementDate"], out var statementDate))
                    return ReconRedirect(hata: "Ekstre günü geçersiz.");

                var formatKey = form["format"].ToString() is { Length: > 0 } f
                    ? f
                    : Poyra.Modules.Recon.Infrastructure.PoyraCsvStatementParser.FormatKey;
                var parser = parsers.FirstOrDefault(p => p.Format == formatKey);
                if (parser is null)
                    return ReconRedirect(hata: $"Bilinmeyen ekstre formatı: {formatKey}");

                Poyra.Modules.Recon.Infrastructure.StatementParseResult parsed;
                using (var reader = new StreamReader(file.OpenReadStream()))
                {
                    parsed = parser.Parse(reader);
                }

                if (parsed.Errors.Count > 0)
                    return ReconRedirect(hata:
                        $"Ekstre ayrıştırılamadı ({parsed.Errors.Count} hata): "
                        + string.Join(" | ", parsed.Errors.Take(3)));

                var lines = parsed.Lines
                    .Select(l => new StatementLineInput(
                        l.OrderId, l.GrossMinor, l.CommissionMinor, l.NetMinor, l.ValueDate, l.LineType))
                    .ToList();

                var summary = await dispatcher.Send(new ImportStatementCommand(
                    accountId, statementDate, lines));
                return ReconRedirect(sonuc:
                    $"Ekstre yüklendi: {summary.LineCount} satır, {summary.MatchedCount} eşleşme — "
                    + "eşleştirme sürüyorsa sonuç birkaç dakika içinde kesinleşir.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return ReconRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();
    }

    private static IResult ReconRedirect(string? sonuc = null, string? hata = null)
    {
        var message = hata ?? sonuc ?? "";
        var query = hata is null ? "sonuc" : "hata";
        return Results.Redirect($"/mutabakat?{query}={Uri.EscapeDataString(PanelFlash.Protect(message))}");
    }

    private static void MapKarekodActions(WebApplication app)
        => app.MapPost("/ayarlar/karekod", async (
            HttpContext http, IDispatcher dispatcher, UserContext user) =>
        {
            if (!user.HasRank(TenantRole.Admin))
                return SettingsRedirect(hata: "Karekod ayarı için 'admin' rolü gerekli.");

            var form = await http.Request.ReadFormAsync();
            string? Field(string key) => form.TryGetValue(key, out var v) && v.ToString() is { Length: > 0 } s
                ? s
                : null;

            try
            {
                var saved = await dispatcher.Send(new SaveKarekodSettingsCommand(
                    Field("schemeGuid"), Field("merchantNo"), Field("categoryCode"),
                    Field("merchantName"), Field("merchantCity")));

                return SettingsRedirect(sonuc: saved.Configured
                    ? "Karekod ayarı kaydedildi. Canlıya çıkmadan bankanızın test setiyle doğrulayın."
                    : "Kaydedildi — ancak şema tanımlayıcısı ve üye işyeri no girilmeden karekod üretilemez.");
            }
            catch (Exception ex) when (ex is PoyraException or FluentValidation.ValidationException)
            {
                return SettingsRedirect(hata: ex.Message);
            }
        }).RequireAuthorization().WithPanelAntiforgery();

    private static IResult SettingsRedirect(string? sonuc = null, string? hata = null)
    {
        var message = hata ?? sonuc ?? "";
        var query = hata is null ? "sonuc" : "hata";
        return Results.Redirect($"/ayarlar?{query}={Uri.EscapeDataString(PanelFlash.Protect(message))}");
    }

    private static Dictionary<string, string> Credentials(IFormCollection form)
        => form.Where(f => f.Key.StartsWith("cred_", StringComparison.Ordinal))
            .Where(f => !string.IsNullOrWhiteSpace(f.Value.ToString()))
            .ToDictionary(f => f.Key["cred_".Length..], f => f.Value.ToString());

    private static IResult AccountRedirect(
        string? sonuc = null, string? hata = null, string? connectorKey = null)
    {
        var message = hata ?? sonuc ?? "";
        var query = hata is null ? "sonuc" : "hata";
        var keep = connectorKey is { Length: > 0 } ? $"&konnektor={Uri.EscapeDataString(connectorKey)}" : "";
        return Results.Redirect($"/baglantilar?{query}={Uri.EscapeDataString(PanelFlash.Protect(message))}{keep}");
    }

    private static IResult SubscriptionRedirect(
        string? subscriptionId, string? sonuc = null, string? hata = null)
    {
        var message = hata ?? sonuc ?? "";
        var query = hata is null ? "sonuc" : "hata";
        var path = subscriptionId is { Length: > 0 } ? $"/abonelikler/{subscriptionId}" : "/abonelikler";
        return Results.Redirect($"{path}?{query}={Uri.EscapeDataString(PanelFlash.Protect(message))}");
    }

    private static IResult CustomerRedirect(string? reference, string? sonuc = null, string? hata = null)
    {
        var message = hata ?? sonuc ?? "";
        var query = hata is null ? "sonuc" : "hata";
        var path = reference is { Length: > 0 } ? $"/musteriler/{Uri.EscapeDataString(reference)}" : "/musteriler";
        return Results.Redirect($"{path}?{query}={Uri.EscapeDataString(PanelFlash.Protect(message))}");
    }

    private static IResult FieldRedirect(string? sonuc = null, string? hata = null)
    {
        var message = hata ?? sonuc ?? "";
        var query = hata is null ? "sonuc" : "hata";
        return Results.Redirect($"/saha?{query}={Uri.EscapeDataString(PanelFlash.Protect(message))}");
    }

    private static IResult ComplianceRedirect(string? sonuc = null, string? hata = null)
    {
        var message = hata ?? sonuc ?? "";
        var query = hata is null ? "sonuc" : "hata";
        return Results.Redirect($"/uyum?{query}={Uri.EscapeDataString(PanelFlash.Protect(message))}");
    }

    private static IResult RiskRedirect(string? sonuc = null, string? hata = null, string? deneme = null)
    {
        if (deneme is { Length: > 0 })
            return Results.Redirect($"/risk?deneme={Uri.EscapeDataString(PanelFlash.Protect(deneme))}");

        var message = hata ?? sonuc ?? "";
        var query = hata is null ? "sonuc" : "hata";
        return Results.Redirect($"/risk?{query}={Uri.EscapeDataString(PanelFlash.Protect(message))}");
    }

    private static IResult DisputeRedirect(string disputeId, string? sonuc = null, string? hata = null)
    {
        var message = hata ?? sonuc ?? "";
        var query = hata is null ? "sonuc" : "hata";
        return Results.Redirect($"/itirazlar/{disputeId}?{query}={Uri.EscapeDataString(PanelFlash.Protect(message))}");
    }

    private static IResult WebhookRedirect(string? sonuc = null, string? hata = null)
    {
        var message = hata ?? sonuc ?? "";
        var query = hata is null ? "sonuc" : "hata";
        return Results.Redirect($"/webhooklar?{query}={Uri.EscapeDataString(PanelFlash.Protect(message))}");
    }

    private static IResult TeamRedirect(string? sonuc = null, string? hata = null)
    {
        var message = hata ?? sonuc ?? "";
        var query = hata is null ? "sonuc" : "hata";
        return Results.Redirect($"/ekip?{query}={Uri.EscapeDataString(PanelFlash.Protect(message))}");
    }

    private static IResult SecretRedirect(
        HttpContext http, OneTimeSecretStash stash, string path, string secret, string sonuc)
    {
        http.Response.Cookies.Append(OneTimeSecretStash.CookieName, stash.Put(secret),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                MaxAge = OneTimeSecretStash.Ttl,
                Path = path,
            });
        return Results.Redirect($"{path}?sonuc={Uri.EscapeDataString(PanelFlash.Protect(sonuc))}");
    }

    private static IResult LinkRedirect(string? sonuc = null, string? hata = null)
    {
        var message = hata ?? sonuc ?? "";
        var query = hata is null ? "sonuc" : "hata";
        return Results.Redirect($"/baglantilar-odeme?{query}={Uri.EscapeDataString(PanelFlash.Protect(message))}");
    }

    private static IResult Redirect(string paymentId, string? sonuc = null, string? hata = null)
    {
        var message = hata ?? sonuc ?? "";
        var query = hata is null ? "sonuc" : "hata";
        return Results.Redirect($"/odemeler/{paymentId}?{query}={Uri.EscapeDataString(PanelFlash.Protect(message))}");
    }
}
