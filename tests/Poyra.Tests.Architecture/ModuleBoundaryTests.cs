using NetArchTest.Rules;
using Poyra.Modules.Compliance;
using Poyra.Modules.Connectors;
using Poyra.Modules.Customers;
using Poyra.Modules.Disputes;
using Poyra.Modules.Installments;
using Poyra.Modules.Payments;
using Poyra.Modules.Recon;
using Poyra.Modules.Risk;
using Poyra.Modules.Routing;
using Poyra.Modules.Subscriptions;
using Poyra.Modules.Tenancy;
using Poyra.Modules.Vault;
using Poyra.Modules.Webhooks;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Architecture;

/// <summary>
/// İlke 1'in bekçisi: modüller birbirine yalnız Contracts ad alanı üzerinden bağlanabilir.
/// İç ad alanlarına (Domain/Infrastructure/Features/Dsl/Migrations) ve DbContext'lere
/// çapraz erişim burada kırmızı yanar.
/// </summary>
public sealed class ModuleBoundaryTests
{
    private static readonly string[] ConnectorsInternals =
    [
        "Poyra.Modules.Connectors.Domain",
        "Poyra.Modules.Connectors.Infrastructure",
        "Poyra.Modules.Connectors.Features",
        "Poyra.Modules.Connectors.Migrations",
        "Poyra.Modules.Connectors.ConnectorsDbContext",
        "Poyra.Modules.Connectors.ConnectorsModule",
    ];

    private static readonly string[] RoutingInternals =
    [
        "Poyra.Modules.Routing.Domain",
        "Poyra.Modules.Routing.Infrastructure",
        "Poyra.Modules.Routing.Features",
        "Poyra.Modules.Routing.Dsl",
        "Poyra.Modules.Routing.Migrations",
        "Poyra.Modules.Routing.RoutingDbContext",
        "Poyra.Modules.Routing.RoutingModule",
    ];

    private static readonly string[] WebhooksInternals =
    [
        "Poyra.Modules.Webhooks.Domain",
        "Poyra.Modules.Webhooks.Infrastructure",
        "Poyra.Modules.Webhooks.Features",
        "Poyra.Modules.Webhooks.Migrations",
        "Poyra.Modules.Webhooks.WebhooksDbContext",
        "Poyra.Modules.Webhooks.WebhooksModule",
    ];

    private static readonly string[] InstallmentsInternals =
    [
        "Poyra.Modules.Installments.Domain",
        "Poyra.Modules.Installments.Infrastructure",
        "Poyra.Modules.Installments.Features",
        "Poyra.Modules.Installments.Migrations",
        "Poyra.Modules.Installments.InstallmentsDbContext",
        "Poyra.Modules.Installments.InstallmentsModule",
    ];

    private static readonly string[] VaultInternals =
    [
        "Poyra.Modules.Vault.Domain",
        "Poyra.Modules.Vault.Infrastructure",
        "Poyra.Modules.Vault.Features",
        "Poyra.Modules.Vault.Migrations",
        "Poyra.Modules.Vault.VaultDbContext",
        "Poyra.Modules.Vault.VaultModule",
    ];

    private static readonly string[] PaymentsInternals =
    [
        "Poyra.Modules.Payments.Domain",
        "Poyra.Modules.Payments.Infrastructure",
        "Poyra.Modules.Payments.Features",
        "Poyra.Modules.Payments.Migrations",
        "Poyra.Modules.Payments.PaymentsDbContext",
        "Poyra.Modules.Payments.PaymentsModule",
    ];

    private static readonly string[] ReconInternals =
    [
        "Poyra.Modules.Recon.Domain",
        "Poyra.Modules.Recon.Infrastructure",
        "Poyra.Modules.Recon.Features",
        "Poyra.Modules.Recon.Migrations",
        "Poyra.Modules.Recon.ReconDbContext",
        "Poyra.Modules.Recon.ReconModule",
    ];

    private static readonly string[] TenancyInternals =
    [
        "Poyra.Modules.Tenancy.Domain",
        "Poyra.Modules.Tenancy.Infrastructure",
        "Poyra.Modules.Tenancy.Features",
        "Poyra.Modules.Tenancy.Security",
        "Poyra.Modules.Tenancy.Migrations",
        "Poyra.Modules.Tenancy.TenancyDbContext",
        "Poyra.Modules.Tenancy.TenancyModule",
    ];

    [Fact]
    public void Payments_yalniz_Contracts_uzerinden_bagimli_olmali()
    {
        var result = Types.InAssembly(PaymentsModule.Assembly)
            .ShouldNot().HaveDependencyOnAny(
                ["Poyra.Modules.Tenancy", .. ConnectorsInternals, .. RoutingInternals,
                 .. WebhooksInternals, .. InstallmentsInternals, .. VaultInternals])
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(result));
    }

    [Fact]
    public void Subscriptions_yalniz_Contracts_uzerinden_bagimli_olmali()
    {
        // Abonelik, ödeme başlatır (Payments.Contracts), olay yayar (Webhooks.Contracts) ve
        // işyeri döngüsü kurar (Tenancy.Contracts) — hiçbirinin içyapısına dokunamaz.
        var result = Types.InAssembly(SubscriptionsModule.Assembly)
            .ShouldNot().HaveDependencyOnAny(
                ["Poyra.Modules.Connectors", "Poyra.Modules.Routing", "Poyra.Modules.Installments",
                 "Poyra.Modules.Recon", "Poyra.Modules.Vault",
                 .. PaymentsInternals, .. WebhooksInternals, .. TenancyInternals])
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(result));
    }

    [Fact]
    public void Disputes_yalniz_Contracts_uzerinden_bagimli_olmali()
    {
        // İtirazlar üç modüle bakar ve hiçbirinin içine giremez:
        //  · Payments.Contracts.IPaymentLookup — itiraza konu ödeme
        //  · SharedKernel.Time.IBankHolidayCalendar — iş günü süresi (tatil verisi;
        //    port çekirdekte, veriyi Recon sahiplenir ve portu UYGULAR)
        //  · Webhooks.Contracts.IWebhookFanout — süre uyarısı ve sonuç duyurusu
        //  · Tenancy.Contracts.ITenantDirectory — süre süpürmesinin işyeri döngüsü
        var result = Types.InAssembly(DisputesModule.Assembly)
            .ShouldNot().HaveDependencyOnAny(
                ["Poyra.Modules.Connectors", "Poyra.Modules.Routing", "Poyra.Modules.Installments",
                 "Poyra.Modules.Vault", "Poyra.Modules.Subscriptions", "Poyra.Modules.PaymentLinks",
                 .. PaymentsInternals, .. WebhooksInternals, .. TenancyInternals, .. ReconInternals])
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(result));
    }

    [Fact]
    public void Hicbir_modul_Disputes_icyapisina_bagimli_olmamali()
    {
        // İtirazlar yaprak modüldür: kimse ona bakmaz, o herkese portla bakar
        foreach (var assembly in new[]
                 {
                     PaymentsModule.Assembly, ReconModule.Assembly, WebhooksModule.Assembly,
                     SubscriptionsModule.Assembly, RoutingModule.Assembly, TenancyModule.Assembly,
                 })
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot().HaveDependencyOn("Poyra.Modules.Disputes")
                .GetResult();

            result.IsSuccessful.ShouldBeTrue($"{assembly.GetName().Name}: {Explain(result)}");
        }
    }

    [Fact]
    public void Risk_yalniz_Payments_Contracts_uzerinden_bagimli_olmali()
    {
        // Risk, Payments'ın TANIMLADIĞI portu (IRiskGate) uygular ve yine Payments'ın
        // portundan (IPaymentVelocitySource) sayaç okur — bağımlılık tersine çevrilmiştir,
        // Payments risk modülünü tanımaz. İçyapıya erişim yasak.
        var result = Types.InAssembly(RiskModule.Assembly)
            .ShouldNot().HaveDependencyOnAny(
                ["Poyra.Modules.Connectors", "Poyra.Modules.Routing", "Poyra.Modules.Installments",
                 "Poyra.Modules.Vault", "Poyra.Modules.Subscriptions", "Poyra.Modules.Recon",
                 "Poyra.Modules.Disputes", "Poyra.Modules.Webhooks", "Poyra.Modules.Tenancy",
                 .. PaymentsInternals])
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(result));
    }

    [Fact]
    public void Payments_risk_modulunu_tanimamali()
    {
        // Risk motorunun yokluğu tahsilatı durdurmamalı: Payments kendi izin veren
        // varsayılanını kullanır ve risk modülüne DERLEME BAĞIMLILIĞI kurmaz.
        var result = Types.InAssembly(PaymentsModule.Assembly)
            .ShouldNot().HaveDependencyOn("Poyra.Modules.Risk")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(result));
    }

    [Fact]
    public void Compliance_hicbir_modulun_icyapisina_bagimli_olmamali()
    {
        // Uyum defteri her modülün eylemini kaydeder ama hiçbirinin içine bakmaz:
        // yakalama merkezî middleware'de, yazma SharedKernel.Audit portu üzerinden.
        var result = Types.InAssembly(ComplianceModule.Assembly)
            .ShouldNot().HaveDependencyOnAny(
                ["Poyra.Modules.Connectors", "Poyra.Modules.Routing", "Poyra.Modules.Installments",
                 "Poyra.Modules.Vault", "Poyra.Modules.Subscriptions", "Poyra.Modules.Recon",
                 "Poyra.Modules.Disputes", "Poyra.Modules.Webhooks", "Poyra.Modules.Tenancy",
                 "Poyra.Modules.Risk", .. PaymentsInternals])
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(result));
    }

    [Fact]
    public void Hicbir_modul_Compliance_icyapisina_bagimli_olmamali()
    {
        // Modüller denetim defterine SharedKernel.Audit.IAuditTrail portundan yazar;
        // uyum modülüne derleme bağımlılığı kurmaz
        foreach (var assembly in new[]
                 {
                     PaymentsModule.Assembly, ReconModule.Assembly, WebhooksModule.Assembly,
                     SubscriptionsModule.Assembly, RoutingModule.Assembly, TenancyModule.Assembly,
                     RiskModule.Assembly, DisputesModule.Assembly,
                 })
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot().HaveDependencyOn("Poyra.Modules.Compliance")
                .GetResult();

            result.IsSuccessful.ShouldBeTrue($"{assembly.GetName().Name}: {Explain(result)}");
        }
    }

    [Fact]
    public void Customers_yalniz_Contracts_uzerinden_bagimli_olmali()
    {
        // Müşteri görünümü üç modülden okur ve hiçbirinin içine giremez:
        // Payments.Contracts.ICustomerPaymentSource, Vault.Contracts.ICustomerCardSource,
        // Subscriptions.Contracts.ICustomerSubscriptionSource
        var result = Types.InAssembly(CustomersModule.Assembly)
            .ShouldNot().HaveDependencyOnAny(
                ["Poyra.Modules.Connectors", "Poyra.Modules.Routing", "Poyra.Modules.Installments",
                 "Poyra.Modules.Recon", "Poyra.Modules.Disputes", "Poyra.Modules.Risk",
                 "Poyra.Modules.Webhooks", "Poyra.Modules.Tenancy", "Poyra.Modules.Compliance",
                 .. PaymentsInternals, .. VaultInternals])
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(result));
    }

    [Fact]
    public void Hicbir_modul_Customers_icyapisina_bagimli_olmamali()
    {
        // Müşteriler yaprak modüldür: customerRef zaten diğer modüllerde dolaşıyor
        // ve onların Müşteriler'i tanımasına gerek yok
        foreach (var assembly in new[]
                 {
                     PaymentsModule.Assembly, VaultModule.Assembly, SubscriptionsModule.Assembly,
                     RiskModule.Assembly, DisputesModule.Assembly, ComplianceModule.Assembly,
                 })
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot().HaveDependencyOn("Poyra.Modules.Customers")
                .GetResult();

            result.IsSuccessful.ShouldBeTrue($"{assembly.GetName().Name}: {Explain(result)}");
        }
    }

    [Fact]
    public void Vault_hicbir_module_bagimli_olmamali()
    {
        // Kasa en hassas modül: yalnız Abstractions + SharedKernel görür (PCI kapsamı dar tutulur)
        var result = Types.InAssembly(VaultModule.Assembly)
            .ShouldNot().HaveDependencyOnAny(
                "Poyra.Modules.Tenancy", "Poyra.Modules.Payments", "Poyra.Modules.Connectors",
                "Poyra.Modules.Routing", "Poyra.Modules.Webhooks", "Poyra.Modules.Installments",
                "Poyra.Modules.Recon")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(result));
    }

    [Fact]
    public void Webhooks_baska_hicbir_module_bagimli_olmamali()
    {
        var result = Types.InAssembly(WebhooksModule.Assembly)
            .ShouldNot().HaveDependencyOnAny(
                "Poyra.Modules.Tenancy", "Poyra.Modules.Payments", "Poyra.Modules.Connectors",
                "Poyra.Modules.Routing", "Poyra.Modules.Installments", "Poyra.Modules.Recon")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(result));
    }

    [Fact]
    public void Installments_yalniz_Connectors_Contracts_uzerinden_bagimli_olmali()
    {
        var result = Types.InAssembly(InstallmentsModule.Assembly)
            .ShouldNot().HaveDependencyOnAny(
                ["Poyra.Modules.Tenancy", "Poyra.Modules.Payments", "Poyra.Modules.Routing",
                 "Poyra.Modules.Webhooks", "Poyra.Modules.Recon", .. ConnectorsInternals])
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(result));
    }

    [Fact]
    public void Recon_yalniz_Contracts_uzerinden_bagimli_olmali()
    {
        // Recon, rota motorunun maliyet PORTUNU uygular (Routing.Contracts.ICommissionRateSource)
        // — bağımlılık tersine çevrilmiştir, Routing'in içyapısına yine dokunamaz.
        var result = Types.InAssembly(ReconModule.Assembly)
            .ShouldNot().HaveDependencyOnAny(
                ["Poyra.Modules.Webhooks", "Poyra.Modules.Installments",
                 .. ConnectorsInternals, .. PaymentsInternals, .. TenancyInternals, .. RoutingInternals])
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(result));
    }

    [Fact]
    public void Routing_hicbir_modulun_icyapisina_bagimli_olmamali()
    {
        // Rota motoru kimseyi tanımaz: maliyet/performans/geçmiş sinyallerini KENDİ
        // tanımladığı portlardan alır (Payments ve Recon o portları uygular).
        var result = Types.InAssembly(RoutingModule.Assembly)
            .ShouldNot().HaveDependencyOnAny(
                ["Poyra.Modules.Payments", "Poyra.Modules.Recon", "Poyra.Modules.Tenancy",
                 "Poyra.Modules.Webhooks", "Poyra.Modules.Installments", "Poyra.Modules.Vault",
                 "Poyra.Modules.Subscriptions", .. ConnectorsInternals])
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(result));
    }

    [Fact]
    public void Routing_yalniz_Connectors_Contracts_uzerinden_bagimli_olmali()
    {
        var result = Types.InAssembly(RoutingModule.Assembly)
            .ShouldNot().HaveDependencyOnAny(
                ["Poyra.Modules.Tenancy", "Poyra.Modules.Payments", .. ConnectorsInternals])
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(result));
    }

    [Fact]
    public void Connectors_yalniz_Tenancy_Contracts_uzerinden_bagimli_olmali()
    {
        // Canary işi işyerleri üzerinde dolaşır → Tenancy.Contracts (ITenantDirectory) serbest,
        // Tenancy iç yapısı yasak.
        var result = Types.InAssembly(ConnectorsModule.Assembly)
            .ShouldNot().HaveDependencyOnAny(
                ["Poyra.Modules.Payments", "Poyra.Modules.Routing", "Poyra.Modules.Webhooks",
                 "Poyra.Modules.Installments", "Poyra.Modules.Recon", .. TenancyInternals])
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(result));
    }

    [Fact]
    public void Tenancy_baska_hicbir_module_bagimli_olmamali()
    {
        var result = Types.InAssembly(TenancyModule.Assembly)
            .ShouldNot().HaveDependencyOnAny(
                "Poyra.Modules.Payments", "Poyra.Modules.Connectors", "Poyra.Modules.Routing",
                "Poyra.Modules.Webhooks", "Poyra.Modules.Installments", "Poyra.Modules.Recon")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(result));
    }

    [Fact]
    public void Moduller_Api_hostuna_bagimli_olmamali()
    {
        foreach (var assembly in new[]
        {
            TenancyModule.Assembly, PaymentsModule.Assembly, ConnectorsModule.Assembly,
            RoutingModule.Assembly, WebhooksModule.Assembly, InstallmentsModule.Assembly,
            ReconModule.Assembly, VaultModule.Assembly, SubscriptionsModule.Assembly,
        })
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot().HaveDependencyOn("Poyra.Api")
                .GetResult();

            result.IsSuccessful.ShouldBeTrue(Explain(result));
        }
    }

    [Fact]
    public void SharedKernel_hicbir_ust_katmana_bagimli_olmamali()
    {
        var result = Types.InAssembly(typeof(Poyra.SharedKernel.Cqrs.IDispatcher).Assembly)
            .ShouldNot().HaveDependencyOnAny("Poyra.Persistence", "Poyra.Modules", "Poyra.Api", "Poyra.Connectors")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Explain(result));
    }

    [Fact]
    public void Konnektor_adaptorleri_yalniz_Abstractions_gormeli()
    {
        foreach (var assembly in new[]
        {
            typeof(Poyra.Connectors.NestPay.NestPayConnector).Assembly,
            typeof(Poyra.Connectors.Gvp.GvpConnector).Assembly,
            typeof(Poyra.Connectors.PayFlex.PayFlexConnector).Assembly,
        })
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot().HaveDependencyOnAny("Poyra.Modules", "Poyra.Persistence", "Poyra.SharedKernel", "Poyra.Api")
                .GetResult();

            result.IsSuccessful.ShouldBeTrue(Explain(result));
        }
    }

    private static string Explain(TestResult result)
        => result.IsSuccessful
            ? ""
            : "Sınır ihlali: " + string.Join(", ", result.FailingTypes?.Select(t => t.FullName ?? "?") ?? []);
}
