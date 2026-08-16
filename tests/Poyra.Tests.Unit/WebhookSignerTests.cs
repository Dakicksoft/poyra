using Poyra.Modules.Webhooks.Infrastructure;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

public sealed class WebhookSignerTests
{
    private const string Secret = "whsec_test_sirri";
    private const string Payload = """{"payment_id":"pay_abc","amount_minor":14900}""";

    [Fact]
    public void Imza_uret_ve_dogrula_gidis_donusu()
    {
        var header = WebhookSigner.BuildHeader(Secret, 1_754_000_000, Payload);

        header.ShouldStartWith("t=1754000000,v1=");
        WebhookSigner.Verify(Secret, header, Payload).ShouldBeTrue();
    }

    [Fact]
    public void Kurcalanmis_govde_imzayi_dusurmeli()
    {
        var header = WebhookSigner.BuildHeader(Secret, 1_754_000_000, Payload);

        WebhookSigner.Verify(Secret, header, Payload.Replace("14900", "1")).ShouldBeFalse();
        WebhookSigner.Verify("yanlis-sir", header, Payload).ShouldBeFalse();
    }

    [Fact]
    public void Tolerans_penceresi_disindaki_zaman_damgasi_reddedilmeli()
    {
        var header = WebhookSigner.BuildHeader(Secret, unixTime: 1_000, Payload);

        WebhookSigner.Verify(Secret, header, Payload, toleranceSeconds: 300, nowUnix: 10_000).ShouldBeFalse();
        WebhookSigner.Verify(Secret, header, Payload, toleranceSeconds: 300, nowUnix: 1_100).ShouldBeTrue();
    }

    [Fact]
    public void Bozuk_baslik_reddedilmeli()
    {
        WebhookSigner.Verify(Secret, "sacma", Payload).ShouldBeFalse();
        WebhookSigner.Verify(Secret, "t=abc,v1=00", Payload).ShouldBeFalse();
    }
}

public sealed class WebhookRetryPolicyTests
{
    [Fact]
    public void Toplam_deneme_hakki_7_olmali()
        => WebhookRetryPolicy.MaxAttempts.ShouldBe(7);

    [Fact]
    public void Gecikmeler_artan_sirada_ilerlemeli_ve_hak_bitince_null()
    {
        WebhookRetryPolicy.NextDelay(1).ShouldBe(TimeSpan.FromMinutes(1));
        WebhookRetryPolicy.NextDelay(2).ShouldBe(TimeSpan.FromMinutes(5));
        WebhookRetryPolicy.NextDelay(6).ShouldBe(TimeSpan.FromHours(24));
        WebhookRetryPolicy.NextDelay(7).ShouldBeNull(); // hak bitti → Exhausted
        WebhookRetryPolicy.NextDelay(99).ShouldBeNull();
    }
}
