namespace Arkana.Gateway.Api.Tests.Endpoints;

using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Xunit;

public sealed partial class AdminEndpointsTests
{
    [Fact]
    public async Task ChatGptAccounts_CreateThenList_ReturnsAccount()
    {
        using var host = await CreateAdminHost();
        var client = host.GetTestClient();

        var create = await client.PostAsync("/admin/chatgpt/accounts", null);
        create.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await client.GetAsync("/admin/chatgpt/accounts");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await list.Content.ReadAsStringAsync();
        body.Should().Contain("chatgpt-acc1");
    }

    [Fact]
    public async Task ChatGptAccounts_CreateThenRemove_ReturnsNotFoundAfterRemoval()
    {
        using var host = await CreateAdminHost();
        var client = host.GetTestClient();

        await client.PostAsync("/admin/chatgpt/accounts", null);

        var remove = await client.DeleteAsync("/admin/chatgpt/accounts/chatgpt-acc1");
        remove.StatusCode.Should().Be(HttpStatusCode.OK);

        var removeAgain = await client.DeleteAsync("/admin/chatgpt/accounts/chatgpt-acc1");
        removeAgain.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ChatGptAccounts_Toggle_FlipsEnabled()
    {
        using var host = await CreateAdminHost();
        var client = host.GetTestClient();

        await client.PostAsync("/admin/chatgpt/accounts", null);

        var toggle = await client.PutAsync("/admin/chatgpt/accounts/chatgpt-acc1/toggle", null);
        toggle.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
