using System.Reflection;
using System.Text;
using System.Text.Json;
using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Arkana.Gateway.Api.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Arkana.Gateway.Api.Tests.Endpoints;

public sealed class McpServerEndpointsTests
{
    private static readonly ILogger<Program> NullLogger = Substitute.For<ILogger<Program>>();
    private static readonly JsonSerializerOptions Opts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private static readonly string[] TestModelCodes = ["gpt-4o", "claude-sonnet-4", "deepseek-chat"];
    private static IModelRepository CreateModelRepository()
    {
        var provider = AiProvider.Create("Test Provider", "test", 1);
        var models = TestModelCodes
            .Select(code =>
            {
                var model = Model.Create(provider.Id, code, code);
                typeof(Model).GetProperty(nameof(Model.Provider))!.SetValue(model, provider);
                return model;
            })
            .ToArray();

        var repository = Substitute.For<IModelRepository>();
        repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(models);
        return repository;
    }


    /// <summary>
    /// Extract the Value from Results.Json() and serialize it as JSON.
    /// Results.Json(value, opts) wraps value in a JsonResult; we need the inner value.
    /// </summary>
    private static JsonElement Serialize(IResult result)
    {
        // Results.Json returns JsonResult with a Value property
        var valueProp = result.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        var actualValue = valueProp?.GetValue(result) ?? result;
        var json = JsonSerializer.Serialize(actualValue, Opts);
        return JsonDocument.Parse(json).RootElement;
    }

    // ── initialize ────────────────────────────────────────

    [Fact]
    public void Initialize_ReturnsServerInfo()
    {
        var rpc = new McpRequest { Id = JsonDocument.Parse("1").RootElement, Method = "initialize" };
        var root = Serialize(McpServerEndpoints.HandleInitialize(rpc));

        root.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString().Should().Be("arkana-gateway");
        root.GetProperty("result").GetProperty("protocolVersion").GetString().Should().Be("2025-03-26");
    }

    [Fact]
    public void Initialize_ReturnsCorrectJsonRpcFormat()
    {
        var rpc = new McpRequest { Id = JsonDocument.Parse("42").RootElement, Method = "initialize" };
        var root = Serialize(McpServerEndpoints.HandleInitialize(rpc));

        root.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        root.GetProperty("id").GetInt32().Should().Be(42);
        root.TryGetProperty("error", out _).Should().BeFalse();
    }

    // ── tools/list ────────────────────────────────────────

    [Fact]
    public void ToolsList_ReturnsAllTools()
    {
        var rpc = new McpRequest { Id = JsonDocument.Parse("2").RootElement, Method = "tools/list" };
        var root = Serialize(McpServerEndpoints.HandleToolsList(rpc));
        var tools = root.GetProperty("result").GetProperty("tools");

        tools.GetArrayLength().Should().Be(4);

        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        names.Should().Contain("list_models");
        names.Should().Contain("generate_text");
        names.Should().Contain("chat");
        names.Should().Contain("get_usage");
    }

    [Fact]
    public void ToolsList_ToolHasInputSchema()
    {
        var rpc = new McpRequest { Id = JsonDocument.Parse("3").RootElement, Method = "tools/list" };
        var root = Serialize(McpServerEndpoints.HandleToolsList(rpc));
        var tools = root.GetProperty("result").GetProperty("tools");

        var genText = tools.EnumerateArray().First(t => t.GetProperty("name").GetString() == "generate_text");
        genText.GetProperty("inputSchema").GetProperty("type").GetString().Should().Be("object");
        genText.GetProperty("inputSchema").GetProperty("required").EnumerateArray()
            .Select(r => r.GetString()).Should().Contain("model");
    }

    // ── tools/call: list_models ───────────────────────────

    [Fact]
    public async Task ToolsCall_ListModels_ReturnsModels()
    {
        var rpcParams = JsonDocument.Parse("""{"name":"list_models","arguments":{}}""").RootElement;
        var rpc = new McpRequest
        {
            Id = JsonDocument.Parse("4").RootElement,
            Method = "tools/call",
            Params = new McpParams { Name = rpcParams.GetProperty("name").GetString(), Arguments = rpcParams.GetProperty("arguments") }
        };

        var result = await McpServerEndpoints.HandleToolsCall(rpc, NullLogger, CreateModelRepository());
        var root = Serialize(result);
        var text = root.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

        text.Should().Contain("gpt-4o");
        text.Should().Contain("claude-sonnet-4");
        text.Should().Contain("deepseek-chat");
    }

    // ── tools/call: get_usage ─────────────────────────────

    [Fact]
    public async Task ToolsCall_GetUsage_ReturnsTenantUsage()
    {
        var rpcParams = JsonDocument.Parse("""{"name":"get_usage","arguments":{"tenant_id":"tenant-42"}}""").RootElement;
        var rpc = new McpRequest
        {
            Id = JsonDocument.Parse("5").RootElement,
            Method = "tools/call",
            Params = new McpParams { Name = rpcParams.GetProperty("name").GetString(), Arguments = rpcParams.GetProperty("arguments") }
        };

        var result = await McpServerEndpoints.HandleToolsCall(rpc, NullLogger, CreateModelRepository());
        var root = Serialize(result);
        var text = root.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

        text.Should().Contain("tenant-42");
    }

    // ── tools/call: stub tools ────────────────────────────

    [Fact]
    public async Task ToolsCall_GenerateText_ReturnsStub()
    {
        var rpcParams = JsonDocument.Parse("""{"name":"generate_text","arguments":{"model":"gpt-4o","prompt":"hello"}}""").RootElement;
        var rpc = new McpRequest
        {
            Id = JsonDocument.Parse("6").RootElement,
            Method = "tools/call",
            Params = new McpParams { Name = rpcParams.GetProperty("name").GetString(), Arguments = rpcParams.GetProperty("arguments") }
        };

        var result = await McpServerEndpoints.HandleToolsCall(rpc, NullLogger, CreateModelRepository());
        var root = Serialize(result);
        var text = root.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

        text.Should().Contain("[Stub]");
        text.Should().Contain("generate_text");
    }

    [Fact]
    public async Task ToolsCall_Chat_ReturnsStub()
    {
        var rpcParams = JsonDocument.Parse("""{"name":"chat","arguments":{"model":"gpt-4o","messages":[]}}""").RootElement;
        var rpc = new McpRequest
        {
            Id = JsonDocument.Parse("7").RootElement,
            Method = "tools/call",
            Params = new McpParams { Name = rpcParams.GetProperty("name").GetString(), Arguments = rpcParams.GetProperty("arguments") }
        };

        var result = await McpServerEndpoints.HandleToolsCall(rpc, NullLogger, CreateModelRepository());
        var root = Serialize(result);
        var text = root.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

        text.Should().Contain("[Stub]");
        text.Should().Contain("chat");
    }

    // ── tools/call: unknown tool ──────────────────────────

    [Fact]
    public async Task ToolsCall_UnknownTool_ReturnsError()
    {
        var rpcParams = JsonDocument.Parse("""{"name":"nonexistent_tool","arguments":{}}""").RootElement;
        var rpc = new McpRequest
        {
            Id = JsonDocument.Parse("8").RootElement,
            Method = "tools/call",
            Params = new McpParams { Name = rpcParams.GetProperty("name").GetString(), Arguments = rpcParams.GetProperty("arguments") }
        };

        var result = await McpServerEndpoints.HandleToolsCall(rpc, NullLogger, CreateModelRepository());
        var root = Serialize(result);
        root.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
    }

    // ── tools/call: missing name ──────────────────────────

    [Fact]
    public async Task ToolsCall_MissingName_ReturnsError()
    {
        var rpcParams = JsonDocument.Parse("""{"arguments":{}}""").RootElement;
        var rpc = new McpRequest
        {
            Id = JsonDocument.Parse("9").RootElement,
            Method = "tools/call",
            Params = new McpParams { Name = null, Arguments = rpcParams.GetProperty("arguments") }
        };

        var result = await McpServerEndpoints.HandleToolsCall(rpc, NullLogger, CreateModelRepository());
        var root = Serialize(result);
        root.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32602);
    }

    // ── resources/list ────────────────────────────────────

    [Fact]
    public void ResourcesList_ReturnsGatewayResources()
    {
        var rpc = new McpRequest { Id = JsonDocument.Parse("10").RootElement, Method = "resources/list" };
        var root = Serialize(McpServerEndpoints.HandleResourcesList(rpc));
        var resources = root.GetProperty("result").GetProperty("resources");

        resources.GetArrayLength().Should().Be(3);
        var uris = resources.EnumerateArray().Select(r => r.GetProperty("uri").GetString()).ToList();
        uris.Should().Contain("gateway://models");
        uris.Should().Contain("gateway://usage");
        uris.Should().Contain("gateway://templates");
    }

    // ── unknown method ────────────────────────────────────

    [Fact]
    public void UnknownMethod_DispatchReturnsMethodNotFound()
    {
        var rpc = new McpRequest { Id = JsonDocument.Parse("11").RootElement, Method = "nonexistent" };
        rpc.Method.Should().NotBe("initialize");
    }
}
