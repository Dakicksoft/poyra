using FastEndpoints;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Customers.Domain;
using Poyra.Modules.Payments.Contracts;
using Poyra.Modules.Subscriptions.Contracts;
using Poyra.Modules.Vault.Contracts;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;
using Poyra.SharedKernel.Messaging;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;

namespace Poyra.Modules.Customers.Features;

public sealed record CustomerDto(
    string Ref, string? Name, string? Email, string? Phone, string? Notes,
    bool Erased, DateTimeOffset? ErasedAt, DateTimeOffset CreatedAt);

public sealed record MandateDto(
    string Id, string CustomerRef, string CardToken, string Type, string TextVersion,
    DateTimeOffset AcceptedAt, string? AcceptedIp, bool Active,
    DateTimeOffset? RevokedAt, string? RevokedReason);

public sealed record CustomerDetailDto(
    CustomerDto Customer,
    CustomerPaymentTotals Totals,
    IReadOnlyList<CustomerPayment> Payments,
    IReadOnlyList<CustomerCard> Cards,
    IReadOnlyList<CustomerSubscription> Subscriptions,
    IReadOnlyList<MandateDto> Mandates);

internal static class CustomerMap
{
    public static CustomerDto ToDto(Customer c)
        => new(c.Ref, c.Name, c.Email, c.Phone, c.Notes, c.IsErased, c.ErasedAt, c.CreatedAt);

    public static MandateDto ToDto(Mandate m)
        => new(m.PublicId, m.CustomerRef, m.CardToken, MandateTypeMap.ToDb[m.Type],
            m.TextVersion, m.AcceptedAt, m.AcceptedIp, m.IsActive, m.RevokedAt, m.RevokedReason);
}

public sealed record SaveCustomerRequest(
    string? Name = null, string? Email = null, string? Phone = null, string? Notes = null);

public sealed record SaveCustomerCommand(
    string Ref, string? Name, string? Email, string? Phone, string? Notes)
    : Poyra.SharedKernel.Cqrs.ICommand<CustomerDto>;

public sealed class SaveCustomerValidator : AbstractValidator<SaveCustomerCommand>
{
    public SaveCustomerValidator()
    {
        RuleFor(x => x.Ref).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Name).MaximumLength(200);
        RuleFor(x => x.Email).MaximumLength(320).EmailAddress()
            .When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Notes).MaximumLength(2000);
        RuleFor(x => x.Phone)
            .Must(TurkishPhone.IsValid)
            .When(x => !string.IsNullOrWhiteSpace(x.Phone))
            .WithMessage("Geçerli bir TR cep numarası gerekli (SMS bu numaraya gider).");
    }
}

public sealed class SaveCustomerHandler(CustomersDbContext db, TenantContext tenant)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<SaveCustomerCommand, CustomerDto>
{
    public async Task<CustomerDto> Handle(SaveCustomerCommand command, CancellationToken ct)
    {
        var reference = command.Ref.Trim();
        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Ref == reference, ct);

        if (customer is null)
        {
            customer = new Customer { TenantId = tenant.TenantId, Ref = reference };
            db.Customers.Add(customer);
        }

        // Telefon E.164'e normalleşir: aynı numaranın iki yazımı iki müşteri gibi
        // görünmemeli ve SMS gönderimi biçime takılmamalı
        customer.Update(command.Name, command.Email,
            TurkishPhone.ToE164(command.Phone) ?? command.Phone, command.Notes);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw new PoyraException(409, "customer.duplicate_ref",
                $"'{reference}' referansıyla bir müşteri zaten var.");
        }

        return CustomerMap.ToDto(customer);
    }
}

public sealed class SaveCustomerEndpoint(IDispatcher dispatcher) : Endpoint<SaveCustomerRequest, CustomerDto>
{
    public override void Configure()
    {
        Put("/v1/customers/{ref}");
        Description(x => x.WithTags("Customers"));
        Summary(s => s.Summary =
            "Müşteriyi kaydeder ya da günceller. Anahtar İŞYERİNİN referansıdır; "
            + "ödeme akışı bu referansı zaten gönderiyorsa geçmiş kendiliğinden bağlanır.");
    }

    public override async Task HandleAsync(SaveCustomerRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(new SaveCustomerCommand(
            Route<string>("ref")!, req.Name, req.Email, req.Phone, req.Notes), ct), ct);
}

public sealed record ListCustomersQuery(string? Search, int Limit) : IQuery<IReadOnlyList<CustomerDto>>;

public sealed class ListCustomersHandler(CustomersDbContext db)
    : IQueryHandler<ListCustomersQuery, IReadOnlyList<CustomerDto>>
{
    public async Task<IReadOnlyList<CustomerDto>> Handle(ListCustomersQuery query, CancellationToken ct)
    {
        var customers = db.Customers.AsNoTracking();

        if (query.Search is { Length: > 0 } search)
        {
            var term = $"%{search.Trim()}%";
            customers = customers.Where(c =>
                EF.Functions.ILike(c.Ref, term)
                || (c.Name != null && EF.Functions.ILike(c.Name, term))
                || (c.Email != null && EF.Functions.ILike(c.Email, term)));
        }

        var rows = await customers
            .OrderByDescending(c => c.CreatedAt)
            .Take(Math.Clamp(query.Limit, 1, 500))
            .ToListAsync(ct);

        return rows.Select(CustomerMap.ToDto).ToList();
    }
}

public sealed class ListCustomersEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<IReadOnlyList<CustomerDto>>
{
    public override void Configure()
    {
        Get("/v1/customers");
        Description(x => x.WithTags("Customers"));
        Summary(s => s.Summary = "Müşteri listesi. Filtre: search (referans, ad ya da e-posta), limit.");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new ListCustomersQuery(
            Query<string?>("search", isRequired: false),
            Query<int?>("limit", isRequired: false) ?? 100), ct), ct);
}

public sealed record GetCustomerQuery(string Ref) : IQuery<CustomerDetailDto>;

public sealed class GetCustomerHandler(
    CustomersDbContext db,
    ICustomerPaymentSource payments,
    ICustomerCardSource cards,
    ICustomerSubscriptionSource subscriptions)
    : IQueryHandler<GetCustomerQuery, CustomerDetailDto>
{
    public async Task<CustomerDetailDto> Handle(GetCustomerQuery query, CancellationToken ct)
    {
        var reference = query.Ref.Trim();
        var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Ref == reference, ct);

        // Kayıt YOKSA bile görüntü verilir: ödeme akışı customerRef gönderiyor olabilir
        // ve "müşteri kaydı yok" demek, var olan ödemeleri görünmez kılardı
        var dto = customer is null
            ? new CustomerDto(reference, null, null, null, null, false, null, default)
            : CustomerMap.ToDto(customer);

        var mandates = await db.Mandates.AsNoTracking()
            .Where(m => m.CustomerRef == reference)
            .OrderByDescending(m => m.AcceptedAt)
            .ToListAsync(ct);

        return new CustomerDetailDto(
            dto,
            await payments.GetTotalsAsync(reference, ct),
            await payments.GetPaymentsAsync(reference, 50, ct),
            await cards.GetCardsAsync(reference, ct),
            await subscriptions.GetSubscriptionsAsync(reference, ct),
            mandates.Select(CustomerMap.ToDto).ToList());
    }
}

public sealed class GetCustomerEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<CustomerDetailDto>
{
    public override void Configure()
    {
        Get("/v1/customers/{ref}");
        Description(x => x.WithTags("Customers"));
        Summary(s => s.Summary =
            "Müşterinin tam görüntüsü: ödemeler, kayıtlı kartlar, abonelikler ve talimatlar. "
            + "Müşteri kaydı olmasa da ödeme geçmişi döner.");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new GetCustomerQuery(Route<string>("ref")!), ct), ct);
}

public sealed record EraseCustomerCommand(string Ref) : Poyra.SharedKernel.Cqrs.ICommand<CustomerDto>;

public sealed class EraseCustomerHandler(CustomersDbContext db, UserContext user, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<EraseCustomerCommand, CustomerDto>
{
    public async Task<CustomerDto> Handle(EraseCustomerCommand command, CancellationToken ct)
    {
        var reference = command.Ref.Trim();
        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Ref == reference, ct)
            ?? throw PoyraException.NotFound("customer.not_found", "Müşteri kaydı bulunamadı.");

        customer.Erase(user.UserId, clock.UtcNow);

        // Açık talimatlar da iptal edilir: kişisel verisi silinen müşteriden
        // tekrarlayan çekim yapmaya devam etmek, silme talebini anlamsız kılar
        var active = await db.Mandates.Where(m => m.CustomerRef == reference && m.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var mandate in active)
            mandate.Revoke("KVKK silme talebi.", clock.UtcNow);

        await db.SaveChangesAsync(ct);
        return CustomerMap.ToDto(customer);
    }
}

public sealed class EraseCustomerEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<CustomerDto>
{
    public override void Configure()
    {
        Post("/v1/customers/{ref}/erase");
        Description(x => x.WithTags("Customers"));
        Summary(s => s.Summary =
            "KVKK silme talebi: kişisel veri silinir, mali kayıt KALIR (VUK/TTK saklama "
            + "yükümlülüğü). Açık ödeme talimatları da iptal edilir.");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new EraseCustomerCommand(Route<string>("ref")!), ct), ct);
}

public sealed record CreateMandateRequest(
    string CardToken, string TextVersion, string Type = "recurring", string? AcceptedIp = null);

public sealed record CreateMandateCommand(
    string CustomerRef, string CardToken, string TextVersion, string Type, string? AcceptedIp)
    : Poyra.SharedKernel.Cqrs.ICommand<MandateDto>;

public sealed class CreateMandateValidator : AbstractValidator<CreateMandateCommand>
{
    public CreateMandateValidator()
    {
        RuleFor(x => x.CustomerRef).NotEmpty().MaximumLength(100);
        RuleFor(x => x.CardToken).NotEmpty().Must(t => t.StartsWith("tok_"))
            .WithMessage("cardToken Kasa token'ı olmalı (tok_…).");
        RuleFor(x => x.TextVersion).NotEmpty().MaximumLength(40)
            .WithMessage("Onay metninin sürümü zorunlu — metin değişince yeni onay gerekir.");
        RuleFor(x => x.Type).Must(MandateTypeMap.FromDb.ContainsKey)
            .WithMessage($"Geçersiz tür. Geçerli: {string.Join(", ", MandateTypeMap.FromDb.Keys)}");
    }
}

public sealed class CreateMandateHandler(CustomersDbContext db, TenantContext tenant, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<CreateMandateCommand, MandateDto>
{
    public async Task<MandateDto> Handle(CreateMandateCommand command, CancellationToken ct)
    {
        var mandate = new Mandate
        {
            TenantId = tenant.TenantId,
            CustomerRef = command.CustomerRef.Trim(),
            CardToken = command.CardToken.Trim(),
            Type = MandateTypeMap.FromDb[command.Type],
            TextVersion = command.TextVersion.Trim(),
            AcceptedAt = clock.UtcNow,
            AcceptedIp = command.AcceptedIp,
        };

        db.Mandates.Add(mandate);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw new PoyraException(409, "mandate.already_active",
                "Bu kart için zaten aktif bir talimat var — iki aktif onay, "
                + "hangisinin dayanak olduğunu belirsiz bırakır.");
        }

        return CustomerMap.ToDto(mandate);
    }
}

public sealed class CreateMandateEndpoint(IDispatcher dispatcher) : Endpoint<CreateMandateRequest, MandateDto>
{
    public override void Configure()
    {
        Post("/v1/customers/{ref}/mandates");
        Description(x => x.WithTags("Customers"));
        Summary(s => s.Summary =
            "Tekrarlayan çekim talimatı (onay) kaydeder. 'Yetkisiz işlem' itirazında "
            + "sunulacak ilk belgedir: ne zaman, hangi metin sürümü, hangi IP. "
            + "acceptedIp'yi MÜŞTERİNİN adresiyle gönderin — istek sizin sunucunuzdan "
            + "geldiği için otomatik yakalanan adres sizin altyapınızı gösterir ve "
            + "savunmada işe yaramaz.");
    }

    public override async Task HandleAsync(CreateMandateRequest req, CancellationToken ct)
    {
        // Yedek olarak bağlantı adresi alınır ama bu genelde İŞYERİNİN sunucusudur
        // (ya da ters vekilin): savunmada değerli olan müşterinin adresidir ve onu
        // yalnız işyeri bilir
        var ip = req.AcceptedIp ?? HttpContext.Connection.RemoteIpAddress?.ToString();
        await Send.OkAsync(await dispatcher.Send(new CreateMandateCommand(
            Route<string>("ref")!, req.CardToken, req.TextVersion, req.Type, ip), ct), ct);
    }
}

public sealed record RevokeMandateRequest(string? Reason = null);

public sealed record RevokeMandateCommand(string MandateId, string? Reason)
    : Poyra.SharedKernel.Cqrs.ICommand<MandateDto>;

public sealed class RevokeMandateHandler(CustomersDbContext db, IClock clock)
    : Poyra.SharedKernel.Cqrs.ICommandHandler<RevokeMandateCommand, MandateDto>
{
    public async Task<MandateDto> Handle(RevokeMandateCommand command, CancellationToken ct)
    {
        var mandate = await db.Mandates.SingleOrDefaultAsync(m => m.PublicId == command.MandateId, ct)
            ?? throw PoyraException.NotFound("mandate.not_found", "Talimat bulunamadı.");

        mandate.Revoke(command.Reason ?? "Müşteri talebi.", clock.UtcNow);
        await db.SaveChangesAsync(ct);

        return CustomerMap.ToDto(mandate);
    }
}

public sealed class RevokeMandateEndpoint(IDispatcher dispatcher) : Endpoint<RevokeMandateRequest, MandateDto>
{
    public override void Configure()
    {
        Post("/v1/mandates/{id}/revoke");
        Description(x => x.WithTags("Customers"));
        Summary(s => s.Summary =
            "Talimatı iptal eder (kayıt silinmez): iptalden önceki çekimlerin dayanağı "
            + "sonradan sorulur.");
    }

    public override async Task HandleAsync(RevokeMandateRequest req, CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Send(
            new RevokeMandateCommand(Route<string>("id")!, req.Reason), ct), ct);
}
