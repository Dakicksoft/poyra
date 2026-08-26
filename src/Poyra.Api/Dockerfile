FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Sürüm numarası dışarıdan gelir (CI: release etiketi). Verilmezse
# Directory.Build.props'taki geliştirme değeri kullanılır.
ARG VERSION=

COPY Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet restore src/Poyra.Api/Poyra.Api.csproj
RUN dotnet publish src/Poyra.Api/Poyra.Api.csproj -c Release -o /app --no-restore \
    $([ -n "$VERSION" ] && echo "-p:Version=$VERSION")

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# wget YALNIZ healthcheck için. aspnet imajında ne wget ne curl vardır; HTTP yoklaması
# yapacak bir araç olmadan healthcheck her denemede başarısız olur ve konteyner kalıcı
# olarak "unhealthy" görünür — sessiz bir tuzak. curl yerine wget: aynı işi 15 paket
# yerine 2 pakette (~1 MB) görüyor.
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget \
    && rm -rf /var/lib/apt/lists/*

USER app

COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1
EXPOSE 8080

# Yoklama imajın İÇİNDE: compose, Dokploy, Swarm — her tüketici aynı tanımı yeniden
# yazmak zorunda kalmasın. /health/ready veritabanı bağlantısını da doğrular.
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=5 \
    CMD wget -qO- http://localhost:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "Poyra.Api.dll"]
