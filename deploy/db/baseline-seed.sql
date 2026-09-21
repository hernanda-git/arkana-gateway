-- Reference rows a created-from-scratch database needs before the gateway can start.
--
-- These are not tenant data: they are the rows the application's own bootstrap expects to find.
-- The migration history that would normally insert them cannot run on a fresh database (see the
-- header of baseline-schema.sql), so they are applied explicitly, right after the schema.
--
-- Keep this file to rows the gateway cannot boot without. The default tenant is one: the admin
-- seeder fails closed with "Default tenant is missing; cannot seed the admin user" without it.
-- Providers, models and API keys are deliberately NOT seeded here — add them from the dashboard
-- (/providers, /apikeys) or with deploy/infra/*.sql, so no production credential or tenant data
-- ever reaches a fresh install.

INSERT INTO public."Tenants" ("Id", "Name", "Slug", "IsActive", "CreatedAt", "Settings")
VALUES ('00000000-0000-0000-0000-000000000001', 'Default Tenant', 'default', TRUE,
        '2026-06-06 00:00:00+00', NULL)
ON CONFLICT ("Id") DO NOTHING;
