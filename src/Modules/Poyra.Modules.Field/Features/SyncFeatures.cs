using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Field.Contracts;
using Poyra.Modules.Field.Domain;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Domain;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Field.Features;

public sealed record FieldSyncOperation(
    Guid ClientOpId,
    string Method,
    long AmountMinor,
    string Currency,
    DateTimeOffset CapturedAtDevice,
    string? CustomerRef,
    string? Description,
    string? Note,
    double? Latitude,
    double? Longitude);


public sealed record FieldSyncResult(
    Guid ClientOpId,
    string Outcome,
    string? CollectionId,
    string? Status,
    string? CheckoutUrl,
    string? Reason);


public sealed record FieldSyncResponse(
    DateTimeOffset ServerTime,
    Guid AgentId,
    int Accepted,
    int Duplicate,
    int Rejected,
    IReadOnlyList<FieldSyncResult> Results);

public sealed record FieldSyncRequest(
    string AgentCode,
    string DeviceId,
    IReadOnlyList<FieldSyncOperation> Operations);

public sealed record SyncFieldBatchCommand(
    string AgentCode,
    string DeviceId,
    IReadOnlyList<FieldSyncOperation> Operations) : Poyra.SharedKernel.Cqrs.ICommand<FieldSyncResponse>;

public sealed class SyncFieldBatchValidator : AbstractValidator<SyncFieldBatchCommand>
{
    /// <summary>
    /// Tek istekte taşınabilecek işlem sayısı. Sınır, kötü niyetten çok gerçeklikten:
    /// haftalarca çevrimdışı kalmış bir cihaz binlerce kayıt biriktirebilir ve hepsini
    /// tek istekte göndermek hem zaman aşımına hem de yarım işlenmiş partiye yol açar.
    /// Cihaz kuyruğu parçalara böler; sıra korunur çünkü her parça sırayla gider.
    /// </summary>
    public const int MaxOperations = 200;

    public SyncFieldBatchValidator()
    {
        RuleFor(x => x.AgentCode).NotEmpty().MaximumLength(60);
        RuleFor(x => x.DeviceId).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Operations).NotNull()
            .Must(o => o.Count <= MaxOperations)
            .WithMessage($"Tek senkronda en çok {MaxOperations} işlem gönderilebilir.");
    }
}


public sealed class SyncFieldBatchHandler(
    FieldDbContext db,
    TenantContext tenant,
    IClock clock,
    IFieldCheckoutLinks links)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<SyncFieldBatchCommand, FieldSyncResponse>
{

    public static readonly TimeSpan ImplausibleSkew = TimeSpan.FromDays(3);

    public async Task<FieldSyncResponse> Handle(SyncFieldBatchCommand command, CancellationToken ct)
    {
        var now = clock.UtcNow;


        var code = TurkishText.Fold(command.AgentCode);

        var agent = await db.FieldAgents
            .SingleOrDefaultAsync(a => a.Code == code, ct)
            ?? throw PoyraException.NotFound("field.agent_not_found",
                $"Saha temsilcisi bulunamadı: {command.AgentCode}.");

        if (!agent.IsActive)
            throw PoyraException.Conflict("field.agent_disabled",
                "Temsilci kapatılmış; senkron kabul edilmez.");

        BindDevice(agent, command.DeviceId, now);

        // 1) TEKRARLAR ÖNCE. Cihaz onayı alamadan koptuysa aynı partiyi yeniden gönderir;
        //    bağlantıyı ikinci kez üretmek müşteriye ikinci bir ödeme talebi göndermek olurdu.
        var incoming = command.Operations.Select(o => o.ClientOpId).ToList();
        var existing = await db.FieldCollections
            .Where(c => incoming.Contains(c.ClientOpId))
            .ToDictionaryAsync(c => c.ClientOpId, ct);

        var results = new List<FieldSyncResult>(command.Operations.Count);
        var pending = new List<(FieldSyncOperation Op, FieldCollection Entity)>();

        foreach (var op in command.Operations)
        {
            if (existing.TryGetValue(op.ClientOpId, out var already))
            {
                // SUNUCU KAZANIR: cihazın gönderdiği değerler kaydın ÜSTÜNE YAZILMAZ.
                // Ama çelişki sessizce yutulmaz — cihaz aynı işlem kimliğiyle farklı bir tutar
                // gönderdiyse (temsilci çevrimdışıyken düzeltti, ya da kuyruk bozuldu) bu fark
                // suistimal incelemesinin başlangıç noktasıdır ve device_claims'e eklenir.
                AppendClaimIfConflicting(already, op, command.DeviceId, now);

                results.Add(new FieldSyncResult(op.ClientOpId, "duplicate", already.PublicId,
                    FieldCollectionStatusMap.ToDb[already.Status], already.CheckoutUrl, null));
                continue;
            }

            var problem = Validate(op);
            if (problem is not null)
            {
                results.Add(new FieldSyncResult(op.ClientOpId, "rejected", null, null, null, problem));
                continue;
            }

            var method = FieldCollectionMethodMap.FromDb[op.Method];
            pending.Add((op, new FieldCollection
            {
                TenantId = tenant.TenantId,
                AgentId = agent.Id,
                ClientOpId = op.ClientOpId,
                CustomerRef = op.CustomerRef,
                AmountMinor = op.AmountMinor,
                Currency = op.Currency.ToUpperInvariant(),
                Method = method,

                // UTC'ye ÇEVRİLİR. Npgsql'in timestamptz'i yalnız sıfır ofset kabul eder
                // ve Türkiye'deki her telefon "+03:00" gönderir — çevirmeseydik saha
                // uygulaması hedef pazarında ilk senkronda 500 alırdı.
                // Bilgi kaybı yok: DateTimeOffset→UTC ANI korur, yalnız gösterim ofseti
                // düşer. Cihazın harfi harfine ne söylediği device_claims'te ham durur.
                CapturedAtDevice = op.CapturedAtDevice.ToUniversalTime(),

                // Yyasal zaman sunucudan gelir. Cihaz ne derse desin.
                OccurredAtServer = now,
                DeviceSkewSeconds = FieldCollection.SkewSeconds(now, op.CapturedAtDevice),

                Latitude = op.Latitude,
                Longitude = op.Longitude,
                Note = op.Note,

                Status = FieldCollection.InitialStatusFor(method),
                DeviceClaims = JsonSerializer.Serialize(new[]
                {
                    new { at = op.CapturedAtDevice, device = command.DeviceId, op.AmountMinor, op.Method },
                }),
            }));
        }

        // 2) Bağlantı üretimi yalnız GERÇEKTEN yeni kayıtlar için
        foreach (var (op, entity) in pending)
        {
            if (entity.Method is FieldCollectionMethod.CashDeclared or FieldCollectionMethod.SoftPosRedirect)
                continue;

            var link = await links.CreateAsync(
                op.AmountMinor, entity.Currency,
                op.Description ?? $"Saha tahsilatı — {agent.Code}",
                op.CustomerRef, null, ct);

            entity.PaymentLinkId = link.PublicId;
            entity.CheckoutUrl = link.Url;
            entity.Status = FieldCollectionStatus.LinkIssued;
        }

        db.FieldCollections.AddRange(pending.Select(p => p.Entity));
        agent.LastSyncAt = now;
        await db.SaveChangesAsync(ct);

        results.AddRange(pending.Select(p => new FieldSyncResult(
            p.Op.ClientOpId, "accepted", p.Entity.PublicId,
            FieldCollectionStatusMap.ToDb[p.Entity.Status], p.Entity.CheckoutUrl, null)));

        // Cihazın gönderdiği sırayla döndür — kuyruk eşlemesi indekse değil kimliğe
        // dayanır ama sıralı yanıt cihaz tarafındaki ayıklamayı basitleştirir
        var byId = results.ToDictionary(r => r.ClientOpId);
        var ordered = command.Operations
            .Select(o => byId[o.ClientOpId])
            .ToList();

        return new FieldSyncResponse(
            now, agent.Id,
            ordered.Count(r => r.Outcome == "accepted"),
            ordered.Count(r => r.Outcome == "duplicate"),
            ordered.Count(r => r.Outcome == "rejected"),
            ordered);
    }


    private static void AppendClaimIfConflicting(
        FieldCollection record, FieldSyncOperation op, string deviceId, DateTimeOffset now)
    {
        var sameAmount = record.AmountMinor == op.AmountMinor;
        var sameMethod = FieldCollectionMethodMap.ToDb[record.Method] == op.Method;
        var sameMoment = record.CapturedAtDevice == op.CapturedAtDevice;

        if (sameAmount && sameMethod && sameMoment)
            return; // gerçek yeniden gönderim — çelişki yok, kayda dokunma

        var claims = JsonSerializer.Deserialize<List<JsonElement>>(record.DeviceClaims) ?? [];
        claims.Add(JsonSerializer.SerializeToElement(new
        {
            at = op.CapturedAtDevice,
            device = deviceId,
            op.AmountMinor,
            op.Method,
            conflictedAt = now,
        }));

        record.DeviceClaims = JsonSerializer.Serialize(claims);
    }

    private static void BindDevice(FieldAgent agent, string deviceId, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(agent.DeviceId))
        {
            agent.DeviceId = deviceId;
            agent.EnrolledAt = now;
            return;
        }

        if (!string.Equals(agent.DeviceId, deviceId, StringComparison.Ordinal))
            throw PoyraException.Conflict("field.device_mismatch",
                "Bu temsilci başka bir cihaza kayıtlı. Cihaz değişimi için kaydı serbest bırakın.");
    }

    private static string? Validate(FieldSyncOperation op)
    {
        if (op.ClientOpId == Guid.Empty)
            return "clientOpId zorunludur.";

        if (!FieldCollectionMethodMap.FromDb.ContainsKey(op.Method ?? string.Empty))
            return $"Bilinmeyen yöntem: {op.Method}.";

        if (op.AmountMinor <= 0)
            return "Tutar sıfırdan büyük olmalıdır.";

        if (string.IsNullOrWhiteSpace(op.Currency) || op.Currency.Length != 3)
            return "Para birimi üç harfli ISO 4217 kodu olmalıdır.";

        return null;
    }
}


public sealed class SyncFieldBatchEndpoint(IDispatcher dispatcher)
    : Endpoint<FieldSyncRequest, FieldSyncResponse>
{
    public override void Configure()
    {
        Post("/v1/field/sync");
        Description(x => x.WithTags("Field"));
        Summary(s =>
        {
            s.Summary = "Sahadaki cihazın çevrimdışı kuyruğunu gönderir (toplu, yeniden gönderilebilir).";
            s.Description =
                "Her işlem KENDİ sonucunu alır: accepted · duplicate · rejected. Geçersiz tek bir "
                + "kayıt partiyi düşürmez — düşürseydi cihazın kuyruğu tıkanır ve o günün tüm "
                + "tahsilatları kaybolurdu.\n\n"
                + "`clientOpId` cihazda üretilir ve işyeri içinde tekildir: ağ, sunucu kaydettikten "
                + "sonra koparsa cihaz aynı partiyi yeniden gönderir ve İLK kaydın kimliğini geri alır "
                + "(`duplicate`) — müşteriye ikinci kez ödeme talebi gitmez.\n\n"
                + "`capturedAtDevice` bir BEYANDIR. Yasal işlem zamanı sunucudan yazılır "
                + "(`occurredAtServer`) ve raporlama onu kullanır. Yanıttaki `serverTime` ile cihaz "
                + "kendi sapmasını hesaplayıp temsilciyi uyarabilir.\n\n"
                + "Cihaz para durumu ÜRETEMEZ: kayıt `pending_request`/`link_issued` doğar, "
                + "`succeeded` yalnız ödeme akışından sunucuda oluşur. `cash_declared` fon hareketi "
                + "değil, beyandır.";
        });
    }

    public override async Task HandleAsync(FieldSyncRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new SyncFieldBatchCommand(req.AgentCode, req.DeviceId, req.Operations ?? []), ct), ct);
}
