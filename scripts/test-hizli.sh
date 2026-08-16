#!/bin/sh
# Hızlı geri bildirim döngüsü: Docker İSTEMEZ.
# Birim testleri + mimari/PCI bekçileri — geliştirirken bunu koşun.
#
#   ./scripts/test-hizli.sh                 # hepsi
#   ./scripts/test-hizli.sh --filter Totp   # ek argümanlar dotnet test'e geçer
#
# Entegrasyon (Testcontainers/Postgres) ve kapsam raporu için: ./scripts/test-kapsam.sh
set -e
cd "$(dirname "$0")/.."

for proje in tests/Poyra.Tests.Unit tests/Poyra.Tests.Architecture; do
  echo "── $proje"
  dotnet test "$proje" --nologo "$@"
done
