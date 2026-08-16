using FastEndpoints;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Disputes.Contracts;
using Poyra.Modules.Disputes.Domain;
using Poyra.Modules.Disputes.Infrastructure;
using Poyra.Modules.Payments.Contracts;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Disputes.Features;

public sealed record DisputeResponse(
    string Id,
    string PaymentId,
    long AmountMinor,
    string Currency,
    string Reason,
    string? RawReasonCode,
    string Stage,
    string Status,
    DateTimeOffset OpenedAt,
    DateTimeOffset EvidenceDueAt,
    double RemainingHours,
    bool Overdue,
    string? ConnectorDisputeId,
    string? EvidenceSummary,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ClosedAt,
    int EvidenceCount);

public sealed record DisputeEvidenceResponse(
    Guid Id, string FileName, string ContentType, string Kind,
    int SizeBytes, bool Revoked, DateTimeOffset CreatedAt);

public sealed record DisputeEventResponse(
    string EventType, string Actor, string Payload, DateTimeOffset CreatedAt);

public sealed record DisputeDetailResponse(
    DisputeResponse Dispute,
    IReadOnlyList<DisputeEvidenceResponse> Evidence,
    IReadOnlyList<DisputeEventResponse> Timeline);

internal static class DisputeMap
{
    public static DisputeResponse ToResponse(Dispute d, DateTimeOffset now, int evidenceCount)
        => new(
            d.PublicId, d.PaymentPublicId, d.AmountMinor, d.Currency, d.Reason, d.RawReasonCode,
            DisputeStageMap.ToDb[d.Stage], DisputeStatusMap.ToDb[d.Status],
            d.OpenedAt, d.EvidenceDueAt, Math.Round(d.Remaining(now).TotalHours, 1),
            d.IsOverdue(now), d.ConnectorDisputeId, d.EvidenceSummary,
            d.SubmittedAt, d.ClosedAt, evidenceCount);
}

public sealed record OpenDisputeRequest(
    string PaymentId,
    long AmountMinor,
    string Reason,
    DateTimeOffset? OpenedAt = null,
    DateTimeOffset? EvidenceDueAt = null,
    string Stage = "chargeback",
    string? ConnectorDisputeId = null,
    string? RawReasonCode = null);

public sealed record OpenDisputeCommand(
    string PaymentId,
    long AmountMinor,
    string Reason,
    DateTimeOffset? OpenedAt,
    DateTimeOffset? EvidenceDueAt,
    string Stage,
    string? ConnectorDisputeId,
    string? RawReasonCode) : Poyra.SharedKernel.Cqrs.ICommand<DisputeResponse>;

public sealed class OpenDisputeValidator : AbstractValidator<OpenDisputeCommand>
{
    public OpenDisputeValidator()
    {
        RuleFor(x => x.PaymentId).NotEmpty();
        RuleFor(x => x.AmountMinor).GreaterThan(0);
        RuleFor(x => x.Reason).Must(DisputeReasons.All.Contains)
            .WithMessage($"Geçersiz neden. Geçerli: {string.Join(", ", DisputeReasons.All)}");
        RuleFor(x => x.Stage).Must(DisputeStageMap.FromDb.ContainsKey)
            .WithMessage($"Geçersiz kademe. Geçerli: {string.Join(", ", DisputeStageMap.FromDb.Keys)}");
    }
}

public sealed class OpenDisputeHandler(
    DisputesDbContext db,
    IBankHolidayCalendar calendar,
    TenantContext tenant,
    UserContext user,
    IPaymentLookup payments,
    IDisputeNotifier notifier,
    IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<OpenDisputeCommand, DisputeResponse>
{
    public async Task<DisputeResponse> Handle(OpenDisputeCommand command, CancellationToken ct)
    {
        var payment = await payments.FindByPublicIdAsync(command.PaymentId, ct)
            ?? throw PoyraException.NotFound("dispute.payment_not_found",
                "İtiraza konu ödeme bulunamadı.");

        if (!payment.IsCaptured)
            throw new PoyraException(409, "dispute.payment_not_captured",
                "Tahsil edilmemiş ödemeye itiraz açılamaz.");

        if (command.AmountMinor > payment.AmountMinor)
            throw new PoyraException(409, "dispute.amount_exceeds_payment",
                $"İtiraz tutarı ödemeyi aşamaz (ödeme: {payment.AmountMinor} kuruş).");

        var stage = DisputeStageMap.FromDb[command.Stage];
        var openedAt = command.OpenedAt ?? clock.UtcNow;

        // Bankanın bildirdiği tarih HER ZAMAN öncelikli; hesap yalnız yokluğunda devreye girer
        var dueAt = command.EvidenceDueAt ?? DisputeDeadline.Compute(openedAt, stage, await calendar.GetHolidaysAsync(ct));

        var dispute = Dispute.Open(
            tenant.TenantId, payment.PaymentIntentId, payment.PublicId,
            command.AmountMinor, payment.Currency, command.Reason, stage,
            openedAt, dueAt, command.ConnectorDisputeId, null, command.RawReasonCode);

        db.Disputes.Add(dispute);
        db.DisputeEvents.Add(DisputeEvent.For(dispute, KnownDisputeEvents.Opened, Actor(user), new
        {
            amount_minor = command.AmountMinor,
            reason = command.Reason,
            stage = command.Stage,
            evidence_due_at = dueAt,
        }));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw new PoyraException(409, "dispute.already_recorded",
                $"Bu banka dosya numarası ({command.ConnectorDisputeId}) zaten kayıtlı.");
        }

        await notifier.PublishAsync(dispute, KnownDisputeEvents.Opened, new
        {
            payment_id = dispute.PaymentPublicId,
            amount_minor = dispute.AmountMinor,
            reason = dispute.Reason,
            stage = DisputeStageMap.ToDb[dispute.Stage],
            evidence_due_at = dispute.EvidenceDueAt,
        }, ct);

        return DisputeMap.ToResponse(dispute, clock.UtcNow, 0);
    }


    internal static string Actor(UserContext user)
        => user.UserId is { } id ? $"user:{id}" : "api_key";
}

public sealed class OpenDisputeEndpoint(IDispatcher dispatcher) : Endpoint<OpenDisputeRequest, DisputeResponse>
{
    public override void Configure()
    {
        Post("/v1/disputes");
        Description(x => x.WithTags("Disputes"));
        Summary(s => s.Summary =
            "Bankadan gelen harcama itirazını deftere açar ve kanıt süresini başlatır. "
            + "evidenceDueAt verilmezse iş günü takvimiyle hesaplanır.");
    }

    public override async Task HandleAsync(OpenDisputeRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new OpenDisputeCommand(
            req.PaymentId, req.AmountMinor, req.Reason, req.OpenedAt, req.EvidenceDueAt,
            req.Stage, req.ConnectorDisputeId, req.RawReasonCode), ct), ct);
}

public sealed record ListDisputesQuery(string? Status, string? PaymentId, int? DueWithinHours)
    : IQuery<IReadOnlyList<DisputeResponse>>;

public sealed class ListDisputesHandler(DisputesDbContext db, IClock clock)
    : IQueryHandler<ListDisputesQuery, IReadOnlyList<DisputeResponse>>
{
    public async Task<IReadOnlyList<DisputeResponse>> Handle(ListDisputesQuery query, CancellationToken ct)
    {
        var disputes = db.Disputes.AsNoTracking();

        if (query.Status is { Length: > 0 } status && DisputeStatusMap.FromDb.TryGetValue(status, out var parsed))
            disputes = disputes.Where(d => d.Status == parsed);

        if (query.PaymentId is { Length: > 0 } paymentId)
            disputes = disputes.Where(d => d.PaymentPublicId == paymentId);

        if (query.DueWithinHours is { } hours)
        {
            var limit = clock.UtcNow.AddHours(hours);
            disputes = disputes.Where(d => d.Status == DisputeStatus.Open && d.EvidenceDueAt <= limit);
        }

        // Süresi en yakın olan ÜSTTE: bu ekranın tek işi "hangisi yanıyor" sorusudur
        var rows = await disputes
            .OrderBy(d => d.Status == DisputeStatus.Open ? 0 : 1)
            .ThenBy(d => d.EvidenceDueAt)
            .Take(200)
            .ToListAsync(ct);

        var counts = await db.DisputeEvidences.AsNoTracking()
            .Where(e => e.RevokedAt == null)
            .GroupBy(e => e.DisputeId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var now = clock.UtcNow;
        return rows.Select(d => DisputeMap.ToResponse(d, now, counts.GetValueOrDefault(d.Id))).ToList();
    }
}

public sealed class ListDisputesEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<IReadOnlyList<DisputeResponse>>
{
    public override void Configure()
    {
        Get("/v1/disputes");
        Description(x => x.WithTags("Disputes"));
        Summary(s => s.Summary =
            "İtiraz listesi. Filtreler: status, paymentId, dueWithinHours (süresi yaklaşanlar).");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListDisputesQuery(
            Query<string?>("status", isRequired: false),
            Query<string?>("paymentId", isRequired: false),
            Query<int?>("dueWithinHours", isRequired: false)), ct), ct);
}

public sealed record GetDisputeQuery(string DisputeId) : IQuery<DisputeDetailResponse>;

public sealed class GetDisputeHandler(DisputesDbContext db, IClock clock)
    : IQueryHandler<GetDisputeQuery, DisputeDetailResponse>
{
    public async Task<DisputeDetailResponse> Handle(GetDisputeQuery query, CancellationToken ct)
    {
        var dispute = await db.Disputes.AsNoTracking()
                          .SingleOrDefaultAsync(d => d.PublicId == query.DisputeId, ct)
                      ?? throw PoyraException.NotFound("dispute.not_found", "İtiraz bulunamadı.");

        var evidence = await db.DisputeEvidences.AsNoTracking()
            .Where(e => e.DisputeId == dispute.Id)
            .OrderBy(e => e.CreatedAt)
            .Select(e => new DisputeEvidenceResponse(
                e.Id, e.FileName, e.ContentType, e.Kind, e.SizeBytes, e.RevokedAt != null, e.CreatedAt))
            .ToListAsync(ct);

        var timeline = await db.DisputeEvents.AsNoTracking()
            .Where(e => e.DisputeId == dispute.Id)
            .OrderBy(e => e.CreatedAt)
            .Select(e => new DisputeEventResponse(e.EventType, e.Actor, e.Payload, e.CreatedAt))
            .ToListAsync(ct);

        return new DisputeDetailResponse(
            DisputeMap.ToResponse(dispute, clock.UtcNow, evidence.Count(e => !e.Revoked)),
            evidence, timeline);
    }
}

public sealed class GetDisputeEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<DisputeDetailResponse>
{
    public override void Configure()
    {
        Get("/v1/disputes/{id}");
        Description(x => x.WithTags("Disputes"));
        Summary(s => s.Summary = "İtiraz detayı: kanıt listesi ve silinemez zaman çizelgesi.");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new GetDisputeQuery(Route<string>("id")!), ct), ct);
}

public sealed record AddEvidenceCommand(
    string DisputeId, string FileName, string ContentType, byte[] Content, string Kind)
    : Poyra.SharedKernel.Cqrs.ICommand<DisputeEvidenceResponse>;

public sealed class AddEvidenceHandler(
    DisputesDbContext db, TenantContext tenant, UserContext user, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<AddEvidenceCommand, DisputeEvidenceResponse>
{
    public async Task<DisputeEvidenceResponse> Handle(AddEvidenceCommand command, CancellationToken ct)
    {
        var dispute = await db.Disputes.SingleOrDefaultAsync(d => d.PublicId == command.DisputeId, ct)
            ?? throw PoyraException.NotFound("dispute.not_found", "İtiraz bulunamadı.");

        if (DisputeStatusMap.IsFinal(dispute.Status))
            throw new PoyraException(409, "dispute.already_closed",
                "Sonuçlanmış itiraza kanıt eklenemez.");

        if (command.Content.Length == 0)
            throw new PoyraException(400, "dispute.evidence_empty", "Boş dosya yüklenemez.");

        if (command.Content.Length > DisputeEvidence.MaxFileBytes)
            throw new PoyraException(400, "dispute.evidence_too_large",
                $"Dosya en çok {DisputeEvidence.MaxFileBytes / (1024 * 1024)} MB olabilir.");

        if (!DisputeEvidence.AllowedContentTypes.Contains(command.ContentType))
            throw new PoyraException(400, "dispute.evidence_type_not_allowed",
                $"Desteklenen türler: {string.Join(", ", DisputeEvidence.AllowedContentTypes)}");

        var active = await db.DisputeEvidences
            .CountAsync(e => e.DisputeId == dispute.Id && e.RevokedAt == null, ct);
        if (active >= DisputeEvidence.MaxFilesPerDispute)
            throw new PoyraException(409, "dispute.evidence_too_many",
                $"Bir itirazda en çok {DisputeEvidence.MaxFilesPerDispute} belge olabilir.");

        var evidence = new DisputeEvidence
        {
            TenantId = tenant.TenantId,
            DisputeId = dispute.Id,
            FileName = command.FileName,
            ContentType = command.ContentType,
            Content = command.Content,
            SizeBytes = command.Content.Length,
            Kind = command.Kind,
            UploadedByUserId = user.UserId,
        };

        db.DisputeEvidences.Add(evidence);
        db.DisputeEvents.Add(DisputeEvent.For(dispute, "dispute.evidence_added",
            OpenDisputeHandler.Actor(user),
            new { file_name = command.FileName, kind = command.Kind, size_bytes = evidence.SizeBytes }));

        await db.SaveChangesAsync(ct);

        return new DisputeEvidenceResponse(evidence.Id, evidence.FileName, evidence.ContentType,
            evidence.Kind, evidence.SizeBytes, false, evidence.CreatedAt);
    }
}

public sealed class AddEvidenceEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<DisputeEvidenceResponse>
{
    public override void Configure()
    {
        Post("/v1/disputes/{id}/evidence");
        AllowFileUploads();
        Description(x => x.WithTags("Disputes"));
        Summary(s => s.Summary =
            "Kanıt belgesi yükler (multipart: file, kind). PDF/JPEG/PNG/WEBP/TXT, en çok 10 MB.");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file")
            ?? throw new PoyraException(400, "dispute.evidence_missing", "'file' alanı zorunlu.");

        using var memory = new MemoryStream();
        await file.CopyToAsync(memory, ct);

        var kind = form.TryGetValue("kind", out var k) && k.ToString() is { Length: > 0 } s ? s : "other";

        await Send.OkAsync(await dispatcher.Send(new AddEvidenceCommand(
            Route<string>("id")!, file.FileName, file.ContentType ?? "application/octet-stream",
            memory.ToArray(), kind), ct), ct);
    }
}

public sealed record DownloadEvidenceQuery(string DisputeId, Guid EvidenceId)
    : IQuery<(string FileName, string ContentType, byte[] Content)>;

public sealed class DownloadEvidenceHandler(DisputesDbContext db)
    : IQueryHandler<DownloadEvidenceQuery, (string FileName, string ContentType, byte[] Content)>
{
    public async Task<(string, string, byte[])> Handle(DownloadEvidenceQuery query, CancellationToken ct)
    {
        var dispute = await db.Disputes.AsNoTracking()
                          .SingleOrDefaultAsync(d => d.PublicId == query.DisputeId, ct)
                      ?? throw PoyraException.NotFound("dispute.not_found", "İtiraz bulunamadı.");

        var evidence = await db.DisputeEvidences.AsNoTracking()
                           .SingleOrDefaultAsync(e => e.Id == query.EvidenceId && e.DisputeId == dispute.Id, ct)
                       ?? throw PoyraException.NotFound("dispute.evidence_not_found", "Belge bulunamadı.");

        return (evidence.FileName, evidence.ContentType, evidence.Content);
    }
}

public sealed class DownloadEvidenceEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/v1/disputes/{id}/evidence/{evidenceId}");
        Description(x => x.WithTags("Disputes"));
        Summary(s => s.Summary = "Kanıt belgesini indirir (bytea'dan olduğu gibi).");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var (fileName, contentType, content) = await dispatcher.Ask(
            new DownloadEvidenceQuery(Route<string>("id")!, Route<Guid>("evidenceId")), ct);

        await Send.BytesAsync(content, fileName, contentType, cancellation: ct);
    }
}

public sealed record RevokeEvidenceCommand(string DisputeId, Guid EvidenceId)
    : Poyra.SharedKernel.Cqrs.ICommand<bool>;

public sealed class RevokeEvidenceHandler(DisputesDbContext db, UserContext user, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<RevokeEvidenceCommand, bool>
{
    public async Task<bool> Handle(RevokeEvidenceCommand command, CancellationToken ct)
    {
        var dispute = await db.Disputes.SingleOrDefaultAsync(d => d.PublicId == command.DisputeId, ct)
            ?? throw PoyraException.NotFound("dispute.not_found", "İtiraz bulunamadı.");

        var evidence = await db.DisputeEvidences
                           .SingleOrDefaultAsync(e => e.Id == command.EvidenceId && e.DisputeId == dispute.Id, ct)
                       ?? throw PoyraException.NotFound("dispute.evidence_not_found", "Belge bulunamadı.");

        if (evidence.RevokedAt is null)
        {
            evidence.RevokedAt = clock.UtcNow;
            db.DisputeEvents.Add(DisputeEvent.For(dispute, "dispute.evidence_revoked",
                OpenDisputeHandler.Actor(user), new { file_name = evidence.FileName }));
            await db.SaveChangesAsync(ct);
        }

        return true;
    }
}

public sealed record RevokeEvidenceResponse(bool Revoked);

public sealed class RevokeEvidenceEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<RevokeEvidenceResponse>
{
    public override void Configure()
    {
        Post("/v1/disputes/{id}/evidence/{evidenceId}/revoke");
        Description(x => x.WithTags("Disputes"));
        Summary(s => s.Summary = "Belgeyi iptal eder (silmez) — bankaya gönderilmez.");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(new RevokeEvidenceResponse(await dispatcher.Send(
            new RevokeEvidenceCommand(Route<string>("id")!, Route<Guid>("evidenceId")), ct)), ct);
}

public sealed record SubmitEvidenceRequest(string? Summary);

public sealed record SubmitEvidenceCommand(string DisputeId, string? Summary)
    : Poyra.SharedKernel.Cqrs.ICommand<DisputeResponse>;

public sealed class SubmitEvidenceHandler(
    DisputesDbContext db, UserContext user, IDisputeNotifier notifier, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<SubmitEvidenceCommand, DisputeResponse>
{
    public async Task<DisputeResponse> Handle(SubmitEvidenceCommand command, CancellationToken ct)
    {
        var dispute = await db.Disputes.SingleOrDefaultAsync(d => d.PublicId == command.DisputeId, ct)
            ?? throw PoyraException.NotFound("dispute.not_found", "İtiraz bulunamadı.");

        var evidenceCount = await db.DisputeEvidences
            .CountAsync(e => e.DisputeId == dispute.Id && e.RevokedAt == null, ct);

        // Boş savunma göndermek, süreyi harcayıp dosyayı kaybetmektir
        if (evidenceCount == 0 && string.IsNullOrWhiteSpace(command.Summary))
            throw new PoyraException(409, "dispute.evidence_empty_submission",
                "En az bir belge ya da savunma metni gerekli.");

        dispute.Submit(clock.UtcNow, command.Summary?.Trim());
        db.DisputeEvents.Add(DisputeEvent.For(dispute, KnownDisputeEvents.Submitted,
            OpenDisputeHandler.Actor(user), new { evidence_count = evidenceCount }));
        await db.SaveChangesAsync(ct);

        await notifier.PublishAsync(dispute, KnownDisputeEvents.Submitted, new
        {
            payment_id = dispute.PaymentPublicId,
            evidence_count = evidenceCount,
        }, ct);

        return DisputeMap.ToResponse(dispute, clock.UtcNow, evidenceCount);
    }
}

public sealed class SubmitEvidenceEndpoint(IDispatcher dispatcher) : Endpoint<SubmitEvidenceRequest, DisputeResponse>
{
    public override void Configure()
    {
        Post("/v1/disputes/{id}/submit");
        Description(x => x.WithTags("Disputes"));
        Summary(s => s.Summary =
            "Savunmayı iletir ve dosyayı 'inceleniyor'a alır. Süre dolmuşsa reddeder.");
    }

    public override async Task HandleAsync(SubmitEvidenceRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new SubmitEvidenceCommand(Route<string>("id")!, req.Summary), ct), ct);
}

public sealed record AcceptDisputeCommand(string DisputeId) : Poyra.SharedKernel.Cqrs.ICommand<DisputeResponse>;

public sealed class AcceptDisputeHandler(
    DisputesDbContext db, UserContext user, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<AcceptDisputeCommand, DisputeResponse>
{
    public async Task<DisputeResponse> Handle(AcceptDisputeCommand command, CancellationToken ct)
    {
        var dispute = await db.Disputes.SingleOrDefaultAsync(d => d.PublicId == command.DisputeId, ct)
            ?? throw PoyraException.NotFound("dispute.not_found", "İtiraz bulunamadı.");

        dispute.Accept(clock.UtcNow);
        db.DisputeEvents.Add(DisputeEvent.For(dispute, "dispute.accepted",
            OpenDisputeHandler.Actor(user), new { amount_minor = dispute.AmountMinor }));
        await db.SaveChangesAsync(ct);

        return DisputeMap.ToResponse(dispute, clock.UtcNow, 0);
    }
}

public sealed class AcceptDisputeEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<DisputeResponse>
{
    public override void Configure()
    {
        Post("/v1/disputes/{id}/accept");
        Description(x => x.WithTags("Disputes"));
        Summary(s => s.Summary =
            "Savunmadan vazgeçer (küçük tutarda savunma maliyeti tutarı aşabilir). Kayıt kapanır, silinmez.");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new AcceptDisputeCommand(Route<string>("id")!), ct), ct);
}

public sealed record CloseDisputeRequest(string Outcome);

public sealed record CloseDisputeCommand(string DisputeId, string Outcome)
    : Poyra.SharedKernel.Cqrs.ICommand<DisputeResponse>;

public sealed class CloseDisputeHandler(
    DisputesDbContext db, UserContext user, IDisputeNotifier notifier, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<CloseDisputeCommand, DisputeResponse>
{
    public async Task<DisputeResponse> Handle(CloseDisputeCommand command, CancellationToken ct)
    {
        var dispute = await db.Disputes.SingleOrDefaultAsync(d => d.PublicId == command.DisputeId, ct)
            ?? throw PoyraException.NotFound("dispute.not_found", "İtiraz bulunamadı.");

        if (!DisputeStatusMap.FromDb.TryGetValue(command.Outcome, out var outcome))
            throw new PoyraException(400, "dispute.invalid_outcome",
                "Sonuç yalnız 'won' veya 'lost' olabilir.");

        dispute.Close(outcome, clock.UtcNow);
        var eventType = outcome == DisputeStatus.Won ? KnownDisputeEvents.Won : KnownDisputeEvents.Lost;

        db.DisputeEvents.Add(DisputeEvent.For(dispute, eventType, OpenDisputeHandler.Actor(user),
            new { amount_minor = dispute.AmountMinor }));
        await db.SaveChangesAsync(ct);

        await notifier.PublishAsync(dispute, eventType, new
        {
            payment_id = dispute.PaymentPublicId,
            amount_minor = dispute.AmountMinor,
        }, ct);

        return DisputeMap.ToResponse(dispute, clock.UtcNow, 0);
    }
}

public sealed class CloseDisputeEndpoint(IDispatcher dispatcher) : Endpoint<CloseDisputeRequest, DisputeResponse>
{
    public override void Configure()
    {
        Post("/v1/disputes/{id}/close");
        Description(x => x.WithTags("Disputes"));
        Summary(s => s.Summary = "Bankanın kararını deftere işler (won | lost).");
    }

    public override async Task HandleAsync(CloseDisputeRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new CloseDisputeCommand(Route<string>("id")!, req.Outcome), ct), ct);
}

public sealed record EscalateDisputeRequest(string Stage, DateTimeOffset? EvidenceDueAt = null);

public sealed record EscalateDisputeCommand(string DisputeId, string Stage, DateTimeOffset? EvidenceDueAt)
    : Poyra.SharedKernel.Cqrs.ICommand<DisputeResponse>;

public sealed class EscalateDisputeHandler(
    DisputesDbContext db, IBankHolidayCalendar calendar, UserContext user, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<EscalateDisputeCommand, DisputeResponse>
{
    public async Task<DisputeResponse> Handle(EscalateDisputeCommand command, CancellationToken ct)
    {
        var dispute = await db.Disputes.SingleOrDefaultAsync(d => d.PublicId == command.DisputeId, ct)
            ?? throw PoyraException.NotFound("dispute.not_found", "İtiraz bulunamadı.");

        if (!DisputeStageMap.FromDb.TryGetValue(command.Stage, out var stage))
            throw new PoyraException(400, "dispute.invalid_stage",
                $"Geçersiz kademe. Geçerli: {string.Join(", ", DisputeStageMap.FromDb.Keys)}");

        var now = clock.UtcNow;
        var holidays = await calendar.GetHolidaysAsync(ct);
        var dueAt = command.EvidenceDueAt ?? DisputeDeadline.Compute(now, stage, holidays);

        dispute.Escalate(stage, dueAt);
        db.DisputeEvents.Add(DisputeEvent.For(dispute, "dispute.escalated",
            OpenDisputeHandler.Actor(user), new { stage = command.Stage, evidence_due_at = dueAt }));
        await db.SaveChangesAsync(ct);

        var evidenceCount = await db.DisputeEvidences
            .CountAsync(e => e.DisputeId == dispute.Id && e.RevokedAt == null, ct);

        return DisputeMap.ToResponse(dispute, now, evidenceCount);
    }
}

public sealed class EscalateDisputeEndpoint(IDispatcher dispatcher)
    : Endpoint<EscalateDisputeRequest, DisputeResponse>
{
    public override void Configure()
    {
        Post("/v1/disputes/{id}/escalate");
        Description(x => x.WithTags("Disputes"));
        Summary(s => s.Summary =
            "Bankanın bir üst kademeye taşıdığını işler; yeni kademe yeni süre başlatır.");
    }

    public override async Task HandleAsync(EscalateDisputeRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new EscalateDisputeCommand(Route<string>("id")!, req.Stage, req.EvidenceDueAt), ct), ct);
}
