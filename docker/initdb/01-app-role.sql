-- Poyra çalışma zamanı rolü.
-- İki katmanlı izolasyonun (İlke 4) B katmanı bu role dayanır:
--   * poyra      : tablo sahibi, migration'ları koşar (postgres imajında superuser).
--   * poyra_app  : uygulamanın bağlandığı rol — SUPERUSER DEĞİL, BYPASSRLS YOK.
--     RLS politikaları yalnız bu rol üzerinden anlamlıdır; uygulama asla sahip rolle bağlanmaz.
-- Bu betik migration'lardan ÖNCE koşar; tablo yetkileri ALTER DEFAULT PRIVILEGES ile
-- migration sırasında oluşturulacak tablolara otomatik uygulanır.
-- (Testcontainers fikstürü aynı betiği kod içinden koşar: tests/Poyra.Tests.Integration/PostgresFixture.cs)

CREATE ROLE poyra_app LOGIN PASSWORD 'poyra_app_pw'
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;

GRANT USAGE ON SCHEMA public TO poyra_app;

ALTER DEFAULT PRIVILEGES FOR ROLE poyra IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO poyra_app;

ALTER DEFAULT PRIVILEGES FOR ROLE poyra IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO poyra_app;

-- Not: payment_events / payment_intents üzerindeki UPDATE/DELETE yetkileri
-- "hiçbir operasyonel kayıt silinmez" ilkesi gereği RLS migration'ında geri alınır.
