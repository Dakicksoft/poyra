#!/bin/sh
# Poyra üretim anahtarlarını üretir. Çıktıyı .env dosyanıza ekleyin:
#   ./scripts/anahtar-uret.sh >> .env
#
# Her anahtar 32 rastgele bayttır (AES-256). Kasa anahtarı diğerlerinden AYRI üretilir:
# konnektör sırrı sızarsa kart zarfı açılmasın diye.
set -e

key() { openssl rand -base64 32; }

echo "POSTGRES_PASSWORD=$(openssl rand -base64 24 | tr -d '/+=')"
echo "POYRA_APP_PASSWORD=$(openssl rand -base64 24 | tr -d '/+=')"
echo "POYRA_CREDENTIAL_KEY=$(key)"
echo "POYRA_JWT_KEY=$(key)"
echo "POYRA_VAULT_KEY=$(key)"
echo "POYRA_PLATFORM_ADMIN_KEY=$(openssl rand -hex 32)"
echo "PGWEB_PASSWORD=$(openssl rand -base64 24 | tr -d '/+=')"
# Demo giriş parolası — yalnız POYRA_DEMO=true iken kullanılır.
echo "POYRA_DEMO_PASSWORD=$(openssl rand -base64 18 | tr -d '/+=')"
