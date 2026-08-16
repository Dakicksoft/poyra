namespace Poyra.SharedKernel.Time;

/// <summary>
/// Türkiye günü. Kayıtlar UTC saklanır ama "hangi gün" sorusunun cevabı TR saatine göredir:
/// 23:30'da (TR) alınan bir tahsilat UTC'de ertesi güne düşer ve gün sonu raporunda
/// yanlış güne yazılırsa temsilcinin hesabı tutmaz.
///
/// Türkiye 2016'dan beri kalıcı UTC+3'tür; yaz saati uygulaması yoktur.
///
/// <b>Npgsql notu:</b> <c>timestamptz</c> yalnız UTC ofsetli değer kabul eder — sınırlar
/// bu yüzden <see cref="DateTimeOffset.ToUniversalTime"/> ile döndürülür.
/// </summary>
public static class TurkeyTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(3);

    public static DateOnly DayOf(DateTimeOffset moment)
        => DateOnly.FromDateTime(moment.ToOffset(Offset).DateTime);

    /// <summary>TR gününün başlangıcı (00:00:00), UTC olarak.</summary>
    public static DateTimeOffset StartOfDay(DateOnly day)
        => new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), Offset).ToUniversalTime();

    /// <summary>
    /// TR gününün bitişi — ERTESİ günün başlangıcıdır, 23:59:59.999 değil.
    /// Yarı açık aralık [başlangıç, bitiş) kullanmak, gün sonundaki son saniyede
    /// alınan tahsilatın hiçbir rapora girmemesi hatasını baştan imkânsız kılar.
    /// </summary>
    public static DateTimeOffset EndOfDay(DateOnly day)
        => StartOfDay(day.AddDays(1));

    /// <summary>Kullanıcıya gösterilecek TR yerel zamanı.</summary>
    public static DateTimeOffset ToLocal(DateTimeOffset moment) => moment.ToOffset(Offset);
}
