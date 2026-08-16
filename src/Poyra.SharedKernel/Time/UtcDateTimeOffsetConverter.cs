using System.Text.Json;
using System.Text.Json.Serialization;

namespace Poyra.SharedKernel.Time;

/// <summary>
/// İstemciden gelen her <see cref="DateTimeOffset"/> değerini SINIRDA UTC'ye çevirir.
///
/// <b>Neden zorunlu:</b> Npgsql'in <c>timestamp with time zone</c> tipi yalnız sıfır ofset
/// kabul eder; <c>+03:00</c> yazan bir değer kaydedilirken
/// <c>ArgumentException</c> ile patlar ve istek 500 döner.
///
/// <b>Neden tehlikeli:</b> Türkiye'de <c>2026-12-31T23:59:59+03:00</c> yazmak EN DOĞAL
/// yazımdır — işyerinin geliştiricisi kendi saatiyle düşünür. Aynı isteğin <c>Z</c>'li hali
/// sorunsuz çalıştığı için hata "bizde çalışıyor" diye kapanır ve yalnız yerel saatle yazan
/// müşteride görülür. Bu tuzağa gerçekten düşüldü: ödeme bağlantısı süresi, kara liste süresi,
/// itiraz süresi ve saha senkronu — dördü de yerel ofsetle 500 veriyordu.
///
/// <b>Bilgi kaybı yok:</b> <see cref="DateTimeOffset"/> → UTC dönüşümü ANI korur; yalnız
/// gösterim ofseti düşer. Kullanıcıya gösterim zaten <see cref="TurkeyTime.ToLocal"/> ile
/// yapılır. Cihaz/istemcinin harfi harfine ne gönderdiği önemliyse (saha beyanı gibi) ham
/// metin ayrıca saklanır.
///
/// Tek tek her uçta <c>ToUniversalTime()</c> çağırmak yerine sınırda çözülür: unutulabilecek
/// bir adım, er ya da geç unutulur.
/// </summary>
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDateTimeOffset().ToUniversalTime();

    public override void Write(
        Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToUniversalTime());
}
