-- Schema-per-service layout (single database, shared tables + tenant_id).
-- EF Core migrations create tables inside these schemas from Phase 2 onward.
CREATE SCHEMA IF NOT EXISTS identity;
CREATE SCHEMA IF NOT EXISTS tenancy;
CREATE SCHEMA IF NOT EXISTS plugins;
CREATE SCHEMA IF NOT EXISTS cms;
CREATE SCHEMA IF NOT EXISTS media;
CREATE SCHEMA IF NOT EXISTS sites;
CREATE SCHEMA IF NOT EXISTS search;
CREATE SCHEMA IF NOT EXISTS analytics;
CREATE SCHEMA IF NOT EXISTS chat;
CREATE SCHEMA IF NOT EXISTS visitors;
CREATE SCHEMA IF NOT EXISTS forms;
CREATE SCHEMA IF NOT EXISTS ai;
CREATE SCHEMA IF NOT EXISTS audit;
CREATE SCHEMA IF NOT EXISTS social;
CREATE SCHEMA IF NOT EXISTS notifications;

-- Read-only reporting views for Grafana. Owned by the superuser, exposed to dcms_grafana
-- and to nobody else; see infra/postgres/init/03-observability-role.sh.
CREATE SCHEMA IF NOT EXISTS obs;

-- Sitewide search needs trigram matching for typeahead (Phase 11).
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- Query-level statistics for the Postgres dashboard. The extension also needs
-- shared_preload_libraries=pg_stat_statements, set on the postgres command line in
-- docker-compose.prod.yml — CREATE EXTENSION alone is not enough and fails without it.
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
