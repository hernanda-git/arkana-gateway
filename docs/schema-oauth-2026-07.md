-- Authoritative OAuth schema, generated from EF model (dotnet ef dbcontext script).
-- Idempotent: drops the OAuth tables, recreates with EF-exact definitions.
-- Apply on the live Postgres (arkana-postgres) at deploy time, then restart
-- the gateway so EnsureCreatedAsync + OAuthConfigSeeder run.

DROP TABLE IF EXISTS "ProviderOAuthTokens";
DROP TABLE IF EXISTS "OAuthPendingFlows";
DROP TABLE IF EXISTS "OAuthProviderConfigs";

CREATE TABLE "OAuthProviderConfigs" (
    "Id" uuid NOT NULL,
    "ProviderCode" character varying(64) NOT NULL,
    "DisplayName" character varying(128) NOT NULL,
    "GrantType" integer NOT NULL,
    "AuthorizationEndpoint" character varying(1024),
    "TokenEndpoint" character varying(1024) NOT NULL,
    "DeviceAuthorizationEndpoint" character varying(1024),
    "ClientId" character varying(512) NOT NULL,
    "SealedClientSecret" character varying(4096),
    "Scopes" character varying(512),
    "ExtraAuthParams" text,
    CONSTRAINT "PK_OAuthProviderConfigs" PRIMARY KEY ("Id")
);

CREATE TABLE "ProviderOAuthTokens" (
    "Id" uuid NOT NULL,
    "AiProviderId" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "SealedAccessToken" character varying(4096),
    "SealedRefreshToken" character varying(4096),
    "TokenType" character varying(32) NOT NULL,
    "ExpiresAt" timestamp with time zone,
    "RefreshExpiresAt" timestamp with time zone,
    "Status" integer NOT NULL,
    "LastError" text,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_ProviderOAuthTokens" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_ProviderOAuthTokens_AiProviders_AiProviderId" FOREIGN KEY ("AiProviderId") REFERENCES "AiProviders" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_ProviderOAuthTokens_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "OAuthPendingFlows" (
    "Id" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "ProviderCode" character varying(64) NOT NULL,
    "State" character varying(128) NOT NULL,
    "SealedCodeVerifier" character varying(4096),
    "RedirectUri" character varying(1024) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "ExpiresAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_OAuthPendingFlows" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_OAuthPendingFlows_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX "IX_OAuthPendingFlows_State" ON "OAuthPendingFlows" ("State");
CREATE INDEX "IX_OAuthPendingFlows_TenantId" ON "OAuthPendingFlows" ("TenantId");

-- Ensure AiProviders carries the OAuth discriminator + FK (idempotent).
ALTER TABLE "AiProviders" ADD COLUMN IF NOT EXISTS "AuthMethod" integer NOT NULL DEFAULT 0;
ALTER TABLE "AiProviders" ADD COLUMN IF NOT EXISTS "OAuthConfigId" uuid;
ALTER TABLE "AiProviders" ADD CONSTRAINT "FK_AiProviders_OAuthProviderConfigs_OAuthConfigId"
    FOREIGN KEY ("OAuthConfigId") REFERENCES "OAuthProviderConfigs" ("Id") ON DELETE RESTRICT;
CREATE INDEX IF NOT EXISTS "IX_AiProviders_OAuthConfigId" ON "AiProviders" ("OAuthConfigId");
