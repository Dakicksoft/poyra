namespace Poyra.Field.Core;

/// <summary>Yerel kuyruktaki kaydın durumu.</summary>
public enum QueueState
{
    /// <summary>Henüz sunucuya ulaşmadı. Gönderilecek olan tek durum budur.</summary>
    Pending = 0,

    /// <summary>Sunucu kabul etti (ya da zaten kayıtlıydı). Bir daha GÖNDERİLMEZ.</summary>
    Synced = 1,

    /// <summary>
    /// Sunucu KALICI olarak reddetti. Kuyruktan çıkar ama SİLİNMEZ: temsilci neyin neden
    /// gitmediğini görebilmeli. Yeniden denemek sonsuz döngü olurdu — ret kalıcıdır.
    /// </summary>
    Rejected = 2,
}

/// <summary>
/// Cihazda tutulan tahsilat kaydı.
///
/// <b>Bu kayıt bir TALEPTİR, tahsilat değil</b>. Cihaz para durumu üretemez;
/// <see cref="ServerStatus"/> yalnız sunucunun söylediğini yansıtır ve cihazdan yazılmaz.
/// </summary>
public sealed class QueuedCollection
{
    /// <summary>
    /// Cihazda üretilen işlem kimliği. <b>Bir kez üretilir ve ASLA değişmez</b> — yeniden
    /// deneme sırasında yeniden üretilseydi sunucu her denemeyi yeni bir tahsilat sanar
    /// ve müşteriye tekrar tekrar ödeme talebi giderdi. Kuyruğun tüm güvenliği buna dayanır.
    /// </summary>
    public Guid ClientOpId { get; init; } = Guid.CreateVersion7();

    /// <summary>
    /// Cihazdaki üretim sırası. Veritabanı tarafından artırılır.
    ///
    /// <b>Neden ayrı bir alan:</b> sıra <see cref="ClientOpId"/>'den TÜRETİLEMEZ.
    /// UUIDv7'nin zaman bileşeni milisaniye çözünürlüğündedir; aynı milisaniyede üretilen
    /// kayıtların geri kalanı rastgeledir ve sıralamaları karışır. Temsilcinin gün içindeki
    /// sırası, gün sonu kasa sayımında karşılığı olan gerçek bir bilgidir.
    /// </summary>
    public long Sequence { get; set; }

    public required string Method { get; init; }
    public long AmountMinor { get; init; }
    public string Currency { get; init; } = "TRY";

    /// <summary>Cihaz saati — sunucuya BEYAN olarak gider, otorite değildir.</summary>
    public DateTimeOffset CapturedAtDevice { get; init; }

    public string? CustomerRef { get; init; }
    public string? Description { get; init; }
    public string? Note { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    public QueueState State { get; set; } = QueueState.Pending;

    /// <summary>Kaç kez gönderilmeye çalışıldı — temsilciye "ağ yok" demek için.</summary>
    public int Attempts { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    // --- sunucudan gelenler (cihaz bu alanları KENDİ yazmaz) ---

    /// <summary>Sunucudaki kaydın kimliği (fc_…).</summary>
    public string? ServerId { get; set; }

    /// <summary>Sunucunun bildirdiği durum. Para durumu YALNIZ buradan gelir.</summary>
    public string? ServerStatus { get; set; }

    /// <summary>Müşteriye gösterilecek/paylaşılacak ödeme adresi.</summary>
    public string? CheckoutUrl { get; set; }

    /// <summary>Kalıcı ret gerekçesi — temsilci ne yapacağını bilsin.</summary>
    public string? RejectReason { get; set; }
}

/// <summary>Kuyruk durumunun temsilciye gösterilecek karşılığı.</summary>
public static class QueueStateText
{
    /// <summary>
    /// Ham enum adı ("Pending") ekranda görünmemeli: uygulamanın tamamı Türkçe ve
    /// saha temsilcisinden İngilizce okuması beklenemez.
    /// </summary>
    public static string Of(QueueState state) => state switch
    {
        QueueState.Pending => "Gönderilmeyi bekliyor",
        QueueState.Synced => "Sunucuya iletildi",
        QueueState.Rejected => "Reddedildi",
        _ => state.ToString(),
    };
}
