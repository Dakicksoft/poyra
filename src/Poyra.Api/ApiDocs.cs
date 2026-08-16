namespace Poyra.Api;

public static class ApiDocs
{
    public const string Overview = """
        Poyra, Türkiye pazarı için çok işyerli ödeme orkestrasyonu platformudur.
        Tek entegrasyonla birden çok banka POS'una bağlanır, işlemi kurala göre
        yönlendirir ve sonucu tek bir sözlükle raporlar.

        ## Kimlik doğrulama

        İki yol vardır ve **karıştırılmamalıdır**:

        | Yol | Başlık | Kim kullanır | Rol denetimi |
        |---|---|---|---|
        | API anahtarı | `X-Api-Key: sk_live_…` | Sunucunuz | Yok — anahtar tam yetkilidir |
        | JWT | `Authorization: Bearer …` | Panel/insan kullanıcı | Var (owner → auditor) |

        API anahtarı işyerinin tam yetkili makine kimliğidir: tarayıcıya, mobil
        uygulamaya veya herhangi bir istemci koduna **konulmaz**. Sızarsa
        `POST /v1/api-keys` ile yenisini üretin, sunucunuza dağıtın, sonra
        `POST /v1/api-keys/{id}/revoke` ile eskisini kapatın — bu sıra tahsilatınızı
        kesintisiz tutar.

        Platform uçları (`POST /v1/tenants`) `X-Platform-Key` ister; bu anahtar
        işyerlerine verilmez.

        ## Hata gövdesi

        Tüm hatalar aynı şekildedir:

        ```json
        { "code": "payment.not_found", "message": "Ödeme bulunamadı.", "errors": null }
        ```

        Doğrulama hatalarında `errors` alan başına doldurulur:

        ```json
        {
          "code": "validation_failed",
          "message": "Doğrulama hatası.",
          "errors": [{ "field": "amountMinor", "message": "'amount Minor' 0'dan büyük olmalı." }]
        }
        ```

        `code` alanına göre dallanın, `message` alanına göre değil: mesajlar kullanıcıya
        gösterilmek için yazılır ve sürüm içinde değişebilir; kodlar sabittir.

        Kart reddi ayrı bir katmandır: HTTP **200** döner, ödeme `failed` olur ve
        `lastError.code` birleşik sözlükten bir değer taşır (`poyra.insufficient_funds`
        gibi). Banka ham kodu `lastError.rawCode` alanında saklı kalır.

        ## Tutarlar

        Tüm tutarlar **kuruş** (minor unit) cinsinden tam sayıdır: `149,90 ₺` → `14990`.
        Ondalık gönderilmez. Taksitli işlemde intent'teki `amountMinor` **mal bedelidir**;
        karttan çekilen vade farklı toplam yanıttaki `chargedAmountMinor` alanındadır ve
        iade tavanı bu ikincisidir.

        ## Idempotency

        `POST /v1/payments` ve `POST /v1/refunds` çağrılarında `Idempotency-Key` başlığı
        gönderin. Aynı anahtarla tekrarlanan istek yeni kayıt açmaz, ilk sonucu döner —
        ağ zaman aşımından sonra güvenle tekrar edebilirsiniz.

        ## Türkiye'ye özel notlar

        - **Gün sınırı UTC+3'tür.** Rapor ve mutabakat günü sunucunun `captured_at`
          alanına göre TR gününe düşer; `2026-08-01` bir TR günüdür, UTC günü değil.
        - **Taksit** yalnız TR banka POS'larında vardır. Yurt dışı sağlayıcılara
          (Stripe, Adyen) taksitli işlem yönlendirilmez; hiçbir uygun POS yoksa
          `409 installments.not_offered` döner ve intent `created` kalır.
        - **Void ile iade farklıdır.** Gün sonu kapanmadan `POST /v1/payments/{id}/cancel`
          komisyon doğurmaz; kapandıktan sonra yalnız iade mümkündür.

        ## Webhook'lar

        Ödeme sonuçlandığında sisteminize bildirim gönderilir. İmzayı **mutlaka**
        doğrulayın; doğrulamayan uç, sahte bir "ödeme başarılı" bildirimiyle
        kandırılabilir. Örnek kod ve olay kataloğu: `entegrasyon.md`.

        ## Kaynaklar

        - Entegrasyon rehberi: `entegrasyon.md`
        - PCI kanıt paketi: `pci-kanit-paketi.md`
        - Kurulum (self-host): `kurulum.md`
        """;
}
