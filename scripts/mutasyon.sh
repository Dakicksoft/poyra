#!/bin/sh
# Mutasyon testi (Stryker.NET): koda kasıtlı küçük hatalar enjekte eder ve
# testlerin bunları YAKALAYIP yakalamadığına bakar. Kapsam "kod çalıştı mı" der,
# mutasyon skoru "test gerçekten bir şey doğruluyor mu" der.
#
#   ./scripts/mutasyon.sh                       # seçili projeler
#   ./scripts/mutasyon.sh Poyra.Modules.Risk    # tek proje
#
# PAHALIDIR (proje başına dakikalar) — CI'da her PR'da değil, elle tetiklenir.
# Çıktı: artifacts/mutasyon/<proje>/mutation-report.html
set -e
cd "$(dirname "$0")/.."

# Neden bu dört proje: hepsi SAF KARAR mantığı ve hepsinde bir dal sessizce
# yanlış tarafa düşerse para hatası olur. Altyapı/CRUD projeleri kapsam dışı —
# oradaki mutasyonların çoğu anlamsız (log satırı, null kontrolü) ve gürültü üretir.
PROJELER="${*:-Poyra.Modules.Routing Poyra.Modules.Installments Poyra.Modules.Risk Poyra.Modules.Recon}"

dotnet tool restore >/dev/null
mkdir -p artifacts/mutasyon

for proje in $PROJELER; do
  yol=$(find src -name "$proje.csproj" -not -path "*/obj/*" | head -1)
  if [ -z "$yol" ]; then
    echo "HATA: $proje.csproj bulunamadı." >&2
    exit 1
  fi

  echo "── $proje"
  dotnet dotnet-stryker \
    --project "$proje.csproj" \
    --test-project tests/Poyra.Tests.Unit/Poyra.Tests.Unit.csproj \
    --output "artifacts/mutasyon/$proje" \
    --reporter html --reporter progress
done

echo
echo "Raporlar: artifacts/mutasyon/<proje>/reports/mutation-report.html"
