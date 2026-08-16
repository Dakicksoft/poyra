using System.Net;
using System.Net.Sockets;

namespace Poyra.Tests.Unit;

/// <summary>
/// Sahte HTTP sunucuları için port ayırır.
///
/// <b>Neden var:</b> önceden port <c>Random.Shared.Next(40000, 50000)</c> ile seçilip
/// boş olduğu VARSAYILIYORDU. xUnit test sınıflarını paralel koştuğundan aynı anda
/// birden çok sahte sunucu ayakta oluyor ve ara sıra ikisi aynı portu seçiyordu;
/// ikincisi "address already in use" ile düşüyordu. Belirtisi şuydu: yalnız TAM süit
/// koşusunda, rastgele bir testte, tekrar koşunca geçen hata — yani en pahalı hata
/// türü, çünkü insanı gerçek regresyonu da "flake" sanmaya alıştırır.
///
/// Şimdi portu işletim sistemi veriyor: o anda bağlı olan bir portu asla vermez.
/// </summary>
internal static class BosPort
{
    public static int Ayir()
    {
        using var dinleyici = new TcpListener(IPAddress.Loopback, 0);
        dinleyici.Start();
        var port = ((IPEndPoint)dinleyici.LocalEndpoint).Port;
        dinleyici.Stop();
        return port;
    }

    /// <summary>
    /// Dinleyiciyi boş bir porta bağlar. Bırakma ile bağlanma arasında teorik bir yarış
    /// kaldığı için birkaç kez denenir — sessizce düşmektense yeniden denemek yeğdir.
    /// </summary>
    public static string Bagla(HttpListener dinleyici)
    {
        for (var deneme = 0; ; deneme++)
        {
            var adres = $"http://127.0.0.1:{Ayir()}";
            dinleyici.Prefixes.Clear();
            dinleyici.Prefixes.Add(adres + "/");

            try
            {
                dinleyici.Start();
                return adres;
            }
            catch (HttpListenerException) when (deneme < 4)
            {
                // port kapıldı — yeni bir tane iste
            }
        }
    }
}
