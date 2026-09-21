using Arkana.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Arkana.Infrastructure.Tests.Services;

public sealed class HttpContextTenantProviderTests
{
    [Fact]
    public void Missing_http_context_returns_null_instead_of_default_tenant()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);

        new HttpContextTenantProvider(accessor).TenantId.Should().BeNull();
    }

    [Fact]
    public void Api_key_tenant_is_returned_without_fallback()
    {
        var expected = Guid.NewGuid();
        var context = Substitute.For<HttpContext>();
        context.Items.Returns(new Dictionary<object, object?> { ["TenantId"] = expected });
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);

        new HttpContextTenantProvider(accessor).TenantId.Should().Be(expected);
    }

    [Fact]
    public void Circuit_tenant_can_be_initialized_without_http_context()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);
        var provider = new HttpContextTenantProvider(accessor);
        var expected = Guid.NewGuid();

        provider.SetTenant(expected);

        provider.TenantId.Should().Be(expected);
    }
}