using System.Net;
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
    public void Ayrilan_portlar_tekil_olmali()
    {
        // Eski kod 10.000'lik aralıktan rastgele seçiyordu; 60 seçimde çakışma olasılığı
        // doğum günü paradoksuyla %16 civarındaydı. İşletim sistemi bağlı portu vermez.
        var portlar = Enumerable.Range(0, 60).Select(_ => BosPort.Ayir()).ToList();

        portlar.ShouldBeUnique();
        portlar.ShouldAllBe(p => p > 0 && p <= 65535);
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
