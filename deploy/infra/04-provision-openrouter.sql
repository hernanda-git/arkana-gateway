-- Provision the OpenRouter provider + one free default model into the gateway DB.
-- Idempotent: safe to re-run on every deploy (ON CONFLICT DO NOTHING).
--
-- WHY THIS IS REQUIRED (not just an env var):
--   GATEWAY_DEFAULT_MODEL is resolved through the catalog as a Model.Code, which
--   must belong to an AiProviders row. Without the openrouter provider + model row,
--   a caller that omits `model` falls through to the dead OpenCode upstream (the
--   original SEC bug this gateway was patched for). The docker-compose.yml default
--   (google/gemma-4-26b-a4b-it:free) only works once these rows exist.
--
-- The provider key is intentionally LEFT EMPTY here. The OpenRouter connector reads
-- OPENROUTER_API_KEY from the environment (passed through docker-compose.yml from
-- deploy/.env) as a fallback when the sealed DB key is empty. Do NOT seal the key in
-- this SQL — the vault only runs in-process. Set OPENROUTER_API_KEY in deploy/.env.
--
-- Run against the gateway Postgres (same DB the compose stack uses):
--   docker compose -f deploy/docker-compose.yml exec -T postgres psql -U arkana -d arkana -f - < deploy/infra/04-provision-openrouter.sql
-- (or:  psql "$DATABASE_URL" -f deploy/infra/04-provision-openrouter.sql)
--
-- To switch the free default model, change GATEWAY_DEFAULT_MODEL in docker-compose.yml
-- AND update MODEL_CODE / MODEL_NAME below to the same slug, then re-run.

\set provider_id 'f1a2b3c4-0000-4000-8000-0000000000a1'
\set default_model_code 'google/gemma-4-26b-a4b-it:free'
\set default_model_name 'Google Gemma 4 26B (OpenRouter, free)'

-- 1) Provider row (Code is UNIQUE).
INSERT INTO "AiProviders" ("Id", "Name", "Code", "BaseUrl", "ApiKey", "AuthMethod", "Priority", "CostPerInputToken", "CostPerOutputToken", "IsEnabled", "CreatedAt")
VALUES (
    :'provider_id'::uuid,
    'OpenRouter',
    'openrouter',
    'https://openrouter.ai/api/v1',
    NULL,                 -- key sourced from OPENROUTER_API_KEY env (connector fallback)
    0,                    -- AuthMethod.ApiKey (enum: 0=ApiKey, 1=OAuth)
    100,                  -- low priority: used as a healthy fallback, not primary
    0,                    -- CostPerInputToken (required, not-null)
    0,                    -- CostPerOutputToken (required, not-null)
    true,
    now()
)
ON CONFLICT ("Code") DO UPDATE
    SET "BaseUrl"  = EXCLUDED."BaseUrl",
        "IsEnabled" = EXCLUDED."IsEnabled",
        "Priority"  = EXCLUDED."Priority",
        "CostPerInputToken"  = EXCLUDED."CostPerInputToken",
        "CostPerOutputToken" = EXCLUDED."CostPerOutputToken"
WHERE "AiProviders"."Code" = 'openrouter';

-- 2) Free default model row (unique on (ProviderId, Code)).
INSERT INTO "Models" ("Id", "ProviderId", "Name", "Code", "IsEnabled", "CostPerInputToken", "CostPerOutputToken", "MaxTokensPerRequest", "CreatedAt")
VALUES (
    gen_random_uuid(),
    :'provider_id'::uuid,
    :'default_model_name',
    :'default_model_code',
    true,
    0,
    0,
    NULL,
    now()
)
ON CONFLICT ("ProviderId", "Code") DO UPDATE
    SET "IsEnabled" = EXCLUDED."IsEnabled",
        "Name"      = EXCLUDED."Name"
WHERE "Models"."ProviderId" = :'provider_id'::uuid
  AND "Models"."Code"       = :'default_model_code';

-- 3) Verify.
SELECT p."Code" AS provider, p."BaseUrl", p."IsEnabled",
       m."Code" AS model_code, m."IsEnabled" AS model_enabled
FROM "AiProviders" p
LEFT JOIN "Models" m ON m."ProviderId" = p."Id"
WHERE p."Code" = 'openrouter';
