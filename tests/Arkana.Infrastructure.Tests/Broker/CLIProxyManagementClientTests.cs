using System.Net;
using System.Text;
using Arkana.Infrastructure.Broker;

namespace Arkana.Infrastructure.Tests.Broker;

public sealed class CLIProxyManagementClientTests
{
    [Fact]
    public async Task Start_oauth_uses_management_route_and_maps_only_safe_fields()
    {
        var handler = new TestHandler(_ => Json("{\"url\":\"https://accounts.google.test/authorize?code=redacted\"}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://broker.test/") };
        var client = new CLIProxyManagementClient(http, Options(), "slot-a");
        var result = await client.StartOAuthAsync();
        handler.Request.Method.Should().Be(HttpMethod.Get);
        handler.Request.RequestUri!.AbsolutePath.Should().Be("/v0/management/antigravity-auth-url");
        handler.Request.RequestUri.Query.Should().Be("?is_webui=true");
        result.AuthorizationUrl.Should().Contain("accounts.google.test");
    }

    [Fact]
    public async Task Start_oauth_rejects_loopback_authorization_url()
    {
        var handler = new TestHandler(_ => Json("{\"url\":\"http://localhost:51121/oauth-callback\"}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://broker.test/") };
        var client = new CLIProxyManagementClient(http, Options(), "slot-a");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartOAuthAsync());

        exception.Message.Should().Contain("server HTTPS callback");
    }

    [Fact]
    public async Task Submit_oauth_callback_posts_state_to_broker_management_handler()
    {
        var handler = new TestHandler(_ => Json("{\"status\":\"ok\"}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://broker.test/") };
        var client = new CLIProxyManagementClient(http, Options(), "slot-a");

        var result = await client.SubmitOAuthCallbackAsync("state-123", "code-redacted", null);

        result.Disposition.Should().Be(CLIProxyOAuthCallbackDisposition.Accepted);
        handler.Request.Method.Should().Be(HttpMethod.Post);
        handler.Request.RequestUri!.AbsolutePath.Should().Be("/v0/management/oauth-callback");
        var body = handler.Body;
        body.Should().Contain("provider");
        body.Should().Contain("state-123");
        body.Should().Contain("code-redacted");
    }

    [Fact]
    public async Task Submit_oauth_callback_maps_broker_conflict_to_already_processed()
    {
        var handler = new TestHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://broker.test/") };
        var client = new CLIProxyManagementClient(http, Options(), "slot-a");

        var result = await client.SubmitOAuthCallbackAsync("state-123", "code-redacted", null);

        result.Disposition.Should().Be(CLIProxyOAuthCallbackDisposition.AlreadyProcessed);
    }

    [Fact]
    public async Task Delete_requires_stable_identifier_and_reads_back_after_mutation()
    {
        var calls = 0;
        var handler = new TestHandler(request =>
        {
            calls++;
            if (request.Method == HttpMethod.Delete) return Json("{}");
            return Json("{\"files\":[]}");
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://broker.test/") };
        var client = new CLIProxyManagementClient(http, Options(), "slot-a");
        await client.DeleteAsync("auth-1");
        handler.Request.Method.Should().Be(HttpMethod.Get);
        calls.Should().Be(2);
        await Assert.ThrowsAsync<ArgumentException>(() => client.DeleteAsync("../auth-file"));
    }

    private static CLIProxyManagementOptions Options() => new() { MaxResponseBytes = 4096, Slots = new() { ["slot-a"] = new() { BaseUrl = "https://broker.test/", ManagementKey = "management-test-key" } } };
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage Request { get; private set; } = null!;
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }
}
