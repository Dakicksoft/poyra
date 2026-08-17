#!/bin/sh
# Üretimde uygulama rolünün parolası ORTAM DEĞİŞKENİNDEN gelir.
#
# 01-app-role.sql rolü sabit bir geliştirme parolasıyla oluşturur (o dosya testlerde de
# aynen koşuyor). Burada, POYRA_APP_PASSWORD tanımlıysa parola gerçek değerle değiştirilir.
# Tanımlı değilse hiçbir şey yapılmaz — geliştirme kurulumu bozulmaz.
#
# Parola komut satırına yazılmaz (ps çıktısında görünürdü); psql'e değişkenle geçilir.
#
# DİKKAT — burada `exit` KULLANILMAZ: Postgres entrypoint'i betiği çalıştırılabilir
# bitine göre ya çalıştırır ya `source` eder. Source edildiğinde `exit`, init sürecinin
# TAMAMINI sonlandırır ve sunucu hiç açılmaz (Aspire `WithInitFiles` ile kopyalanan
# dosyalarda exec biti korunmaz — sessizce bu tuzağa düşülür). Akış if/else ile kurulur.

if [ -z "$POYRA_APP_PASSWORD" ]; then
    echo "POYRA_APP_PASSWORD tanımlı değil — poyra_app geliştirme parolasıyla kalıyor."
else
    psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
         -v app_password="$POYRA_APP_PASSWORD" <<'SQL'
ALTER ROLE poyra_app WITH PASSWORD :'app_password';
SQL

    echo "poyra_app parolası ortam değişkeninden ayarlandı."
fi
