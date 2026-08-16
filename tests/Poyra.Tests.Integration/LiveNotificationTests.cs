using Microsoft.Extensions.Logging.Abstractions;
using Poyra.Persistence.Notifications;
using Poyra.SharedKernel.Notifications;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Integration;

/// <summary>
/// F4.2 canlı bildirim: Postgres LISTEN/NOTIFY gerçek veritabanı üzerinde.
/// Redis gibi ek bileşen yok — panel Api'nin yayınladığı olayı anında alır.
/// </summary>
[Collection("postgres")]
public sealed class LiveNotificationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Yayinlanan_bildirim_dinleyiciye_ulasmali()
    {
        using var listener = new PostgresNotificationListener(
            fixture.AppCs, NullLogger<PostgresNotificationListener>.Instance);

        var received = new TaskCompletionSource<PoyraNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Received += n => received.TrySetResult(n);

        await listener.StartAsync(default);
        await Task.Delay(500); // LISTEN kurulsun

        var publisher = new PostgresNotificationPublisher(
            fixture.AppCs, NullLogger<PostgresNotificationPublisher>.Instance);
        var tenantId = Guid.CreateVersion7();
        await publisher.PublishAsync(new PoyraNotification(
            tenantId, PoyraNotificationTypes.PaymentSucceeded, "pay_test123"));

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.ShouldBe(received.Task, "bildirim 10 sn içinde ulaşmadı");

        var notification = await received.Task;
        notification.TenantId.ShouldBe(tenantId);
        notification.Type.ShouldBe(PoyraNotificationTypes.PaymentSucceeded);
        notification.EntityId.ShouldBe("pay_test123");

        await listener.StopAsync(default);
    }

    [Fact]
    public async Task Yayinci_hata_durumunda_akisi_bozmamali()
    {
        // Bildirim en-iyi-çabadır: bağlantı yoksa ödeme akışı ETKİLENMEZ (sessizce loglanır)
        var publisher = new PostgresNotificationPublisher(
            "Host=127.0.0.1;Port=1;Database=yok;Username=yok;Password=yok;Timeout=1",
            NullLogger<PostgresNotificationPublisher>.Instance);

        await Should.NotThrowAsync(() => publisher.PublishAsync(
            new PoyraNotification(Guid.NewGuid(), PoyraNotificationTypes.PaymentFailed, "pay_x")));
    }
}
