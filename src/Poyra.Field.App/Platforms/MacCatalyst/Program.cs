using ObjCRuntime;
using UIKit;

namespace Poyra.Field.App;

/// <summary>
/// Yalnız TİP DENETİMİ hedefi. Ürün hedefi Android'dir (saha telefonları); bu giriş
/// noktası, Android derlemesi için JDK 21 bulunmayan makinelerde kodun yine de
/// derlendiğini doğrulayabilmek içindir.
/// </summary>
public static class Program
{
    private static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}

[Foundation.Register(nameof(AppDelegate))]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
