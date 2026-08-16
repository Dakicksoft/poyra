using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.Connectors.Abstractions;
using Poyra.Modules.Connectors.Contracts;
using Poyra.Modules.Connectors.Domain;
using Poyra.Modules.Connectors.Infrastructure;
using Poyra.SharedKernel.Cqrs;

namespace Poyra.Modules.Connectors.Features;

/// <param name="Available">Bu işyerinin BUGÜN kullanabileceği yetenek mi.</param>
/// <param name="Reason">Kullanılamıyorsa neden — "hayır" cevabı tek başına işe yaramaz.</param>
/// <param name="Accounts">Yeteneği sağlayan POS etiketleri.</param>
public sealed record CapabilityDto(
    string Key, string Title, bool Available, string Reason, IReadOnlyList<string> Accounts);

public sealed record TenantCapabilitiesDto(
    int ActiveAccounts, int HealthyAccounts, IReadOnlyList<CapabilityDto> Capabilities);

public sealed record GetTenantCapabilitiesQuery : IQuery<TenantCapabilitiesDto>;

/// <summary>
/// İşyerinin yetenek matrisi: "taksit yapabilir miyim, iade alabilir miyim,
/// 3DS'siz satış açık mı" sorusunun tek yerden cevabı.
///
/// Katalogdan FARKLIDIR: katalog "hangi konnektör neyi destekler" der, bu ise
/// "SENİN kurulumun bugün neyi yapabilir" der. İşyeri entegrasyona başlarken
/// katalogdaki bir yeteneği görüp kendi hesabında olmadığını canlıda öğrenmemeli.
/// </summary>
public sealed class GetTenantCapabilitiesHandler(ConnectorsDbContext db, ConnectorRegistry registry)
    : IQueryHandler<GetTenantCapabilitiesQuery, TenantCapabilitiesDto>
{
    public async Task<TenantCapabilitiesDto> Handle(GetTenantCapabilitiesQuery query, CancellationToken ct)
    {
        var accounts = await db.ConnectorAccounts.AsNoTracking()
            .Where(a => a.Status == ConnectorAccountStatus.Active)
            .Select(a => new { a.ConnectorKey, a.Label, a.Health })
            .ToListAsync(ct);

        // Sağlıksız hesap rotada atlanır — "aktif" olması yeteneği sağladığı anlamına gelmez
        var usable = accounts.Where(a => a.Health != ConnectorHealth.Down).ToList();

        // Katalogda olmayan konnektör anahtarı (eski kayıt, kaldırılmış adaptör) sessizce
        // atlanır — yeteneği "var" saymak, işyerini olmayan bir POS'a güvendirirdi
        var descriptors = usable
            .Where(a => registry.Exists(a.ConnectorKey))
            .Select(a => (Account: a, Descriptor: registry.Get(a.ConnectorKey).Descriptor))
            .ToList();

        List<string> Labels(Func<ConnectorDescriptor, bool> predicate)
            => descriptors.Where(x => predicate(x.Descriptor)).Select(x => x.Account.Label).ToList();

        CapabilityDto Capability(string key, string title, Func<ConnectorDescriptor, bool> predicate, string missing)
        {
            var labels = Labels(predicate);
            return new CapabilityDto(key, title, labels.Count > 0,
                labels.Count > 0 ? $"{labels.Count} POS sağlıyor." : missing, labels);
        }

        var capabilities = new List<CapabilityDto>
        {
            Capability("payments.hosted", "Banka sayfasında 3DS ile tahsilat",
                _ => true, "Kullanılabilir aktif POS yok."),
            Capability("payments.installments", "Taksitli satış",
                d => d.SupportsInstallments,
                "Hiçbir POS taksit sunmuyor (yurt dışı sağlayıcılarda TR taksidi yoktur)."),
            Capability("payments.void", "Gün sonu öncesi iptal (void)",
                d => d.SupportsVoid, "Hiçbir POS void desteklemiyor — iade kullanın."),
            Capability("payments.refund", "İade",
                d => d.SupportsRefund, "Hiçbir POS iade desteklemiyor."),
        };

        return new TenantCapabilitiesDto(accounts.Count, usable.Count, capabilities);
    }
}

public sealed class GetTenantCapabilitiesEndpoint(IDispatcher dispatcher)
    : EndpointWithoutRequest<TenantCapabilitiesDto>
{
    public override void Configure()
    {
        Get("/v1/platform/capabilities");
        Description(x => x.WithTags("Platform"));
        Summary(s => s.Summary =
            "Bu işyerinin kurulumuyla BUGÜN yapabildikleri. Katalogdan farklıdır: "
            + "katalog konnektörün neyi desteklediğini, bu ise sizin hesaplarınızın "
            + "neyi sağladığını söyler.");
    }

    public override async Task HandleAsync(CancellationToken ct)
        => await Send.OkAsync(await dispatcher.Ask(new GetTenantCapabilitiesQuery(), ct), ct);
}
