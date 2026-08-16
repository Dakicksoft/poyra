using System.Net;
using Poyra.SharedKernel.Messaging;

namespace Poyra.Modules.Tenancy.Infrastructure;

public static class EmailTemplates
{
    public static EmailMessage PasswordReset(string toEmail, string displayName, string link, TimeSpan lifetime)
    {
        const string subject = "Poyra parola sıfırlama";
        var minutes = (int)lifetime.TotalMinutes;

        var text = $"""
            Merhaba {displayName},

            Poyra hesabınızın parolasını sıfırlamak için aşağıdaki bağlantıyı açın:
            {link}

            Bağlantı {minutes} dakika geçerlidir ve yalnız bir kez kullanılabilir.
            Bu isteği siz yapmadıysanız bu postayı yok sayın — parolanız değişmez.

            Poyra
            """;

        return new EmailMessage(null, toEmail, subject, Layout(subject, $"""
            <p>Merhaba {Esc(displayName)},</p>
            <p>Poyra hesabınızın parolasını sıfırlamak için düğmeye basın:</p>
            <p><a class="btn" href="{Esc(link)}">Parolamı sıfırla</a></p>
            <p class="hint">Bağlantı <strong>{minutes} dakika</strong> geçerlidir ve yalnız bir kez kullanılabilir.</p>
            <p class="hint">Düğme çalışmazsa: <span class="mono">{Esc(link)}</span></p>
            <p class="hint">Bu isteği siz yapmadıysanız bu postayı yok sayın — parolanız değişmez.</p>
            """), text, "password_reset");
    }

    public static EmailMessage EmailVerification(string toEmail, string displayName, string link, TimeSpan lifetime)
    {
        const string subject = "Poyra e-posta doğrulama";
        var days = (int)lifetime.TotalDays;

        var text = $"""
            Merhaba {displayName},

            Poyra hesabınızın e-posta adresini doğrulamak için aşağıdaki bağlantıyı açın:
            {link}

            Bağlantı {days} gün geçerlidir.
            Bu isteği siz yapmadıysanız bu postayı yok sayın.

            Poyra
            """;

        return new EmailMessage(null, toEmail, subject, Layout(subject, $"""
            <p>Merhaba {Esc(displayName)},</p>
            <p>Poyra hesabınızın e-posta adresini doğrulayın:</p>
            <p><a class="btn" href="{Esc(link)}">Adresimi doğrula</a></p>
            <p class="hint">Bağlantı <strong>{days} gün</strong> geçerlidir.</p>
            <p class="hint">Düğme çalışmazsa: <span class="mono">{Esc(link)}</span></p>
            <p class="hint">Bu isteği siz yapmadıysanız bu postayı yok sayın.</p>
            """), text, "email_verification");
    }

    public static EmailMessage PasswordChanged(string toEmail, string displayName)
    {
        const string subject = "Poyra parolanız değişti";

        var text = $"""
            Merhaba {displayName},

            Poyra hesabınızın parolası az önce değiştirildi ve açık tüm oturumlar kapatıldı.
            Bu işlemi siz yapmadıysanız hemen parolanızı sıfırlayın ve bize ulaşın.

            Poyra
            """;

        return new EmailMessage(null, toEmail, subject, Layout(subject, $"""
            <p>Merhaba {Esc(displayName)},</p>
            <p>Poyra hesabınızın parolası az önce değiştirildi ve <strong>açık tüm oturumlar kapatıldı</strong>.</p>
            <p class="hint">Bu işlemi siz yapmadıysanız hemen parolanızı sıfırlayın ve bize ulaşın.</p>
            """), text, "password_changed");
    }

    private static string Esc(string value) => WebUtility.HtmlEncode(value);

    private static string Layout(string title, string body) => $$"""
        <!DOCTYPE html>
        <html lang="tr"><head><meta charset="utf-8" />
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        <title>{{Esc(title)}}</title></head>
        <body style="margin:0;background:#F7F5F2;font-family:-apple-system,Segoe UI,Roboto,sans-serif;color:#1A1A1A">
          <div style="max-width:520px;margin:0 auto;padding:32px 24px">
            <div style="font-size:20px;letter-spacing:.08em;color:#0B1220;margin-bottom:24px">poyra</div>
            <div style="background:#FFFFFF;border:1px solid #E6E1DA;border-radius:12px;padding:24px;line-height:1.6">
              {{body}}
            </div>
            <p style="color:#8A8A8A;font-size:12px;margin-top:20px">
              Poyra — ödeme orkestrasyonu. Bu bir işlem postasıdır.
            </p>
          </div>
          <style>
            .btn { display:inline-block;background:#C4713B;color:#FFF8F2 !important;text-decoration:none;
                   padding:12px 22px;border-radius:8px;font-weight:600 }
            .hint { color:#6B6B6B;font-size:13px }
            .mono { font-family:ui-monospace,Menlo,monospace;font-size:12px;word-break:break-all }
          </style>
        </body></html>
        """;
}
