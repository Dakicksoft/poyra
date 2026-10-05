# Poyra.Tests.Sandbox

Sağlayıcıların **gerçek test ortamlarına** karşı uçtan uca testler. Birim testleri sahte
sunucuyla gövde biçimini korur; buradaki testler sağlayıcının o gövdeyi gerçekten kabul
ettiğini ve parayı gerçekten çektiğini/iade ettiğini kanıtlar.

Kimlik bilgisi yoksa testler **atlanır** (`dotnet test` kırmızı vermez) — CI'da ve
kimliği olmayan geliştiricide böyledir.

## Lidio

Kimlik bilgileri depoya girmez. Lidio'nun verdiği test bilgileriyle bir kez:

```bash
cd tests/Poyra.Tests.Sandbox
dotnet user-secrets set "Lidio:MerchantCode" "<işyeri kodu>"
dotnet user-secrets set "Lidio:ApiKey" "<MxS2S API anahtarı>"
dotnet user-secrets set "Lidio:MerchantKey" "<merchant key>"
dotnet user-secrets set "Lidio:ApiPassword" "<API parolası>"
```

Ortam değişkeni de olur: `Lidio__MerchantCode`, `Lidio__ApiKey`, … Servis adresi
varsayılan `https://test.lidio.com/api`; `Lidio:GatewayBase` ile değiştirilebilir.
`MerchantKey`/`ApiPassword` verilmezse API anahtarından çözülür
(anahtar `base64(MerchantKey:ApiPassword)` biçimindedir).

Koşum:

```bash
dotnet test tests/Poyra.Tests.Sandbox
```

### Tarayıcı

3D Secure ve hosted sayfa testleri gerçek Chromium sürer (Playwright; ilk koşumda
tarayıcıyı kendisi indirir). Pencere **görünür** açılır: Garanti'nin test 3D motoru
headless tarayıcıyı "güvenlik politikası" sayfasıyla reddediyor. Tespit atlatılmaz
(sahte user-agent vb.); engellemeyen bir ortamda `POYRA_SANDBOX_HEADLESS=1` ile headless
koşulabilir.

Test kartı Garanti BBVA `5549 6022 5721 0013`, 02/2030, CVV 689, 3D kodu 147852
(developer.lidio.com → Test Card Numbers).

### Kapsam

| Sınıf | Akışlar |
|---|---|
| `LidioPaymentSandboxTests` | 3D'siz satış, iptal, çift iptal, kısmi iade, aynı iade kimliğiyle tekrar (mükerrer değil), aynı tutarlı ikinci kısmi iade, fazla iade reddi, kalanın iadesi, tam iade, ön provizyon + kısmi kapama + iade, ön provizyon iptali, hatalı CVV, son kullanma tarihi geçmiş kart, aynı sipariş no ile çift satış, sağlık yoklaması, taksit sorgusu, BIN sorgusu |
| `LidioThreeDsSandboxTests` | 3DS direct satış + dönüş hash'i + iade, bankada iptal edilen 3D, kurcalanmış dönüş, hosted sayfa satışı + iptal, hosted ön ödeme + Finish, saklı kartla 3D ödeme, hosted hesap (kart) yönetimi sayfası |
| `LidioStoredCardSandboxTests` | kart kaydet / listele / belirteçle sorgula / güncelle / sil, saklı kartla ödeme (doğrulamasız ve CVV doğrulamalı), ödeme sonrası kart kaydı, geçici kayıttan kalıcıya, müşterinin bütün kartlarını silme, hesap yetkisine bağlı metotlar |

**Hesap yetkisine bağlı metotlar.** `CardToTokenInquiry`, `CopyStoredCard`,
`GetBankOfBINNumber` ve OTP'li *Interoperable* mod metotları (`SendOTPForCardSave`,
`SendOTPForCardUpdate`, `SendOTPForCardRetrieve`, `RetrieveCards`,
`SendOTPforConsentUpdate`, `UpdateConsent`) Lidio tarafında hesap düzeyinde açılır.
Açık değilse `InvalidCredential` döner; testler isteğin biçimsel olarak kabul edildiğini
sınar ve sonucu çıktıya yazar. Yetki açıldığında aynı testler başarı yolunu sınar.
