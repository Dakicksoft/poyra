using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Api;
using Poyra.Modules.Disputes.Domain;
using Poyra.SharedKernel.Tenancy;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// M10 İtirazlar. Bu modülün varlık sebebi SÜREDİR: kaçırılan kanıt süresi —
/// savunma ne kadar güçlü olursa olsun — otomatik kayıptır. Testler bu yüzden
/// "kayıt tutuluyor mu"dan çok "süre doğru mu, kapı kapanıyor mu" sorar.
/// </summary>
[Collection("postgres")]
public sealed class DisputeFlowTests : IDisposable
{
    private const string AdminKey = "test-admin-key";

    private readonly PostgresFixture _fixture;
    private readonly WebApplicationFactory<ApiEntryPoint> _factory;
    private readonly HttpClient _api;

    public DisputeFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _factory = new WebApplicationFactory<ApiEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Poyra", fixture.AppCs);
            builder.UseSetting("ConnectionStrings:PoyraMigrations", fixture.OwnerCs);
            builder.UseSetting("Database:AutoMigrate", "false");
            builder.UseSetting("Platform:AdminKey", AdminKey);
            builder.UseSetting("Poyra:PublicBaseUrl", "http://localhost");
            builder.UseSetting("Poyra:CredentialKey", Convert.ToBase64String(new byte[32]));
            builder.UseSetting("Poyra:JwtKey", Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()));
            builder.UseSetting("Poyra:VaultKey", Convert.ToBase64String(Enumerable.Repeat((byte)5, 32).ToArray()));
        });
        _api = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    private sealed record TenantCreated(Guid TenantId, string Slug, string ApiKey);
    private sealed record NextAction(string Url, Dictionary<string, string> Fields);
    private sealed record PaymentDto(string Id, string Status, NextAction? NextAction);
    private sealed record DisputeDto(
        string Id, string PaymentId, long AmountMinor, string Currency, string Reason,
        string? RawReasonCode, string Stage, string Status, DateTimeOffset OpenedAt,
        DateTimeOffset EvidenceDueAt, double RemainingHours, bool Overdue,
        string? ConnectorDisputeId, string? EvidenceSummary,
        DateTimeOffset? SubmittedAt, DateTimeOffset? ClosedAt, int EvidenceCount);
    private sealed record EvidenceDto(
        Guid Id, string FileName, string ContentType, string Kind,
        int SizeBytes, bool Revoked, DateTimeOffset CreatedAt);
    private sealed record EventDto(string EventType, string Actor, string Payload, DateTimeOffset CreatedAt);
    private sealed record DetailDto(DisputeDto Dispute, List<EvidenceDto> Evidence, List<EventDto> Timeline);
    private sealed record DeliveryDto(Guid Id, string EventType, string Status);

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body,
        params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        foreach (var (name, value) in headers)
            request.Headers.Add(name, value);
        return await _api.SendAsync(request);
    }

    private async Task<T> SendOk<T>(HttpMethod method, string path, object? body,
        params (string Name, string Value)[] headers)
    {
        var response = await Send(method, path, body, headers);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<TenantCreated> SeedTenantAsync()
    {
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants",
            new { name = "İtiraz A.Ş.", slug = "itr-" + Guid.NewGuid().ToString("N")[..10] },
            ("X-Platform-Key", AdminKey));

        await SendOk<object>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Mock POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        return tenant;
    }

    /// <summary>Gerçek 3DS akışı koşturur — itiraz ancak TAHSİL EDİLMİŞ ödemeye açılabilir.</summary>
    private async Task<string> PaidPaymentAsync(string apiKey, long amountMinor = 50_000)
    {
        var payment = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor, currency = "TRY", confirm = true }, ("X-Api-Key", apiKey));

        (await _api.PostAsync(payment.NextAction!.Url,
            new FormUrlEncodedContent(payment.NextAction.Fields))).StatusCode.ShouldBe(HttpStatusCode.OK);

        return payment.Id;
    }

    private async Task<DisputeDto> OpenDisputeAsync(
        string apiKey, string paymentId, long amountMinor = 50_000,
        string reason = "poyra.dispute.fraud", DateTimeOffset? dueAt = null, string? bankFileNo = null)
        => await SendOk<DisputeDto>(HttpMethod.Post, "/v1/disputes", new
        {
            paymentId,
            amountMinor,
            reason,
            evidenceDueAt = dueAt,
            connectorDisputeId = bankFileNo,
        }, ("X-Api-Key", apiKey));

    private static MultipartFormDataContent EvidenceForm(
        string fileName, string contentType, byte[] bytes, string kind = "delivery_proof")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        form.Add(new StringContent(kind), "kind");
        return form;
    }

    private async Task<HttpResponseMessage> UploadAsync(
        string apiKey, string disputeId, MultipartFormDataContent form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/disputes/{disputeId}/evidence")
        {
            Content = form,
        };
        request.Headers.Add("X-Api-Key", apiKey);
        return await _api.SendAsync(request);
    }

    // ---- Açılış -----------------------------------------------------------------

    [Fact]
    public async Task Itiraz_acilabilmeli_ve_sure_is_gunuyle_hesaplanmali()
    {
        var tenant = await SeedTenantAsync();
        var paymentId = await PaidPaymentAsync(tenant.ApiKey);

        var dispute = await OpenDisputeAsync(tenant.ApiKey, paymentId);

        dispute.Id.ShouldStartWith("dsp_");
        dispute.PaymentId.ShouldBe(paymentId);
        dispute.Status.ShouldBe("open");
        dispute.Stage.ShouldBe("chargeback");
        dispute.Overdue.ShouldBeFalse();

        // Süre TAKVİM günüyle değil İŞ GÜNÜYLE sayılır: 9 iş günü hafta sonlarıyla
        // birlikte en az 11 takvim günü eder
        var calendarDays = (dispute.EvidenceDueAt - dispute.OpenedAt).TotalDays;
        calendarDays.ShouldBeGreaterThan(10);
        dispute.RemainingHours.ShouldBeGreaterThan(240);
    }

    [Fact]
    public async Task Bankanin_bildirdigi_tarih_hesaplanani_EZMELI()
    {
        var tenant = await SeedTenantAsync();
        var paymentId = await PaidPaymentAsync(tenant.ApiKey);

        // Banka bildirimi 4 gün diyorsa hesabımız 9 iş günü dese de banka kazanır
        var bankDeadline = DateTimeOffset.UtcNow.AddDays(4);
        var dispute = await OpenDisputeAsync(tenant.ApiKey, paymentId, dueAt: bankDeadline);

        (dispute.EvidenceDueAt - bankDeadline).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Tahsil_edilmemis_odemeye_itiraz_acilamamali()
    {
        var tenant = await SeedTenantAsync();

        // 3DS tamamlanmadı: para hiç çekilmedi, itiraz edilecek bir şey yok
        var intent = await SendOk<PaymentDto>(HttpMethod.Post, "/v1/payments",
            new { amountMinor = 20_000, currency = "TRY" }, ("X-Api-Key", tenant.ApiKey));

        var response = await Send(HttpMethod.Post, "/v1/disputes",
            new { paymentId = intent.Id, amountMinor = 20_000, reason = "poyra.dispute.fraud" },
            ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("dispute.payment_not_captured");
    }

    [Fact]
    public async Task Odemeyi_asan_itiraz_reddedilmeli()
    {
        var tenant = await SeedTenantAsync();
        var paymentId = await PaidPaymentAsync(tenant.ApiKey, 50_000);

        // Kısmi itiraz olağandır; ödemeyi AŞAN itiraz veri hatasıdır
        var partial = await OpenDisputeAsync(tenant.ApiKey, paymentId, amountMinor: 20_000);
        partial.AmountMinor.ShouldBe(20_000);

        var response = await Send(HttpMethod.Post, "/v1/disputes",
            new { paymentId, amountMinor = 90_000, reason = "poyra.dispute.fraud" },
            ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("dispute.amount_exceeds_payment");
    }

    [Fact]
    public async Task Ayni_banka_dosyasi_iki_kez_kaydedilememeli()
    {
        var tenant = await SeedTenantAsync();
        var paymentId = await PaidPaymentAsync(tenant.ApiKey);

        await OpenDisputeAsync(tenant.ApiKey, paymentId, bankFileNo: "CHB-2026-0042");

        // Banka bildirimi tekrar geldi: ikinci kayıt açmak süreyi SIFIRLAR
        var response = await Send(HttpMethod.Post, "/v1/disputes",
            new
            {
                paymentId,
                amountMinor = 50_000,
                reason = "poyra.dispute.fraud",
                connectorDisputeId = "CHB-2026-0042",
            }, ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("dispute.already_recorded");

        (await SendOk<List<DisputeDto>>(HttpMethod.Get, "/v1/disputes", null,
            ("X-Api-Key", tenant.ApiKey))).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Gecersiz_neden_reddedilmeli()
    {
        var tenant = await SeedTenantAsync();
        var paymentId = await PaidPaymentAsync(tenant.ApiKey);

        var response = await Send(HttpMethod.Post, "/v1/disputes",
            new { paymentId, amountMinor = 10_000, reason = "musteri-kizdi" },
            ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ---- Kanıt -------------------------------------------------------------------

    [Fact]
    public async Task Kanit_yuklenip_indirilebilmeli()
    {
        var tenant = await SeedTenantAsync();
        var dispute = await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey));

        var pdf = Encoding.UTF8.GetBytes("%PDF-1.4 kargo teslim tutanağı");
        var upload = await UploadAsync(tenant.ApiKey, dispute.Id,
            EvidenceForm("teslimat.pdf", "application/pdf", pdf));
        upload.StatusCode.ShouldBe(HttpStatusCode.OK, await upload.Content.ReadAsStringAsync());

        var evidence = (await upload.Content.ReadFromJsonAsync<EvidenceDto>())!;
        evidence.FileName.ShouldBe("teslimat.pdf");
        evidence.SizeBytes.ShouldBe(pdf.Length);

        // İçerik bytea'da bozulmadan durmalı — hakem aşamasına yıllar sonra gidebilir
        var download = await Send(HttpMethod.Get,
            $"/v1/disputes/{dispute.Id}/evidence/{evidence.Id}", null, ("X-Api-Key", tenant.ApiKey));
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await download.Content.ReadAsByteArrayAsync()).ShouldBe(pdf);
    }

    [Fact]
    public async Task Desteklenmeyen_dosya_turu_reddedilmeli()
    {
        var tenant = await SeedTenantAsync();
        var dispute = await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey));

        var response = await UploadAsync(tenant.ApiKey, dispute.Id,
            EvidenceForm("savunma.exe", "application/x-msdownload", [1, 2, 3]));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("dispute.evidence_type_not_allowed");
    }

    [Fact]
    public async Task Bos_dosya_reddedilmeli()
    {
        var tenant = await SeedTenantAsync();
        var dispute = await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey));

        var response = await UploadAsync(tenant.ApiKey, dispute.Id,
            EvidenceForm("bos.pdf", "application/pdf", []));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Iptal_edilen_kanit_SILINMEMELI()
    {
        var tenant = await SeedTenantAsync();
        var dispute = await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey));

        var upload = await UploadAsync(tenant.ApiKey, dispute.Id,
            EvidenceForm("yanlis.pdf", "application/pdf", Encoding.UTF8.GetBytes("%PDF yanlış belge")));
        var evidence = (await upload.Content.ReadFromJsonAsync<EvidenceDto>())!;

        await SendOk<object>(HttpMethod.Post,
            $"/v1/disputes/{dispute.Id}/evidence/{evidence.Id}/revoke", null, ("X-Api-Key", tenant.ApiKey));

        var detail = await SendOk<DetailDto>(HttpMethod.Get, $"/v1/disputes/{dispute.Id}", null,
            ("X-Api-Key", tenant.ApiKey));

        // Listede DURUYOR ama iptal işaretli — "o gün ne yüklenmişti" sorusu hakemde sorulur
        detail.Evidence.ShouldHaveSingleItem().Revoked.ShouldBeTrue();
        detail.Dispute.EvidenceCount.ShouldBe(0); // sayaç yalnız geçerli belgeleri sayar
        detail.Timeline.ShouldContain(e => e.EventType == "dispute.evidence_revoked");
    }

    // ---- Savunma ------------------------------------------------------------------

    [Fact]
    public async Task Savunma_gonderilebilmeli()
    {
        var tenant = await SeedTenantAsync();
        var dispute = await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey));

        await UploadAsync(tenant.ApiKey, dispute.Id,
            EvidenceForm("kargo.pdf", "application/pdf", Encoding.UTF8.GetBytes("%PDF kargo")));

        var submitted = await SendOk<DisputeDto>(HttpMethod.Post, $"/v1/disputes/{dispute.Id}/submit",
            new { summary = "Ürün 12.07.2026'da teslim edildi, imzalı tutanak ektedir." },
            ("X-Api-Key", tenant.ApiKey));

        submitted.Status.ShouldBe("under_review");
        submitted.SubmittedAt.ShouldNotBeNull();
        submitted.EvidenceSummary.ShouldContain("teslim edildi");
    }

    [Fact]
    public async Task Bos_savunma_gonderilememeli()
    {
        var tenant = await SeedTenantAsync();
        var dispute = await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey));

        // Belgesiz ve metinsiz gönderim, süreyi harcayıp dosyayı kaybetmektir
        var response = await Send(HttpMethod.Post, $"/v1/disputes/{dispute.Id}/submit",
            new { summary = (string?)null }, ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("dispute.evidence_empty_submission");
    }

    [Fact]
    public async Task Suresi_gecmis_itiraz_savunulamamali()
    {
        var tenant = await SeedTenantAsync();
        var paymentId = await PaidPaymentAsync(tenant.ApiKey);

        // Banka dosyayı dün kapatmış: süre geçti
        var dispute = await OpenDisputeAsync(tenant.ApiKey, paymentId,
            dueAt: DateTimeOffset.UtcNow.AddDays(-1));
        dispute.Overdue.ShouldBeTrue();
        dispute.RemainingHours.ShouldBeLessThan(0);

        await UploadAsync(tenant.ApiKey, dispute.Id,
            EvidenceForm("gec.pdf", "application/pdf", Encoding.UTF8.GetBytes("%PDF geç")));

        var response = await Send(HttpMethod.Post, $"/v1/disputes/{dispute.Id}/submit",
            new { summary = "Geç kaldık." }, ("X-Api-Key", tenant.ApiKey));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("dispute.evidence_window_closed");
    }

    [Fact]
    public async Task Iki_kez_savunma_gonderilememeli()
    {
        var tenant = await SeedTenantAsync();
        var dispute = await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey));

        await SendOk<DisputeDto>(HttpMethod.Post, $"/v1/disputes/{dispute.Id}/submit",
            new { summary = "İlk savunma." }, ("X-Api-Key", tenant.ApiKey));

        var again = await Send(HttpMethod.Post, $"/v1/disputes/{dispute.Id}/submit",
            new { summary = "İkinci savunma." }, ("X-Api-Key", tenant.ApiKey));

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await again.Content.ReadAsStringAsync()).ShouldContain("dispute.already_submitted");
    }

    // ---- Sonuç --------------------------------------------------------------------

    [Fact]
    public async Task Banka_karari_deftere_islenebilmeli()
    {
        var tenant = await SeedTenantAsync();
        var dispute = await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey));

        await SendOk<DisputeDto>(HttpMethod.Post, $"/v1/disputes/{dispute.Id}/submit",
            new { summary = "Savunma." }, ("X-Api-Key", tenant.ApiKey));

        var won = await SendOk<DisputeDto>(HttpMethod.Post, $"/v1/disputes/{dispute.Id}/close",
            new { outcome = "won" }, ("X-Api-Key", tenant.ApiKey));

        won.Status.ShouldBe("won");
        won.ClosedAt.ShouldNotBeNull();

        // Sonuçlanmış dosya yeniden kapatılamaz
        var again = await Send(HttpMethod.Post, $"/v1/disputes/{dispute.Id}/close",
            new { outcome = "lost" }, ("X-Api-Key", tenant.ApiKey));
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Gecersiz_sonuc_reddedilmeli()
    {
        var tenant = await SeedTenantAsync();
        var dispute = await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey));

        var response = await Send(HttpMethod.Post, $"/v1/disputes/{dispute.Id}/close",
            new { outcome = "open" }, ("X-Api-Key", tenant.ApiKey));

        // 'open' geçerli bir DURUM ama geçerli bir SONUÇ değil
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("dispute.invalid_outcome");
    }

    [Fact]
    public async Task Savunmadan_vazgecilebilmeli()
    {
        var tenant = await SeedTenantAsync();
        var dispute = await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey), 1_500);

        // 15 ₺'lik itirazda savunma maliyeti tutarı aşar — bu bir iş kararıdır
        var accepted = await SendOk<DisputeDto>(HttpMethod.Post, $"/v1/disputes/{dispute.Id}/accept",
            null, ("X-Api-Key", tenant.ApiKey));

        accepted.Status.ShouldBe("accepted");
        accepted.ClosedAt.ShouldNotBeNull();

        // Kapanmış dosyaya kanıt eklenemez
        var upload = await UploadAsync(tenant.ApiKey, dispute.Id,
            EvidenceForm("gec.pdf", "application/pdf", Encoding.UTF8.GetBytes("%PDF")));
        upload.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Ust_kademe_yeni_sure_baslatmali()
    {
        var tenant = await SeedTenantAsync();
        var dispute = await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey));

        await SendOk<DisputeDto>(HttpMethod.Post, $"/v1/disputes/{dispute.Id}/submit",
            new { summary = "İlk savunma." }, ("X-Api-Key", tenant.ApiKey));

        var escalated = await SendOk<DisputeDto>(HttpMethod.Post, $"/v1/disputes/{dispute.Id}/escalate",
            new { stage = "pre_arbitration" }, ("X-Api-Key", tenant.ApiKey));

        escalated.Stage.ShouldBe("pre_arbitration");
        escalated.Status.ShouldBe("open"); // yeniden savunulabilir
        escalated.SubmittedAt.ShouldBeNull();

        // Yeni süre BUGÜNDEN sayılır ve ileridedir. Önceki süreden geç olması ŞART DEĞİL:
        // ön hakem penceresi (7 iş günü) harcama itirazından (9 iş günü) dardır — bu yüzden
        // "eskisinden büyük" beklemek yanlış olurdu.
        escalated.EvidenceDueAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        escalated.RemainingHours.ShouldBeGreaterThan(0);
        escalated.EvidenceDueAt.ShouldNotBe(dispute.EvidenceDueAt);

        // Kademe geri alınamaz
        var back = await Send(HttpMethod.Post, $"/v1/disputes/{dispute.Id}/escalate",
            new { stage = "retrieval" }, ("X-Api-Key", tenant.ApiKey));
        back.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ---- Süre bekçisi ---------------------------------------------------------------

    [Fact]
    public async Task Sure_bekcisi_yaklasani_uyarmali_geceni_kapatmali()
    {
        var tenant = await SeedTenantAsync();

        // ① Süresi yarın dolan dosya → uyarı
        var soon = await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey, 11_000),
            11_000, dueAt: DateTimeOffset.UtcNow.AddDays(1));

        // ② Süresi dün dolmuş dosya → expired
        var late = await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey, 12_000),
            12_000, dueAt: DateTimeOffset.UtcNow.AddDays(-1));

        // ③ Süresi bir ay sonra olan dosya → dokunulmamalı
        var far = await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey, 13_000),
            13_000, dueAt: DateTimeOffset.UtcNow.AddDays(30));

        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatform();
            await scope.ServiceProvider
                .GetRequiredService<Poyra.Modules.Disputes.Infrastructure.DisputeDeadlineJob>()
                .SweepAsync();
        }

        var after = await SendOk<List<DisputeDto>>(HttpMethod.Get, "/v1/disputes", null,
            ("X-Api-Key", tenant.ApiKey));

        after.Single(d => d.Id == late.Id).Status.ShouldBe("expired");
        after.Single(d => d.Id == soon.Id).Status.ShouldBe("open"); // uyarıldı ama kapanmadı
        after.Single(d => d.Id == far.Id).Status.ShouldBe("open");

        // Uyarı defterde
        var detail = await SendOk<DetailDto>(HttpMethod.Get, $"/v1/disputes/{soon.Id}", null,
            ("X-Api-Key", tenant.ApiKey));
        detail.Timeline.ShouldContain(e => e.EventType == "dispute.evidence_due_soon");
    }

    [Fact]
    public async Task Ayni_uyari_iki_kez_gonderilmemeli()
    {
        var tenant = await SeedTenantAsync();
        var dispute = await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey, 14_000),
            14_000, dueAt: DateTimeOffset.UtcNow.AddDays(1));

        for (var round = 0; round < 3; round++)
        {
            using var scope = _factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatform();
            await scope.ServiceProvider
                .GetRequiredService<Poyra.Modules.Disputes.Infrastructure.DisputeDeadlineJob>()
                .SweepAsync();
        }

        var detail = await SendOk<DetailDto>(HttpMethod.Get, $"/v1/disputes/{dispute.Id}", null,
            ("X-Api-Key", tenant.ApiKey));

        // Saatlik iş 3 kez koştu — işyeri 3 bildirim almamalı, yoksa susturur
        detail.Timeline.Count(e => e.EventType == "dispute.evidence_due_soon").ShouldBe(1);
    }

    // ---- Webhook -------------------------------------------------------------------

    [Fact]
    public async Task Itiraz_olaylari_webhook_olarak_gitmeli()
    {
        var tenant = await SeedTenantAsync();
        await SendOk<object>(HttpMethod.Post, "/v1/webhook-endpoints",
            new { url = "http://127.0.0.1:1/olmayan", eventTypes = new[] { "dispute.opened", "dispute.won" } },
            ("X-Api-Key", tenant.ApiKey));

        var dispute = await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey));
        await SendOk<DisputeDto>(HttpMethod.Post, $"/v1/disputes/{dispute.Id}/submit",
            new { summary = "Savunma." }, ("X-Api-Key", tenant.ApiKey));
        await SendOk<DisputeDto>(HttpMethod.Post, $"/v1/disputes/{dispute.Id}/close",
            new { outcome = "won" }, ("X-Api-Key", tenant.ApiKey));

        var deliveries = await SendOk<List<DeliveryDto>>(HttpMethod.Get, "/v1/webhook-deliveries", null,
            ("X-Api-Key", tenant.ApiKey));

        deliveries.ShouldContain(d => d.EventType == "dispute.opened");
        deliveries.ShouldContain(d => d.EventType == "dispute.won");
        // Abone OLUNMAYAN olay gönderilmez
        deliveries.ShouldNotContain(d => d.EventType == "dispute.evidence_submitted");
    }

    // ---- Yalıtım ve defter ----------------------------------------------------------

    [Fact]
    public async Task Baska_isyerinin_itirazi_gorunmemeli()
    {
        var first = await SeedTenantAsync();
        var second = await SeedTenantAsync();
        var dispute = await OpenDisputeAsync(first.ApiKey, await PaidPaymentAsync(first.ApiKey));

        (await SendOk<List<DisputeDto>>(HttpMethod.Get, "/v1/disputes", null,
            ("X-Api-Key", second.ApiKey))).ShouldBeEmpty();

        (await Send(HttpMethod.Get, $"/v1/disputes/{dispute.Id}", null, ("X-Api-Key", second.ApiKey)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Başkasının dosyasını kapatmak da mümkün olmamalı
        (await Send(HttpMethod.Post, $"/v1/disputes/{dispute.Id}/close",
            new { outcome = "lost" }, ("X-Api-Key", second.ApiKey)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Itiraz_defteri_DB_duzeyinde_silinemez_olmali()
    {
        var tenant = await SeedTenantAsync();
        var dispute = await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey));

        await using var db = _fixture.CreateDisputes(PostgresFixture.TenantCtx(tenant.TenantId));
        var record = await db.Disputes.SingleAsync(d => d.PublicId == dispute.Id);

        // Uygulama rolünün DELETE yetkisi YOKTUR (İlke 3) — kod hatası bile kaybettiremez
        db.Disputes.Remove(record);
        var error = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        error.InnerException.ShouldBeOfType<Npgsql.PostgresException>()
            .SqlState.ShouldBe("42501"); // insufficient_privilege

        db.ChangeTracker.Clear();
        (await db.Disputes.CountAsync(d => d.PublicId == dispute.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task Olay_defteri_degistirilemez_olmali()
    {
        var tenant = await SeedTenantAsync();
        var dispute = await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey));

        await using var db = _fixture.CreateDisputes(PostgresFixture.TenantCtx(tenant.TenantId));
        var record = await db.Disputes.AsNoTracking().SingleAsync(d => d.PublicId == dispute.Id);
        var entry = await db.DisputeEvents.SingleAsync(e => e.DisputeId == record.Id);

        entry.GetType().GetProperty(nameof(DisputeEvent.Actor))!.SetValue(entry, "sahte");
        var error = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        error.InnerException.ShouldBeOfType<Npgsql.PostgresException>().SqlState.ShouldBe("42501");
    }

    // ---- Rol kapısı ------------------------------------------------------------------

    [Fact]
    public async Task Denetci_itiraz_yonetememeli_ama_gorebilmeli()
    {
        const string password = "itiraz-parola-123";
        var email = $"sahip-{Guid.NewGuid():N}@ornek.com";
        var tenant = await SendOk<TenantCreated>(HttpMethod.Post, "/v1/tenants", new
        {
            name = "İtiraz Rol A.Ş.",
            slug = "itr-" + Guid.NewGuid().ToString("N")[..10],
            ownerEmail = email,
            ownerPassword = password,
            ownerName = "Sahip",
        }, ("X-Platform-Key", AdminKey));

        await SendOk<object>(HttpMethod.Post, "/v1/connector-accounts", new
        {
            connectorKey = "mockbank",
            label = "Mock POS",
            credentials = new Dictionary<string, string> { ["secret"] = "s3cret" },
            priority = 1,
        }, ("X-Api-Key", tenant.ApiKey));

        var dispute = await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey));

        var auditorEmail = $"denetci-{Guid.NewGuid():N}@ornek.com";
        await SendOk<object>(HttpMethod.Post, "/v1/users",
            new { email = auditorEmail, password, displayName = "Denetçi", role = "auditor" },
            ("X-Api-Key", tenant.ApiKey));

        var login = await SendOk<Dictionary<string, System.Text.Json.JsonElement>>(
            HttpMethod.Post, "/v1/auth/login",
            new { email = auditorEmail, password, tenantSlug = tenant.Slug });
        var token = login["accessToken"].GetString()!;

        // Görmek denetçinin işidir
        (await Send(HttpMethod.Get, "/v1/disputes", null, ("Authorization", $"Bearer {token}")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // Savunmadan vazgeçmek DEĞİL — para kaybettiren bir karardır
        (await Send(HttpMethod.Post, $"/v1/disputes/{dispute.Id}/accept", null,
            ("Authorization", $"Bearer {token}")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ---- Filtreler --------------------------------------------------------------------

    [Fact]
    public async Task Suresi_yaklasanlar_filtrelenebilmeli()
    {
        var tenant = await SeedTenantAsync();

        var urgent = await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey, 21_000),
            21_000, dueAt: DateTimeOffset.UtcNow.AddHours(20));
        await OpenDisputeAsync(tenant.ApiKey, await PaidPaymentAsync(tenant.ApiKey, 22_000),
            22_000, dueAt: DateTimeOffset.UtcNow.AddDays(20));

        var soon = await SendOk<List<DisputeDto>>(HttpMethod.Get, "/v1/disputes?dueWithinHours=48", null,
            ("X-Api-Key", tenant.ApiKey));

        soon.ShouldHaveSingleItem().Id.ShouldBe(urgent.Id);

        // Varsayılan sıralama: süresi en yakın olan ÜSTTE ("hangisi yanıyor")
        var all = await SendOk<List<DisputeDto>>(HttpMethod.Get, "/v1/disputes", null,
            ("X-Api-Key", tenant.ApiKey));
        all[0].Id.ShouldBe(urgent.Id);
    }
}
