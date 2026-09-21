namespace Arkana.Gateway.Api.Tests.Auth;

using System.Security.Claims;
using Arkana.Domain.Interfaces;
using Arkana.Gateway.Api.Auth;
using Arkana.Gateway.Api.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class TenantCircuitHandlerTests
{
    private static readonly Guid TenantOne = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid TenantTwo = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static AuthenticationState Anonymous()
        => new(new ClaimsPrincipal(new ClaimsIdentity()));

    private static AuthenticationState Authenticated(string tenantClaim)
        => new(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "someone@example.com"), new Claim("TenantId", tenantClaim)],
            "test")));

    private static (TenantCircuitHandler Handler, RecordingTenantAccessor Tenant) Sut(
        IConfiguration config, AuthenticationState state)
    {
        var tenant = new RecordingTenantAccessor();
        var handler = new TenantCircuitHandler(
            new StubAuthStateProvider(state),
            new DashboardTenantResolver(config),
            tenant,
            NullLogger<TenantCircuitHandler>.Instance);
        return (handler, tenant);
    }

    [Fact]
    public async Task PinTenantAsync_pins_the_admin_tenant_for_the_open_dashboard()
    {
        // AUTH=0: no authenticated principal — the circuit still needs the tenant
        // the dashboard is operated against, or every tenant-scoped panel stalls.
        var config = Config(("AUTH", "0"), ("ADMIN_TENANT_ID", TenantOne.ToString()));
        var (handler, tenant) = Sut(config, Anonymous());

        await handler.PinTenantAsync("circuit-1");

        tenant.TenantId.Should().Be(TenantOne);
    }

    [Fact]
    public async Task PinTenantAsync_pins_the_authenticated_claim_tenant()
    {
        var config = Config(("AUTH", "1"), ("ADMIN_TENANT_ID", TenantTwo.ToString()));
        var (handler, tenant) = Sut(config, Authenticated(TenantTwo.ToString()));

        await handler.PinTenantAsync("circuit-2");

        tenant.TenantId.Should().Be(TenantTwo);
    }

    [Fact]
    public async Task PinTenantAsync_leaves_the_circuit_tenant_unset_when_auth_is_enabled_and_anonymous()
    {
        var config = Config(("AUTH", "1"), ("ADMIN_TENANT_ID", TenantOne.ToString()));
        var (handler, tenant) = Sut(config, Anonymous());

        await handler.PinTenantAsync("circuit-3");

        tenant.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task PinTenantAsync_survives_a_failing_auth_state_provider()
    {
        var config = Config(("AUTH", "0"), ("ADMIN_TENANT_ID", TenantOne.ToString()));
        var tenant = new RecordingTenantAccessor();
        var handler = new TenantCircuitHandler(
            new ThrowingAuthStateProvider(),
            new DashboardTenantResolver(config),
            tenant,
            NullLogger<TenantCircuitHandler>.Instance);

        var act = () => handler.PinTenantAsync("circuit-4");

        await act.Should().NotThrowAsync();
        tenant.TenantId.Should().BeNull();
    }

    private sealed class StubAuthStateProvider(AuthenticationState state) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(state);
    }

    private sealed class ThrowingAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => throw new InvalidOperationException("boom");
    }

    private sealed class RecordingTenantAccessor : ITenantContextAccessor
    {
        public Guid? TenantId { get; private set; }

        public void SetTenant(Guid tenantId) => TenantId = tenantId;

        public void ClearTenant() => TenantId = null;
    }
}
