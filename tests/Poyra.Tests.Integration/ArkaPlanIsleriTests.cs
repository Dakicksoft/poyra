using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Api;
using Poyra.Modules.Ledger.Domain;
using Poyra.Modules.Ledger.Infrastructure;
using Poyra.SharedKernel.Tenancy;
using Poyra.SharedKernel.Time;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// Arka plan işleri. Kapsam ölçümü bunların <b>%0</b> olduğunu gösterdi: gecikmiş alacak
/// damgalama ve abonelik tahsilatı hiçbir testte çalıştırılmıyordu.
///
/// Bu işlerin kendine özgü riski KİRACI DÖNGÜSÜDÜR: hepsi platform bağlamında başlayıp
/// her işyeri için <c>tenant.Set(...)</c> yapar. Döngüde bir hata, bir işyerinin kaydını
/// başka işyerinin bağlamında güncellemek demektir — RLS'in tam da engellemesi gereken şey,
/// ama bağlam yanlış kurulursa RLS de yanlış işyerini doğru sanır.
/// </summary>
[Collection("postgres")]
public sealed class ArkaPlanIsleriTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly WebApplicationFactory<ApiEntryPoint> _factory;

    public ArkaPlanIsleriTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _factory = new WebApplicationFactory<ApiEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Poyra", fixture.AppCs);
            builder.UseSetting("ConnectionStrings:PoyraMigrations", fixture.OwnerCs);
            builder.UseSetting("Database:AutoMigrate", "false");
            builder.UseSetting("Platform:AdminKey", "test-admin-key");
            builder.UseSetting("Poyra:PublicBaseUrl", "http://localhost");
            builder.UseSetting("Poyra:CredentialKey", Convert.ToBase64String(new byte[32]));
        });
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Gecikmis_alacak_damgalanmali_vadesi_gelmeyen_dokunulmamali()
    {
        var (tenantA, _) = await _fixture.SeedTwoTenantsAsync();
        var bugun = TurkeyTime.DayOf(DateTimeOffset.UtcNow);

        var gecikmis = await AlacakEkleAsync(tenantA, bugun.AddDays(-3));
        var vadesiGelmemis = await AlacakEkleAsync(tenantA, bugun.AddDays(5));
        var teyitli = await AlacakEkleAsync(tenantA, bugun.AddDays(-3), ReceivableStatus.Settled);

        await IsiKosturAsync();

        (await DurumOkuAsync(tenantA, gecikmis)).ShouldBe(ReceivableStatus.Overdue);
        (await DurumOkuAsync(tenantA, vadesiGelmemis)).ShouldBe(ReceivableStatus.Expected);

        // Hesaba geçmiş alacak geriye dönük "gecikmiş" olamaz — iş yalnız Expected'a dokunur
        (await DurumOkuAsync(tenantA, teyitli)).ShouldBe(ReceivableStatus.Settled);
    }

    [Fact]
    public async Task Is_her_isyerini_KENDI_baglaminda_islemeli()
    {
        var (tenantA, tenantB) = await _fixture.SeedTwoTenantsAsync();
        var bugun = TurkeyTime.DayOf(DateTimeOffset.UtcNow);

        var aAlacagi = await AlacakEkleAsync(tenantA, bugun.AddDays(-1));
        var bAlacagi = await AlacakEkleAsync(tenantB, bugun.AddDays(-1));

        await IsiKosturAsync();

        // İkisi de damgalanmalı: döngü bir işyerinde durursa ötekinin alacağı sessizce
        // "zamanında" görünür ve bankadan sorulmayan para olur.
        (await DurumOkuAsync(tenantA, aAlacagi)).ShouldBe(ReceivableStatus.Overdue);
        (await DurumOkuAsync(tenantB, bAlacagi)).ShouldBe(ReceivableStatus.Overdue);

        // Ve karışmamalı: A'nın bağlamından B'nin kaydı görünmemeli (RLS)
        await using var db = _fixture.CreateLedger(PostgresFixture.TenantCtx(tenantA));
        (await db.BankReceivables.CountAsync(r => r.Id == bAlacagi)).ShouldBe(0);
    }

    // ---- Yardımcılar -----------------------------------------------------------

    private async Task IsiKosturAsync()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatform();
        await scope.ServiceProvider.GetRequiredService<OverdueReceivableJob>().FlagAsync();
    }

    private async Task<Guid> AlacakEkleAsync(
        Guid tenantId, DateOnly beklenenValor, ReceivableStatus durum = ReceivableStatus.Expected)
    {
        await using var db = _fixture.CreateLedger(PostgresFixture.TenantCtx(tenantId));

        var alacak = new BankReceivable
        {
            TenantId = tenantId,
            ConnectorAccountId = Guid.CreateVersion7(),
            Kind = ReceivableKind.Sale,
            AttemptPublicId = "att_" + Guid.NewGuid().ToString("N")[..12],
            GrossMinor = 149_00,
            Installments = 1,
            ExpectedValueDate = beklenenValor,
            CapturedAtServer = DateTimeOffset.UtcNow.AddDays(-7),
            Status = durum,
        };

        db.BankReceivables.Add(alacak);
        await db.SaveChangesAsync();
        return alacak.Id;
    }

    private async Task<ReceivableStatus> DurumOkuAsync(Guid tenantId, Guid alacakId)
    {
        await using var db = _fixture.CreateLedger(PostgresFixture.TenantCtx(tenantId));
        return (await db.BankReceivables.SingleAsync(r => r.Id == alacakId)).Status;
    }
}
