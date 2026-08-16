using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Poyra.Field.Core;

namespace Poyra.Field.App;

/// <summary>
/// Sahadaki tek ekran: tahsilat kaydet ve kuyruğu senkronla.
///
/// <b>Tasarım kararı — "Kaydet" ASLA ağ beklemez.</b> Temsilci dükkânda, müşteri karşısında
/// ve muhtemelen kapsama alanı dışında. Butona basınca dönen çemberi izlemek zorunda
/// kalırsa uygulama sahada kullanılamaz. Kayıt yerel olarak biter; senkron arka plandadır.
///
/// <b>Ekranda gösterilen para her zaman kuruşuyla</b> — TR'de "1.234,50 ₺" yazmak yerine
/// yuvarlamak, temsilcinin kasa sayımını tutturamaması demektir.
/// </summary>
public sealed class CollectionPage : ContentPage
{
    private readonly FieldQueue _queue;
    private readonly FieldSyncClient _sync;

    /// <summary>
    /// Yalnız RAKAM alır; kuruş sağdan dolar (POS terminali alışkanlığı).
    /// Ayraç yok — Android sayısal klavyesi virgülü yutuyor, nokta ise TR okumasında
    /// binlik ayracı: ikisi birlikte 100 kat sapma üretiyordu.
    /// </summary>
    private readonly Entry _amount = new()
    {
        Placeholder = "Tutarı kuruşuyla yazın (ör. 123450)",
        Keyboard = Keyboard.Numeric,
        FontSize = 24,
    };

    /// <summary>Yazarken canlı karşılık — temsilci kaydetmeden ÖNCE ne göndereceğini görür.</summary>
    private readonly Label _amountPreview = new()
    {
        FontSize = 34,
        FontAttributes = FontAttributes.Bold,
        HorizontalTextAlignment = TextAlignment.Center,
        Text = "0,00 ₺",
    };

    private readonly Entry _customerRef = new() { Placeholder = "Müşteri kodu (isteğe bağlı)" };
    private readonly Picker _method = new()
    {
        Title = "Tahsilat yöntemi",
        ItemsSource = new List<string> { "Ödeme bağlantısı", "Karekod", "Nakit (beyan)" },
        SelectedIndex = 0,
    };

    private readonly Label _status = new() { FontSize = 14, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Label _summary = new() { FontSize = 16, FontAttributes = FontAttributes.Bold };
    private readonly CollectionView _list = new();

    private readonly IServiceProvider _services;

    public CollectionPage(FieldQueue queue, FieldSyncClient sync, IServiceProvider services)
    {
        _queue = queue;
        _sync = sync;
        _services = services;

        Title = "Tahsilat";
        Padding = 16;

        // Kurulum ekranı üst çubukta: temsilci kodu olmadan senkron çalışmaz ve
        // temsilcinin nereye bakacağını bilmesi gerekir
        ToolbarItems.Add(new ToolbarItem
        {
            Text = "Kurulum",
            Command = new Command(async () =>
                await Navigation.PushAsync(_services.GetRequiredService<SettingsPage>())),
        });

        _amount.TextChanged += (_, e) => _amountPreview.Text = TurkishMoney.Preview(e.NewTextValue);

        var save = new Button { Text = "Tahsilatı Kaydet", FontSize = 20, HeightRequest = 60 };
        save.Clicked += async (_, _) => await SaveAsync();

        var syncButton = new Button { Text = "Senkronla", HeightRequest = 48 };
        syncButton.Clicked += async (_, _) => await SyncAsync();

        _list.ItemTemplate = new DataTemplate(() =>
        {
            var amount = new Label { FontAttributes = FontAttributes.Bold };
            amount.SetBinding(Label.TextProperty, new Binding(nameof(QueuedCollection.AmountMinor),
                converter: new KurusConverter()));

            var state = new Label { FontSize = 12 };
            state.SetBinding(Label.TextProperty, new Binding(nameof(QueuedCollection.State),
                converter: new QueueStateConverter()));

            var reason = new Label { FontSize = 12, TextColor = Colors.OrangeRed };
            reason.SetBinding(Label.TextProperty, nameof(QueuedCollection.RejectReason));

            return new VerticalStackLayout { Padding = 8, Children = { amount, state, reason } };
        });

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 12,
                Children = { _amount, _amountPreview, _customerRef, _method, save, syncButton, _summary, _status, _list },
            },
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshAsync();
    }

    private async Task SaveAsync()
    {
        if (!TurkishMoney.TryParseDigits(_amount.Text, out var minor))
        {
            await DisplayAlertAsync("Tutar",
                "Tutarı yalnız rakamla, kuruşuyla yazın. Örnek: 1.234,50 ₺ için 123450.", "Tamam");
            return;
        }

        var method = _method.SelectedIndex switch
        {
            1 => "qr",
            2 => "cash_declared",
            _ => "link",
        };

        await _queue.EnqueueAsync(new QueuedCollection
        {
            Method = method,
            AmountMinor = minor,
            CustomerRef = string.IsNullOrWhiteSpace(_customerRef.Text) ? null : _customerRef.Text.Trim(),

            // Cihaz saati BEYANDIR. Sunucu bunu düzeltmez, yanına kendi zamanını yazar.
            CapturedAtDevice = DateTimeOffset.Now,
        });

        _amount.Text = string.Empty;
        _amountPreview.Text = "0,00 ₺";
        _customerRef.Text = string.Empty;
        _status.Text = "Kaydedildi. Ağ geldiğinde gönderilecek.";

        await RefreshAsync();

        // Senkron BEKLENMEZ: başarısız olursa kayıt zaten kuyrukta duruyor
        _ = SyncAsync();
    }

    private async Task SyncAsync()
    {
        if (!AppSettings.IsConfigured)
        {
            _status.Text = "Cihaz kurulmamış — üstteki \"Kurulum\" ile temsilci kodunu girin.";
            return;
        }

        SyncRunResult result;
        try
        {
            result = await _sync.RunAsync(AppSettings.AgentCode, AppSettings.DeviceId);
        }
        catch (Exception exception)
        {
            // İkinci savunma hattı: senkron "ateşle-unut" olarak da çağrılıyor
            // (kaydetmeden sonra). Oradaki gözlenmemiş bir istisna süreci kapatır.
            _status.Text = $"Senkron başarısız: {exception.Message}\nKayıtlar cihazda duruyor.";
            return;
        }

        _status.Text = result switch
        {
            { Reachable: false } => "Ağ yok — kayıtlar cihazda bekliyor, kaybolmadı.",
            { Error: not null } => $"Sunucu hatası: {result.Error}",
            _ => $"Gönderildi: {result.Accepted} · zaten kayıtlı: {result.Duplicate}"
                 + (result.Rejected > 0 ? $" · REDDEDİLEN: {result.Rejected}" : string.Empty),
        };

        // Saat sapması sessiz kalmamalı: beyanları tutarsız görünen temsilci,
        // gün sonu raporunda "saati bozuk" olarak işaretlenir ve nedenini bilmez
        if (Math.Abs(result.ClockSkew.TotalMinutes) > FieldSyncClient.SkewWarning.TotalMinutes)
        {
            _status.Text += $"\n⚠ Telefon saatiniz {Math.Abs(result.ClockSkew.TotalMinutes):N0} dk "
                            + (result.ClockSkew > TimeSpan.Zero ? "geride." : "ileride.")
                            + " Lütfen otomatik saati açın.";
        }

        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var (pending, synced, rejected, pendingMinor) = await _queue.SummaryAsync();

        _summary.Text = $"Bekleyen {pending} ({TurkishMoney.Format(pendingMinor)}) · "
                        + $"Gönderilen {synced} · Reddedilen {rejected}";

        _list.ItemsSource = await _queue.PendingAsync(limit: 50);
    }

    // --- TR para biçimi çekirdektedir (TurkishMoney): test edilebilir olmalı ---

    private sealed class QueueStateConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is QueueState state ? QueueStateText.Of(state) : string.Empty;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class KurusConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is long minor ? TurkishMoney.Format(minor) : string.Empty;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
