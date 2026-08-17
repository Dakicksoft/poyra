# Güvenlik Politikası

Poyra bir ödeme platformudur; güvenlik açıkları bizim için sıradan hata değil,
en yüksek öncelikli iştir. Sorumlu bildirim yapan herkese şimdiden teşekkür ederiz.

## Açık bildirme

Lütfen güvenlik açığı için **kamuya açık issue veya PR açmayın**, ayrıntıyı
tartışmalarda/sosyal medyada paylaşmayın.

Bildirim tek kanaldan yapılır:

**[GitHub Security Advisories — özel bildirim](https://github.com/Dakicksoft/poyra/security/advisories/new)**

Bildirimde şunlar işimizi kolaylaştırır:

- Etkilenen bileşen (API / Panel / Checkout / Saha uygulaması / konnektör adı)
- Yeniden üretme adımları — mümkünse yerel geliştirme kurulumunda (MockBank ile)
- Etki değerlendirmeniz: hangi veri/işyeri sınırı aşılıyor?
- Varsa öneri veya yama

### Ne bekleyebilirsiniz

| Adım | Hedef süre |
|---|---|
| Alındı onayı | 3 iş günü |
| İlk değerlendirme (geçerli/kapsam dışı) | 7 iş günü |
| Düzeltme ve yayın | Kritikliğe göre; kritik açıklarda öncelik her şeyin üstündedir |

Düzeltme yayınlanana kadar ayrıntının gizli tutulmasını rica ederiz
(koordineli açıklama). Düzeltme yayınlandığında advisory kamuya açılır;
dileğiniz dışında adınız anılmaz, dilerseniz teşekkürle anılırsınız.

Bu bağımsız bir açık kaynak projedir: **para ödülü (bug bounty) programı yoktur.**

## Desteklenen sürümler

Proje henüz sürüm etiketlememiştir; güvenlik düzeltmeleri yalnız `main` dalına
uygulanır. Sürümleme başladığında bu tablo güncellenecektir.

| Sürüm | Destek |
|---|---|
| `main` | ✅ |

## Kapsam — özellikle ilgilendiğimiz alanlar

Poyra'nın güvenlik iddiaları test edilebilir ilkelerdir; bunları kıran her şey
yüksek önceliklidir:

- **İşyeri izolasyonu ihlali** — EF global filter veya Postgres RLS katmanını
  aşarak başka işyerinin verisini okuma/yazma
- **Kart verisi** — PAN/CVV sızması, Kasa modülünün (AES-256-GCM) zayıflatılması,
  PCI bekçi testlerinin atlatılabildiği bir kod yolu
- **Banka callback sahteciliği** — imza doğrulamasını atlatma, tek kullanımlık
  callback belirtecinin tahmini/yeniden kullanımı, "imzasız dönüşün kabulü"
- **Webhook** — `Poyra-Signature` HMAC atlatma, replay koruması ihlali
- **Kimlik doğrulama** — TOTP 2FA atlatma, cihaz hatırlama kötüye kullanımı,
  API anahtarı/oturum yönetimi zafiyetleri
- **Sırlar** — anahtar türetme hataları, tek kullanımlık sır gösteriminin
  atlatılması, sırların log/URL'e sızması
- **Panel** — CSRF/antiforgery atlatma, imzalı flash mesajı sahteciliği,
  tehlikeli aksiyon onayının atlatılması
- **Silinemezlik** — append-only olay defterlerinde `UPDATE/DELETE` yapılabilen
  bir yol bulunması
- Konnektör adaptörlerindeki güvenlik hataları — **"SERTİFİKASYON BEKLİYOR"
  işaretli olanlar dahil** (imza/doğrulama hataları sandbox'ta da açıktır)

## Kapsam dışı

- Geliştirme ortamı varsayılanları (`dev-platform-key`, MockBank sihirli
  tutarları, `docker-compose.yml` geliştirme kompozisyonu) — bunlar bilinçli
  olarak yalnız geliştirme içindir
- Self-host kurulumun yanlış yapılandırılması (TLS'siz yayın, `.env`'in dünyaya
  açılması, süper kullanıcıyla çalıştırma) — [üretim kurulum rehberindeki](README.md#üretim-kurulumu-self-host)
  uyarılar bu riskleri zaten belgeler
- Çalıştırılabilir kanıt içermeyen salt bağımlılık sürümü raporları
  ("X paketinin CVE'si var" — sömürülebilirlik gösterilmeden)
- Hacim/DoS testleri ve üçüncü taraf sistemlerdeki (banka, PSP, GitHub) açıklar
- Sosyal mühendislik ve fiziksel erişim senaryoları

## Test kuralları

Güvenlik araştırmanızı **kendi yerel kurulumunuzda** yapın
(`docker compose up -d` + MockBank ile tam akış çalışır, gerçek banka gerekmez).
Başkalarına ait canlı Poyra kurulumlarına karşı test yapmayın; gerçek kart
verisiyle test etmeyin.

## Mimari güvenlik özeti

Tasarım gereği alınan önlemler README'nin [Güvenlik](README.md#güvenlik)
bölümünde özetlenmiştir: banka-hosted 3DS (kart verisi Poyra'ya uğramaz),
iki katmanlı işyeri izolasyonu (EF filter + RLS), append-only defterler,
AES-256-GCM sır zarfları, SHA-512 anahtar özetleri, TOTP 2FA ve CSRF koruması.
Bu iddiaların her biri test süitinde bekçi testleriyle doğrulanır.

---

## Reporting in English

Poyra is a Turkish-first project, but security reports in English are very
welcome. Please use
[GitHub Security Advisories](https://github.com/Dakicksoft/poyra/security/advisories/new)
for private disclosure — do not open public issues for vulnerabilities.
We aim to acknowledge within 3 business days. There is no bug bounty program;
credit is given in the advisory unless you prefer otherwise.
