#!/bin/sh
# Tüm testler + kapsam raporu. Docker GEREKİR (Testcontainers, postgres:18-alpine).
#
#   ./scripts/test-kapsam.sh
#
# Çıktılar (artifacts/ .gitignore'da):
#   artifacts/kapsam/index.html   → tarayıcıda açılan detaylı rapor (satır satır)
#   artifacts/kapsam/Summary.txt  → özet, ekrana da basılır
set -e
cd "$(dirname "$0")/.."

rm -rf artifacts/kapsam artifacts/kapsam-ham

dotnet test --nologo \
  --settings coverlet.runsettings \
  --collect:"XPlat Code Coverage" \
  --results-directory artifacts/kapsam-ham "$@"

# vstest, bozuk runsettings'i sessizce yok sayıp 0 ile çıkabiliyor — kapsam
# dosyası üretilmediyse "yeşil" görünmesin.
if ! find artifacts/kapsam-ham -name 'coverage.cobertura.xml' | grep -q .; then
  echo "HATA: kapsam dosyası üretilmedi (coverlet.runsettings okunamamış olabilir)." >&2
  exit 1
fi

dotnet tool restore >/dev/null
dotnet reportgenerator \
  -reports:"artifacts/kapsam-ham/**/coverage.cobertura.xml" \
  -targetdir:artifacts/kapsam \
  -reporttypes:"Html;TextSummary;MarkdownSummaryGithub" \
  -verbosity:Warning

echo
cat artifacts/kapsam/Summary.txt
echo
echo "Detaylı rapor: artifacts/kapsam/index.html"

# Kapsam KAPISI (yalnız eşik verildiyse). Amaç yüksek hedef değil, GERİLEMEYİ yakalamak:
# testsiz kod eklendiğinde oran düşer ve PR kırmızıya döner.
#   KAPSAM_SATIR_ESIK=85 KAPSAM_DAL_ESIK=58 ./scripts/test-kapsam.sh
# "Branch coverage: 59.4% (3989 of 6714)" → 59.4 (parantezli kısım atılır)
oran() { sed -n "s/^  $1 coverage: *\([0-9.]*\)%.*/\1/p" artifacts/kapsam/Summary.txt | head -1; }

kapi() {
  ad="$1"; olculen="$2"; esik="$3"
  [ -z "$esik" ] && return 0
  if [ "$(printf '%s\n%s\n' "$esik" "$olculen" | sort -g | head -1)" != "$esik" ]; then
    echo "HATA: $ad kapsamı %$olculen — eşik %$esik altında." >&2
    return 1
  fi
  echo "$ad kapsamı %$olculen (eşik %$esik) ✓"
}

hata=0
kapi "Satır" "$(oran Line)" "${KAPSAM_SATIR_ESIK:-}" || hata=1
kapi "Dal" "$(oran Branch)" "${KAPSAM_DAL_ESIK:-}" || hata=1
exit "$hata"
