using Android.App;
using Android.Content.PM;

namespace Poyra.Field.App;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    // Ekran döndürme ve klavye açılışı uygulamayı YENİDEN BAŞLATMAMALI: temsilci
    // tutarı yazarken telefon dönerse girdisini kaybetmemeli
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation
        | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity;
