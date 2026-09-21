-- Schema baseline for a database created from scratch.
--
-- The migration history in src/Arkana.Infrastructure/Migrations cannot reproduce the
-- production schema on a fresh database: migrations were rewritten after they had been
-- applied, and at least one table (OAuthPendingFlows) has no creating migration left in the
-- repository, so a from-scratch run aborts partway. This dump is the ground truth taken from
-- the running production database: schema only, public schema only, no data, no owners and no
-- privileges. It includes the (empty) __EFMigrationsHistory table; deploy/db/init-baseline.sh
-- fills that table with baseline-migrations.txt so EF only applies migrations written after
-- this baseline. Existing databases are untouched: the initializer no-ops as soon as a
-- migration history exists. Regenerate with:
--
--   docker exec -i arkana-postgres pg_dump -U arkana -d arkana --schema-only \
--     --no-owner --no-privileges --schema=public \
--     | grep -v "^\\\(un\)\?restrict"
--
-- then drop pg_dump guard lines (\restrict / \unrestrict) and relax the schema statement:
--   sed -i 's/^CREATE SCHEMA public;$/CREATE SCHEMA IF NOT EXISTS public;/' baseline-schema.sql

--
-- PostgreSQL database dump
--


-- Dumped from database version 16.14
-- Dumped by pg_dump version 16.14

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

--
-- Name: public; Type: SCHEMA; Schema: -; Owner: -
--

CREATE SCHEMA IF NOT EXISTS public;


--
-- Name: SCHEMA public; Type: COMMENT; Schema: -; Owner: -
--

COMMENT ON SCHEMA public IS 'standard public schema';


SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: AccountUsageSnapshots; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."AccountUsageSnapshots" (
    "Id" uuid NOT NULL,
    "AccountProviderId" uuid NOT NULL,
    "AccountCode" character varying(64) NOT NULL,
    "WindowKind" integer NOT NULL,
    "UsedPercent" double precision NOT NULL,
    "WindowMinutes" integer,
    "ResetsAtUtc" timestamp with time zone,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    "PlanType" character varying(64)
);


--
-- Name: AgentDefinitions; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."AgentDefinitions" (
    "Id" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "Name" character varying(128) NOT NULL,
    "Description" character varying(1024) NOT NULL,
    "SystemPrompt" text NOT NULL,
    "ModelCode" character varying(64) NOT NULL,
    "MaxTokens" integer NOT NULL,
    "Temperature" numeric NOT NULL,
    "IsActive" boolean NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "Metadata" text
);


--
-- Name: AgentTasks; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."AgentTasks" (
    "Id" uuid NOT NULL,
    "AgentId" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "Status" integer NOT NULL,
    "Input" text NOT NULL,
    "Output" text,
    "ErrorMessage" text,
    "StartedAt" timestamp with time zone,
    "CompletedAt" timestamp with time zone,
    "DurationMs" bigint,
    "TokenUsed" integer,
    "ParentTaskId" uuid,
    "CreatedAt" timestamp with time zone NOT NULL
);


--
-- Name: AiProviders; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."AiProviders" (
    "Id" uuid NOT NULL,
    "Name" character varying(128) NOT NULL,
    "Code" character varying(64) NOT NULL,
    "BaseUrl" text,
    "ApiKey" character varying(1024),
    "Priority" integer NOT NULL,
    "IsEnabled" boolean NOT NULL,
    "MaxTokensPerRequest" integer,
    "CostPerInputToken" numeric NOT NULL,
    "CostPerOutputToken" numeric NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "AuthMethod" integer DEFAULT 0 NOT NULL,
    "OAuthConfigId" uuid,
    "TenantId" uuid DEFAULT '00000000-0000-0000-0000-000000000001'::uuid NOT NULL,
    "AccountId" text
);


--
-- Name: ApiKeyModels; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."ApiKeyModels" (
    "AllowedByKeysId" uuid NOT NULL,
    "AllowedModelsId" uuid NOT NULL
);


--
-- Name: ApiKeyPoolEntries; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."ApiKeyPoolEntries" (
    "Id" uuid NOT NULL,
    "PoolId" uuid NOT NULL,
    "SealedKey" character varying(2048) NOT NULL,
    "Label" character varying(128) NOT NULL,
    "Priority" integer NOT NULL,
    "IsActive" boolean NOT NULL,
    "RateLimitedAt" timestamp with time zone,
    "CooldownSeconds" integer NOT NULL,
    "ConsecutiveRateLimits" integer NOT NULL,
    "RequestCount" bigint NOT NULL,
    "RateLimitCount" bigint NOT NULL,
    "IsPermanentlyDisabled" boolean NOT NULL,
    "LastErrorType" text,
    "AllowedModels" text,
    "CreatedAt" timestamp with time zone NOT NULL
);


--
-- Name: ApiKeyPools; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."ApiKeyPools" (
    "Id" uuid NOT NULL,
    "AiProviderId" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "ActiveIndex" integer NOT NULL,
    "TotalRequests" bigint NOT NULL,
    "TotalRateLimits" bigint NOT NULL,
    "IsActive" boolean NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL
);


--
-- Name: ApiKeys; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."ApiKeys" (
    "Id" uuid NOT NULL,
    "KeyHash" character varying(128) NOT NULL,
    "KeyPrefix" character varying(12) NOT NULL,
    "Name" character varying(256) NOT NULL,
    "IsActive" boolean NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "ExpiresAt" timestamp with time zone,
    "RateLimitRpm" integer,
    "RateLimitTpm" integer,
    "RateLimitMaxConcurrent" integer,
    "TenantId" uuid NOT NULL,
    "PreferredProviderCode" character varying(64),
    "OwnerUserId" uuid,
    "AllowProviderFallback" boolean DEFAULT false NOT NULL,
    "AccountRoutingMode" integer DEFAULT 0 NOT NULL,
    "AllowAccountFallback" boolean DEFAULT false NOT NULL,
    "PreferredProviderAccountId" uuid
);


--
-- Name: DashboardUsers; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."DashboardUsers" (
    "Id" uuid NOT NULL,
    "Username" character varying(128) NOT NULL,
    "PasswordHash" character varying(256) NOT NULL,
    "Role" character varying(64) DEFAULT 'Admin'::character varying NOT NULL,
    "Email" character varying(256),
    "IsActive" boolean NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "LastLoginAt" timestamp with time zone,
    "LoginCount" integer NOT NULL,
    "TenantId" uuid NOT NULL
);


--
-- Name: GlobalRateLimits; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."GlobalRateLimits" (
    "Id" uuid NOT NULL,
    "Enabled" boolean DEFAULT true NOT NULL,
    "DefaultRequestsPerMinute" integer DEFAULT 60 NOT NULL,
    "DefaultTokensPerMinute" integer DEFAULT 1000000 NOT NULL,
    "DefaultMaxConcurrent" integer DEFAULT 5 NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL
);


--
-- Name: Models; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Models" (
    "Id" uuid NOT NULL,
    "ProviderId" uuid NOT NULL,
    "Name" character varying(128) NOT NULL,
    "Code" character varying(64) NOT NULL,
    "IsEnabled" boolean NOT NULL,
    "CostPerInputToken" numeric(20,10) NOT NULL,
    "CostPerOutputToken" numeric(20,10) NOT NULL,
    "MaxTokensPerRequest" integer,
    "CreatedAt" timestamp with time zone NOT NULL,
    "TenantId" uuid DEFAULT '00000000-0000-0000-0000-000000000001'::uuid NOT NULL
);


--
-- Name: OAuthPendingFlows; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OAuthPendingFlows" (
    "Id" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "ProviderCode" character varying(64) NOT NULL,
    "State" character varying(128) NOT NULL,
    "SealedCodeVerifier" character varying(4096),
    "RedirectUri" character varying(1024) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "ExpiresAt" timestamp with time zone NOT NULL,
    "AiProviderCode" character varying(64),
    "DeviceAuthId" text,
    "DeviceUserCode" text,
    "ClaimedAt" timestamp with time zone,
    "SealedAuthorizationCode" character varying(4096),
    "CompletionExpiresAt" timestamp with time zone,
    "CompletionRefreshExpiresAt" timestamp with time zone,
    "CompletionTokenType" character varying(32),
    "SealedCompletionAccessToken" character varying(4096),
    "SealedCompletionRefreshToken" character varying(4096),
    "CompletionClaimedAt" timestamp with time zone,
    "CompletionFailure" character varying(256),
    "CompletionFailedAt" timestamp with time zone
);


--
-- Name: OAuthProviderConfigs; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."OAuthProviderConfigs" (
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
    "ExtraAuthParams" text
);


--
-- Name: Plans; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Plans" (
    "Id" uuid NOT NULL,
    "Name" character varying(128) NOT NULL,
    "Slug" character varying(64) NOT NULL,
    "MonthlyPrice" numeric(20,4) NOT NULL,
    "IncludedInputTokens" bigint NOT NULL,
    "IncludedOutputTokens" bigint NOT NULL,
    "MaxRequestsPerMinute" integer NOT NULL,
    "MaxTokensPerMinute" integer NOT NULL,
    "MaxConcurrent" integer NOT NULL,
    "MaxApiKeys" integer NOT NULL,
    "Features" text,
    "IsActive" boolean NOT NULL
);


--
-- Name: PolicyTemplates; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."PolicyTemplates" (
    "Id" uuid NOT NULL,
    "Name" character varying(128) NOT NULL,
    "Slug" character varying(128) NOT NULL,
    "Description" character varying(1024) NOT NULL,
    "Config" text NOT NULL,
    "IsActive" boolean NOT NULL,
    "IsBuiltin" boolean NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL
);


--
-- Name: ProviderAccountOperations; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."ProviderAccountOperations" (
    "Id" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "ProviderAccountId" uuid NOT NULL,
    "Kind" integer NOT NULL,
    "State" integer NOT NULL,
    "Nonce" character varying(64) NOT NULL,
    "ExpiresAt" timestamp with time zone NOT NULL,
    "Slot" character varying(256) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    "AuthorizationUrl" character varying(2048),
    "IdempotencyKey" character varying(128) DEFAULT ''::character varying NOT NULL,
    "RequestFingerprint" character varying(64) DEFAULT ''::character varying NOT NULL,
    "ExpectedVersion" uuid,
    "ResultJson" text,
    "AuditActor" character varying(256),
    "FailureClass" character varying(128),
    "OperationGroup" character varying(32) DEFAULT ''::character varying NOT NULL,
    "BrokerInstanceId" character varying(128),
    "BrokerKind" character varying(64),
    "MetadataJson" character varying(2048),
    "StableCredentialId" character varying(256)
);


--
-- Name: ProviderAccounts; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."ProviderAccounts" (
    "Id" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "AiProviderId" uuid NOT NULL,
    "Code" character varying(64) NOT NULL,
    "DisplayName" character varying(256) NOT NULL,
    "AuthOwnership" integer NOT NULL,
    "BrokerKind" integer,
    "BrokerCredentialId" character varying(256),
    "ExternalCredentialFileName" character varying(256),
    "BrokerInstanceId" character varying(128),
    "AuthDirectoryKey" character varying(128),
    "RoutingPrefix" character varying(128),
    "IsEnabled" boolean NOT NULL,
    "ConnectionStatus" integer NOT NULL,
    "CooldownUntil" timestamp with time zone,
    "LastSuccessAt" timestamp with time zone,
    "LastFailureAt" timestamp with time zone,
    "LastFailureClass" character varying(128),
    "LastBrokerSyncAt" timestamp with time zone,
    "TokenExpiresAt" timestamp with time zone,
    "Version" uuid NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    "DeletedAt" timestamp with time zone,
    "AuditActor" character varying(256),
    "SupportedModels" character varying(2048),
    "BrokerInstanceIdNormalized" character varying(128) GENERATED ALWAYS AS (lower(("BrokerInstanceId")::text)) STORED
);


--
-- Name: ProviderOAuthTokens; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."ProviderOAuthTokens" (
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
    "Label" character varying(128),
    "Version" uuid DEFAULT '00000000-0000-0000-0000-000000000000'::uuid NOT NULL,
    "CompletionFlowId" uuid
);


--
-- Name: RequestLogs; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."RequestLogs" (
    "Id" uuid NOT NULL,
    "Provider" character varying(64) NOT NULL,
    "Model" character varying(128) NOT NULL,
    "ApiKeyName" character varying(256),
    "MessagesJson" jsonb NOT NULL,
    "ResponseContent" text,
    "ToolCallsJson" jsonb,
    "InputTokens" integer NOT NULL,
    "OutputTokens" integer NOT NULL,
    "Cost" numeric(20,10) NOT NULL,
    "DurationTicks" bigint NOT NULL,
    "Timestamp" timestamp with time zone NOT NULL,
    "IsError" boolean NOT NULL,
    "ErrorMessage" text,
    "TenantId" uuid NOT NULL,
    "ViaMitmAgent" text,
    "UpstreamStatus" integer,
    "RequestedProviderAccountCode" character varying(64),
    "RequestedProviderAccountId" uuid,
    "RequestedProviderCode" character varying(64),
    "ResolvedProviderAccountCode" character varying(64),
    "ResolvedProviderAccountId" uuid,
    "RouteKind" character varying(32) DEFAULT 'legacy'::character varying NOT NULL
);


--
-- Name: SlaMetrics; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."SlaMetrics" (
    "Id" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "ProviderCode" character varying(64) NOT NULL,
    "ModelCode" character varying(64) NOT NULL,
    "AvgLatencyMs" double precision DEFAULT 0 NOT NULL,
    "P50LatencyMs" double precision DEFAULT 0 NOT NULL,
    "P95LatencyMs" double precision DEFAULT 0 NOT NULL,
    "P99LatencyMs" double precision DEFAULT 0 NOT NULL,
    "MaxLatencyMs" double precision DEFAULT 0 NOT NULL,
    "TotalRequests" bigint DEFAULT 0 NOT NULL,
    "SuccessfulRequests" bigint DEFAULT 0 NOT NULL,
    "FailedRequests" bigint DEFAULT 0 NOT NULL,
    "ErrorRate" double precision DEFAULT 0 NOT NULL,
    "ConsecutiveFailures" integer DEFAULT 0 NOT NULL,
    "LastFailureAt" timestamp with time zone,
    "UptimePercent" double precision DEFAULT 100 NOT NULL,
    "IsHealthy" boolean DEFAULT true NOT NULL,
    "LastHealthCheckAt" timestamp with time zone NOT NULL,
    "WindowStart" timestamp with time zone NOT NULL,
    "WindowEnd" timestamp with time zone NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL
);


--
-- Name: TenantBudgets; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."TenantBudgets" (
    "TenantId" uuid NOT NULL,
    "MonthlyInputTokenCap" bigint NOT NULL,
    "MonthlyOutputTokenCap" bigint NOT NULL,
    "RemainingInputTokens" bigint NOT NULL,
    "RemainingOutputTokens" bigint NOT NULL,
    "PeriodStart" timestamp with time zone NOT NULL,
    "PeriodEnd" timestamp with time zone NOT NULL,
    "RowVersion" bigint DEFAULT 0 NOT NULL
);


--
-- Name: TenantPlans; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."TenantPlans" (
    "TenantId" uuid NOT NULL,
    "PlanId" uuid NOT NULL,
    "StartsAt" timestamp with time zone NOT NULL,
    "EndsAt" timestamp with time zone,
    "OverrideIncludedInputTokens" bigint,
    "OverrideIncludedOutputTokens" bigint,
    "OverrideMaxRpm" integer,
    "OverrideMaxTpm" integer,
    "OverrideMaxConcurrent" integer,
    "OverrideMaxApiKeys" integer
);


--
-- Name: Tenants; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Tenants" (
    "Id" uuid NOT NULL,
    "Name" character varying(256) NOT NULL,
    "Slug" character varying(128) NOT NULL,
    "IsActive" boolean NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "Settings" text
);


--
-- Name: TokenUsages; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."TokenUsages" (
    "Id" uuid NOT NULL,
    "Provider" character varying(64) NOT NULL,
    "Model" character varying(128) NOT NULL,
    "InputTokens" integer NOT NULL,
    "OutputTokens" integer NOT NULL,
    "Cost" numeric(20,10) NOT NULL,
    "DurationTicks" bigint NOT NULL,
    "Timestamp" timestamp with time zone NOT NULL,
    "ApiKeyName" character varying(256),
    "TenantId" uuid NOT NULL
);


--
-- Name: Webhooks; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."Webhooks" (
    "Id" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "Url" character varying(1024) NOT NULL,
    "Secret" character varying(256) NOT NULL,
    "Events" text NOT NULL,
    "IsActive" boolean NOT NULL,
    "RetryCount" integer DEFAULT 3 NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "LastTriggeredAt" timestamp with time zone,
    "FailureCount" integer NOT NULL
);


--
-- Name: __EFMigrationsHistory; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public."__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL
);


--
-- Name: AiProviders AK_AiProviders_TenantId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AiProviders"
    ADD CONSTRAINT "AK_AiProviders_TenantId_Id" UNIQUE ("TenantId", "Id");


--
-- Name: ProviderAccounts AK_ProviderAccounts_TenantId_Id; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ProviderAccounts"
    ADD CONSTRAINT "AK_ProviderAccounts_TenantId_Id" UNIQUE ("TenantId", "Id");


--
-- Name: AccountUsageSnapshots PK_AccountUsageSnapshots; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AccountUsageSnapshots"
    ADD CONSTRAINT "PK_AccountUsageSnapshots" PRIMARY KEY ("Id");


--
-- Name: AgentDefinitions PK_AgentDefinitions; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AgentDefinitions"
    ADD CONSTRAINT "PK_AgentDefinitions" PRIMARY KEY ("Id");


--
-- Name: AgentTasks PK_AgentTasks; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AgentTasks"
    ADD CONSTRAINT "PK_AgentTasks" PRIMARY KEY ("Id");


--
-- Name: AiProviders PK_AiProviders; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AiProviders"
    ADD CONSTRAINT "PK_AiProviders" PRIMARY KEY ("Id");


--
-- Name: ApiKeyModels PK_ApiKeyModels; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ApiKeyModels"
    ADD CONSTRAINT "PK_ApiKeyModels" PRIMARY KEY ("AllowedByKeysId", "AllowedModelsId");


--
-- Name: ApiKeyPoolEntries PK_ApiKeyPoolEntries; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ApiKeyPoolEntries"
    ADD CONSTRAINT "PK_ApiKeyPoolEntries" PRIMARY KEY ("Id");


--
-- Name: ApiKeyPools PK_ApiKeyPools; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ApiKeyPools"
    ADD CONSTRAINT "PK_ApiKeyPools" PRIMARY KEY ("Id");


--
-- Name: ApiKeys PK_ApiKeys; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ApiKeys"
    ADD CONSTRAINT "PK_ApiKeys" PRIMARY KEY ("Id");


--
-- Name: DashboardUsers PK_DashboardUsers; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DashboardUsers"
    ADD CONSTRAINT "PK_DashboardUsers" PRIMARY KEY ("Id");


--
-- Name: GlobalRateLimits PK_GlobalRateLimits; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."GlobalRateLimits"
    ADD CONSTRAINT "PK_GlobalRateLimits" PRIMARY KEY ("Id");


--
-- Name: Models PK_Models; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Models"
    ADD CONSTRAINT "PK_Models" PRIMARY KEY ("Id");


--
-- Name: OAuthPendingFlows PK_OAuthPendingFlows; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OAuthPendingFlows"
    ADD CONSTRAINT "PK_OAuthPendingFlows" PRIMARY KEY ("Id");


--
-- Name: OAuthProviderConfigs PK_OAuthProviderConfigs; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OAuthProviderConfigs"
    ADD CONSTRAINT "PK_OAuthProviderConfigs" PRIMARY KEY ("Id");


--
-- Name: Plans PK_Plans; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Plans"
    ADD CONSTRAINT "PK_Plans" PRIMARY KEY ("Id");


--
-- Name: PolicyTemplates PK_PolicyTemplates; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."PolicyTemplates"
    ADD CONSTRAINT "PK_PolicyTemplates" PRIMARY KEY ("Id");


--
-- Name: ProviderAccountOperations PK_ProviderAccountOperations; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ProviderAccountOperations"
    ADD CONSTRAINT "PK_ProviderAccountOperations" PRIMARY KEY ("Id");


--
-- Name: ProviderAccounts PK_ProviderAccounts; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ProviderAccounts"
    ADD CONSTRAINT "PK_ProviderAccounts" PRIMARY KEY ("Id");


--
-- Name: ProviderOAuthTokens PK_ProviderOAuthTokens; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ProviderOAuthTokens"
    ADD CONSTRAINT "PK_ProviderOAuthTokens" PRIMARY KEY ("Id");


--
-- Name: RequestLogs PK_RequestLogs; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RequestLogs"
    ADD CONSTRAINT "PK_RequestLogs" PRIMARY KEY ("Id");


--
-- Name: SlaMetrics PK_SlaMetrics; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SlaMetrics"
    ADD CONSTRAINT "PK_SlaMetrics" PRIMARY KEY ("Id");


--
-- Name: TenantBudgets PK_TenantBudgets; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."TenantBudgets"
    ADD CONSTRAINT "PK_TenantBudgets" PRIMARY KEY ("TenantId");


--
-- Name: TenantPlans PK_TenantPlans; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."TenantPlans"
    ADD CONSTRAINT "PK_TenantPlans" PRIMARY KEY ("TenantId");


--
-- Name: Tenants PK_Tenants; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Tenants"
    ADD CONSTRAINT "PK_Tenants" PRIMARY KEY ("Id");


--
-- Name: TokenUsages PK_TokenUsages; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."TokenUsages"
    ADD CONSTRAINT "PK_TokenUsages" PRIMARY KEY ("Id");


--
-- Name: Webhooks PK_Webhooks; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Webhooks"
    ADD CONSTRAINT "PK_Webhooks" PRIMARY KEY ("Id");


--
-- Name: __EFMigrationsHistory PK___EFMigrationsHistory; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."__EFMigrationsHistory"
    ADD CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId");


--
-- Name: IX_AccountUsageSnapshots_AccountCode; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_AccountUsageSnapshots_AccountCode" ON public."AccountUsageSnapshots" USING btree ("AccountCode");


--
-- Name: IX_AccountUsageSnapshots_AccountProviderId_WindowKind; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "IX_AccountUsageSnapshots_AccountProviderId_WindowKind" ON public."AccountUsageSnapshots" USING btree ("AccountProviderId", "WindowKind");


--
-- Name: IX_AgentDefinitions_TenantId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_AgentDefinitions_TenantId" ON public."AgentDefinitions" USING btree ("TenantId");


--
-- Name: IX_AgentTasks_AgentId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_AgentTasks_AgentId" ON public."AgentTasks" USING btree ("AgentId");


--
-- Name: IX_AgentTasks_Status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_AgentTasks_Status" ON public."AgentTasks" USING btree ("Status");


--
-- Name: IX_AgentTasks_TenantId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_AgentTasks_TenantId" ON public."AgentTasks" USING btree ("TenantId");


--
-- Name: IX_AiProviders_Code; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "IX_AiProviders_Code" ON public."AiProviders" USING btree ("Code");


--
-- Name: IX_AiProviders_OAuthConfigId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_AiProviders_OAuthConfigId" ON public."AiProviders" USING btree ("OAuthConfigId");


--
-- Name: IX_ApiKeyModels_AllowedModelsId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_ApiKeyModels_AllowedModelsId" ON public."ApiKeyModels" USING btree ("AllowedModelsId");


--
-- Name: IX_ApiKeyPoolEntries_PoolId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_ApiKeyPoolEntries_PoolId" ON public."ApiKeyPoolEntries" USING btree ("PoolId");


--
-- Name: IX_ApiKeyPools_AiProviderId_TenantId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "IX_ApiKeyPools_AiProviderId_TenantId" ON public."ApiKeyPools" USING btree ("AiProviderId", "TenantId");


--
-- Name: IX_ApiKeyPools_TenantId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_ApiKeyPools_TenantId" ON public."ApiKeyPools" USING btree ("TenantId");


--
-- Name: IX_ApiKeys_KeyHash; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "IX_ApiKeys_KeyHash" ON public."ApiKeys" USING btree ("KeyHash");


--
-- Name: IX_ApiKeys_OwnerUserId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_ApiKeys_OwnerUserId" ON public."ApiKeys" USING btree ("OwnerUserId");


--
-- Name: IX_ApiKeys_PreferredProviderAccountId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_ApiKeys_PreferredProviderAccountId" ON public."ApiKeys" USING btree ("PreferredProviderAccountId");


--
-- Name: IX_ApiKeys_TenantId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_ApiKeys_TenantId" ON public."ApiKeys" USING btree ("TenantId");


--
-- Name: IX_DashboardUsers_TenantId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_DashboardUsers_TenantId" ON public."DashboardUsers" USING btree ("TenantId");


--
-- Name: IX_DashboardUsers_Username; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "IX_DashboardUsers_Username" ON public."DashboardUsers" USING btree ("Username");


--
-- Name: IX_Models_ProviderId_Code; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "IX_Models_ProviderId_Code" ON public."Models" USING btree ("ProviderId", "Code");


--
-- Name: IX_OAuthPendingFlows_State; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "IX_OAuthPendingFlows_State" ON public."OAuthPendingFlows" USING btree ("State");


--
-- Name: IX_OAuthPendingFlows_TenantId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_OAuthPendingFlows_TenantId" ON public."OAuthPendingFlows" USING btree ("TenantId");


--
-- Name: IX_Plans_Slug; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "IX_Plans_Slug" ON public."Plans" USING btree ("Slug");


--
-- Name: IX_PolicyTemplates_IsActive; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_PolicyTemplates_IsActive" ON public."PolicyTemplates" USING btree ("IsActive");


--
-- Name: IX_PolicyTemplates_Slug; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "IX_PolicyTemplates_Slug" ON public."PolicyTemplates" USING btree ("Slug");


--
-- Name: IX_ProviderAccountOperations_Nonce; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "IX_ProviderAccountOperations_Nonce" ON public."ProviderAccountOperations" USING btree ("Nonce");


--
-- Name: IX_ProviderAccountOperations_TenantId_ProviderAccountId_Kind; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "IX_ProviderAccountOperations_TenantId_ProviderAccountId_Kind" ON public."ProviderAccountOperations" USING btree ("TenantId", "ProviderAccountId", "Kind") WHERE ("State" = ANY (ARRAY[0, 1]));


--
-- Name: IX_ProviderAccountOperations_TenantId_ProviderAccountId_Kind_I~; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "IX_ProviderAccountOperations_TenantId_ProviderAccountId_Kind_I~" ON public."ProviderAccountOperations" USING btree ("TenantId", "ProviderAccountId", "Kind", "IdempotencyKey");


--
-- Name: IX_ProviderAccounts_AiProviderId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_ProviderAccounts_AiProviderId" ON public."ProviderAccounts" USING btree ("AiProviderId");


--
-- Name: IX_ProviderAccounts_BrokerInstanceIdNormalized; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "IX_ProviderAccounts_BrokerInstanceIdNormalized" ON public."ProviderAccounts" USING btree ("BrokerInstanceIdNormalized") WHERE (("BrokerInstanceId" IS NOT NULL) AND ("DeletedAt" IS NULL));


--
-- Name: IX_ProviderAccounts_TenantId_AiProviderId_IsEnabled_Connection~; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_ProviderAccounts_TenantId_AiProviderId_IsEnabled_Connection~" ON public."ProviderAccounts" USING btree ("TenantId", "AiProviderId", "IsEnabled", "ConnectionStatus");


--
-- Name: IX_ProviderAccounts_TenantId_Code; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "IX_ProviderAccounts_TenantId_Code" ON public."ProviderAccounts" USING btree ("TenantId", "Code");


--
-- Name: IX_ProviderOAuthTokens_TenantId_AiProviderId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "IX_ProviderOAuthTokens_TenantId_AiProviderId" ON public."ProviderOAuthTokens" USING btree ("TenantId", "AiProviderId");


--
-- Name: IX_RequestLogs_Model; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_RequestLogs_Model" ON public."RequestLogs" USING btree ("Model");


--
-- Name: IX_RequestLogs_Provider; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_RequestLogs_Provider" ON public."RequestLogs" USING btree ("Provider");


--
-- Name: IX_RequestLogs_TenantId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_RequestLogs_TenantId" ON public."RequestLogs" USING btree ("TenantId");


--
-- Name: IX_RequestLogs_TenantId_Provider_Timestamp; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_RequestLogs_TenantId_Provider_Timestamp" ON public."RequestLogs" USING btree ("TenantId", "Provider", "Timestamp");


--
-- Name: IX_RequestLogs_TenantId_RequestedProviderAccountId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_RequestLogs_TenantId_RequestedProviderAccountId" ON public."RequestLogs" USING btree ("TenantId", "RequestedProviderAccountId");


--
-- Name: IX_RequestLogs_TenantId_ResolvedProviderAccountId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_RequestLogs_TenantId_ResolvedProviderAccountId" ON public."RequestLogs" USING btree ("TenantId", "ResolvedProviderAccountId");


--
-- Name: IX_RequestLogs_Timestamp; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_RequestLogs_Timestamp" ON public."RequestLogs" USING btree ("Timestamp");


--
-- Name: IX_SlaMetrics_ProviderCode_ModelCode_TenantId; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "IX_SlaMetrics_ProviderCode_ModelCode_TenantId" ON public."SlaMetrics" USING btree ("ProviderCode", "ModelCode", "TenantId");


--
-- Name: IX_SlaMetrics_TenantId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_SlaMetrics_TenantId" ON public."SlaMetrics" USING btree ("TenantId");


--
-- Name: IX_TenantPlans_PlanId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_TenantPlans_PlanId" ON public."TenantPlans" USING btree ("PlanId");


--
-- Name: IX_Tenants_Slug; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX "IX_Tenants_Slug" ON public."Tenants" USING btree ("Slug");


--
-- Name: IX_TokenUsages_Model; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_TokenUsages_Model" ON public."TokenUsages" USING btree ("Model");


--
-- Name: IX_TokenUsages_Provider; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_TokenUsages_Provider" ON public."TokenUsages" USING btree ("Provider");


--
-- Name: IX_TokenUsages_TenantId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_TokenUsages_TenantId" ON public."TokenUsages" USING btree ("TenantId");


--
-- Name: IX_TokenUsages_Timestamp; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_TokenUsages_Timestamp" ON public."TokenUsages" USING btree ("Timestamp");


--
-- Name: IX_Webhooks_TenantId; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX "IX_Webhooks_TenantId" ON public."Webhooks" USING btree ("TenantId");


--
-- Name: AgentTasks FK_AgentTasks_AgentDefinitions_AgentId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AgentTasks"
    ADD CONSTRAINT "FK_AgentTasks_AgentDefinitions_AgentId" FOREIGN KEY ("AgentId") REFERENCES public."AgentDefinitions"("Id") ON DELETE CASCADE;


--
-- Name: AiProviders FK_AiProviders_OAuthProviderConfigs_OAuthConfigId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."AiProviders"
    ADD CONSTRAINT "FK_AiProviders_OAuthProviderConfigs_OAuthConfigId" FOREIGN KEY ("OAuthConfigId") REFERENCES public."OAuthProviderConfigs"("Id") ON DELETE RESTRICT;


--
-- Name: ApiKeyModels FK_ApiKeyModels_ApiKeys_AllowedByKeysId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ApiKeyModels"
    ADD CONSTRAINT "FK_ApiKeyModels_ApiKeys_AllowedByKeysId" FOREIGN KEY ("AllowedByKeysId") REFERENCES public."ApiKeys"("Id") ON DELETE CASCADE;


--
-- Name: ApiKeyModels FK_ApiKeyModels_Models_AllowedModelsId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ApiKeyModels"
    ADD CONSTRAINT "FK_ApiKeyModels_Models_AllowedModelsId" FOREIGN KEY ("AllowedModelsId") REFERENCES public."Models"("Id") ON DELETE CASCADE;


--
-- Name: ApiKeyPoolEntries FK_ApiKeyPoolEntries_ApiKeyPools_PoolId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ApiKeyPoolEntries"
    ADD CONSTRAINT "FK_ApiKeyPoolEntries_ApiKeyPools_PoolId" FOREIGN KEY ("PoolId") REFERENCES public."ApiKeyPools"("Id") ON DELETE CASCADE;


--
-- Name: ApiKeyPools FK_ApiKeyPools_AiProviders_AiProviderId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ApiKeyPools"
    ADD CONSTRAINT "FK_ApiKeyPools_AiProviders_AiProviderId" FOREIGN KEY ("AiProviderId") REFERENCES public."AiProviders"("Id") ON DELETE CASCADE;


--
-- Name: ApiKeyPools FK_ApiKeyPools_Tenants_TenantId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ApiKeyPools"
    ADD CONSTRAINT "FK_ApiKeyPools_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES public."Tenants"("Id") ON DELETE RESTRICT;


--
-- Name: ApiKeys FK_ApiKeys_DashboardUsers_OwnerUserId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ApiKeys"
    ADD CONSTRAINT "FK_ApiKeys_DashboardUsers_OwnerUserId" FOREIGN KEY ("OwnerUserId") REFERENCES public."DashboardUsers"("Id") ON DELETE SET NULL;


--
-- Name: ApiKeys FK_ApiKeys_Tenants_TenantId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ApiKeys"
    ADD CONSTRAINT "FK_ApiKeys_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES public."Tenants"("Id") ON DELETE RESTRICT;


--
-- Name: DashboardUsers FK_DashboardUsers_Tenants_TenantId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."DashboardUsers"
    ADD CONSTRAINT "FK_DashboardUsers_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES public."Tenants"("Id") ON DELETE RESTRICT;


--
-- Name: Models FK_Models_AiProviders_ProviderId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Models"
    ADD CONSTRAINT "FK_Models_AiProviders_ProviderId" FOREIGN KEY ("ProviderId") REFERENCES public."AiProviders"("Id") ON DELETE CASCADE;


--
-- Name: OAuthPendingFlows FK_OAuthPendingFlows_Tenants_TenantId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."OAuthPendingFlows"
    ADD CONSTRAINT "FK_OAuthPendingFlows_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES public."Tenants"("Id") ON DELETE CASCADE;


--
-- Name: ProviderAccountOperations FK_ProviderAccountOperations_ProviderAccounts_TenantAccount; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ProviderAccountOperations"
    ADD CONSTRAINT "FK_ProviderAccountOperations_ProviderAccounts_TenantAccount" FOREIGN KEY ("TenantId", "ProviderAccountId") REFERENCES public."ProviderAccounts"("TenantId", "Id") ON DELETE RESTRICT;


--
-- Name: ProviderAccounts FK_ProviderAccounts_AiProviders_AiProviderId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ProviderAccounts"
    ADD CONSTRAINT "FK_ProviderAccounts_AiProviders_AiProviderId" FOREIGN KEY ("AiProviderId") REFERENCES public."AiProviders"("Id") ON DELETE RESTRICT;


--
-- Name: ProviderAccounts FK_ProviderAccounts_Tenants_TenantId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ProviderAccounts"
    ADD CONSTRAINT "FK_ProviderAccounts_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES public."Tenants"("Id") ON DELETE RESTRICT;


--
-- Name: ProviderOAuthTokens FK_ProviderOAuthTokens_AiProviders_TenantId_AiProviderId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ProviderOAuthTokens"
    ADD CONSTRAINT "FK_ProviderOAuthTokens_AiProviders_TenantId_AiProviderId" FOREIGN KEY ("TenantId", "AiProviderId") REFERENCES public."AiProviders"("TenantId", "Id") ON DELETE CASCADE;


--
-- Name: ProviderOAuthTokens FK_ProviderOAuthTokens_Tenants_TenantId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."ProviderOAuthTokens"
    ADD CONSTRAINT "FK_ProviderOAuthTokens_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES public."Tenants"("Id") ON DELETE RESTRICT;


--
-- Name: RequestLogs FK_RequestLogs_ProviderAccounts_TenantId_RequestedProviderAcco~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RequestLogs"
    ADD CONSTRAINT "FK_RequestLogs_ProviderAccounts_TenantId_RequestedProviderAcco~" FOREIGN KEY ("TenantId", "RequestedProviderAccountId") REFERENCES public."ProviderAccounts"("TenantId", "Id");


--
-- Name: RequestLogs FK_RequestLogs_ProviderAccounts_TenantId_ResolvedProviderAccou~; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."RequestLogs"
    ADD CONSTRAINT "FK_RequestLogs_ProviderAccounts_TenantId_ResolvedProviderAccou~" FOREIGN KEY ("TenantId", "ResolvedProviderAccountId") REFERENCES public."ProviderAccounts"("TenantId", "Id");


--
-- Name: SlaMetrics FK_SlaMetrics_Tenants_TenantId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."SlaMetrics"
    ADD CONSTRAINT "FK_SlaMetrics_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES public."Tenants"("Id") ON DELETE CASCADE;


--
-- Name: TenantBudgets FK_TenantBudgets_Tenants_TenantId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."TenantBudgets"
    ADD CONSTRAINT "FK_TenantBudgets_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES public."Tenants"("Id") ON DELETE CASCADE;


--
-- Name: TenantPlans FK_TenantPlans_Plans_PlanId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."TenantPlans"
    ADD CONSTRAINT "FK_TenantPlans_Plans_PlanId" FOREIGN KEY ("PlanId") REFERENCES public."Plans"("Id") ON DELETE RESTRICT;


--
-- Name: TenantPlans FK_TenantPlans_Tenants_TenantId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."TenantPlans"
    ADD CONSTRAINT "FK_TenantPlans_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES public."Tenants"("Id") ON DELETE RESTRICT;


--
-- Name: Webhooks FK_Webhooks_Tenants_TenantId; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public."Webhooks"
    ADD CONSTRAINT "FK_Webhooks_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES public."Tenants"("Id") ON DELETE CASCADE;


--
-- PostgreSQL database dump complete
--


