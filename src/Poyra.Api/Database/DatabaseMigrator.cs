using Microsoft.EntityFrameworkCore;
using Poyra.Modules.Compliance;
using Poyra.Modules.Connectors;
using Poyra.Modules.Customers;
using Poyra.Modules.Field;
using Poyra.Modules.Ledger;
using Poyra.Modules.Disputes;
using Poyra.Modules.Installments;
using Poyra.Modules.PaymentLinks;
using Poyra.Modules.Payments;
using Poyra.Modules.Recon;
using Poyra.Modules.Risk;
using Poyra.Modules.Routing;
using Poyra.Modules.Subscriptions;
using Poyra.Modules.Tenancy;
using Poyra.Modules.Vault;
using Poyra.Modules.Webhooks;

namespace Poyra.Api.Database;

public static class DatabaseMigrator
{
    public static async Task RunAsync(string ownerConnectionString, ILogger? logger = null)
    {
        logger?.LogInformation("Migration'lar uygulanıyor (Tenancy → Connectors → Routing → Payments)…");

        await using (var tenancy = TenancyDbContext.CreateForMigrations(ownerConnectionString))
        {
            await tenancy.Database.MigrateAsync();
        }

        await using (var connectors = ConnectorsDbContext.CreateForMigrations(ownerConnectionString))
        {
            await connectors.Database.MigrateAsync();
        }

        await using (var routing = RoutingDbContext.CreateForMigrations(ownerConnectionString))
        {
            await routing.Database.MigrateAsync();
        }

        await using (var payments = PaymentsDbContext.CreateForMigrations(ownerConnectionString))
        {
            await payments.Database.MigrateAsync();
        }

        await using (var webhooks = WebhooksDbContext.CreateForMigrations(ownerConnectionString))
        {
            await webhooks.Database.MigrateAsync();
        }

        // Installments, connector_accounts'a FK kurar → Connectors'tan sonra olmalı
        await using (var installments = InstallmentsDbContext.CreateForMigrations(ownerConnectionString))
        {
            await installments.Database.MigrateAsync();
        }

        // Recon, connector_accounts + payment_attempts'e FK kurar → ikisinden sonra
        await using (var recon = ReconDbContext.CreateForMigrations(ownerConnectionString))
        {
            await recon.Database.MigrateAsync();
        }

        // Vault bağımsızdır (yalnız tenants) — sırası serbest
        await using (var vault = VaultDbContext.CreateForMigrations(ownerConnectionString))
        {
            await vault.Database.MigrateAsync();
        }

        await using (var subscriptions = SubscriptionsDbContext.CreateForMigrations(ownerConnectionString))
        {
            await subscriptions.Database.MigrateAsync();
        }

        await using (var links = PaymentLinksDbContext.CreateForMigrations(ownerConnectionString))
        {
            await links.Database.MigrateAsync();
        }

        // İtirazlar, payment_intents'e FK kurar → Payments'tan sonra
        await using (var disputes = DisputesDbContext.CreateForMigrations(ownerConnectionString))
        {
            await disputes.Database.MigrateAsync();
        }

        // Risk bağımsızdır (yalnız tenants) — sırası serbest
        await using (var risk = RiskDbContext.CreateForMigrations(ownerConnectionString))
        {
            await risk.Database.MigrateAsync();
        }

        // Uyum bağımsızdır (yalnız tenants) — sırası serbest
        await using (var compliance = ComplianceDbContext.CreateForMigrations(ownerConnectionString))
        {
            await compliance.Database.MigrateAsync();
        }

        // Müşteriler bağımsızdır (yalnız tenants) — sırası serbest
        await using (var customers = CustomersDbContext.CreateForMigrations(ownerConnectionString))
        {
            await customers.Database.MigrateAsync();
        }

        // Saha, ödeme bağlantısı üretir ama tabloları bağımsızdır — sırası serbest
        await using (var field = FieldDbContext.CreateForMigrations(ownerConnectionString))
        {
            await field.Database.MigrateAsync();
        }

        // Defter, ödemeler ve mutabakat tablolarına FK ile bağlı DEĞİLDİR (att_/pay_
        // genel kimliklerle gevşek bağ) — sırası serbest
        await using (var ledger = LedgerDbContext.CreateForMigrations(ownerConnectionString))
        {
            await ledger.Database.MigrateAsync();
        }

        logger?.LogInformation("Migration'lar tamam.");
    }
}
