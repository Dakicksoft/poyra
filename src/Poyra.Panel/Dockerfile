# Çok aşamalı: derleme SDK ile, çalışma zamanı yalnız runtime imajıyla.
# Kaynak ve SDK üretim imajında BULUNMAZ — saldırı yüzeyi ve boyut ikisi de küçülür.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Önce yalnız proje dosyaları: bağımlılık katmanı kaynak değişince yeniden çözülmesin
COPY Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet restore src/Poyra.Panel/Poyra.Panel.csproj
RUN dotnet publish src/Poyra.Panel/Poyra.Panel.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Kök olmayan kullanıcı: .NET imajları hazır 'app' kullanıcısıyla gelir (UID 1654).
# Konteyner ele geçirilse bile host'a tırmanış zorlaşır.
USER app

COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1
EXPOSE 8080
ENTRYPOINT ["dotnet", "Poyra.Panel.dll"]
