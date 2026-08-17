using System.Net;
using System.Net.Sockets;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

/// <summary>
/// Sahte sunucuların port ayırması. Bu testler bir davranışı değil, <b>giderilmiş bir
/// flake'i</b> koruyor: portlar rastgele seçilip boş olduğu varsayıldığında, paralel
/// koşan testler ara sıra çakışıyor ve tam süitte rastgele bir test düşüyordu.
/// </summary>
public sealed class BosPortTests
{
    [Fact]
    public void Ayni_anda_tutulan_portlar_tekil_olmali()
    {
        // Eski kod 10.000'lik aralıktan rastgele seçiyordu; 60 seçimde çakışma olasılığı
        // doğum günü paradoksuyla %16 civarındaydı. İşletim sistemi bağlı portu vermez.
        //
        // Portlar ayrıldıkça AÇIK TUTULUR. Sebebi ince: Ayir() portu okuyup dinleyiciyi
        // kapatır, yani numarayı döndürdüğü an port yeniden boştadır — işletim sistemi
        // onu bir sonraki çağrıda pekâlâ yeniden verebilir. Ayrılanları kapatarak
        // "60 çağrı 60 farklı numara döndürür" demek, fonksiyonun VERMEDİĞİ bir garantiyi
        // sınamak olurdu; nitekim bu test CI'da tam bu yüzden rastgele düşüyordu.
        // Açık tutunca sınanan şey gerçek sözleşme oluyor: aynı anda ayakta duran sahte
        // sunucular birbirinin portunu kapmaz. Rastgele seçime dönülürse test yine kırılır.
        var dinleyiciler = new List<TcpListener>();

        try
        {
            var portlar = new List<int>();
            for (var i = 0; i < 60; i++)
            {
                var port = BosPort.Ayir();
                var dinleyici = new TcpListener(IPAddress.Loopback, port);
                dinleyici.Start();
                dinleyiciler.Add(dinleyici);
                portlar.Add(port);
            }

            portlar.ShouldBeUnique();
            portlar.ShouldAllBe(p => p > 0 && p <= 65535);
        }
        finally
        {
            foreach (var dinleyici in dinleyiciler)
                dinleyici.Stop();
        }
    }

    [Fact]
    public void Ayni_anda_acilan_dinleyicilerin_hepsi_baglanabilmeli()
    {
        // Flake'in tam senaryosu: aynı anda çok sayıda sahte sunucu ayakta.
        // Eski kodda bunlardan biri "address already in use" ile düşerdi.
        var dinleyiciler = new List<HttpListener>();

        try
        {
            var adresler = Enumerable.Range(0, 40)
                .Select(_ =>
                {
                    var dinleyici = new HttpListener();
                    dinleyiciler.Add(dinleyici);
                    return BosPort.Bagla(dinleyici);
                })
                .ToList();

            adresler.ShouldBeUnique();
            dinleyiciler.ShouldAllBe(d => d.IsListening);
        }
        finally
        {
            foreach (var dinleyici in dinleyiciler)
                dinleyici.Close();
        }
    }
}
