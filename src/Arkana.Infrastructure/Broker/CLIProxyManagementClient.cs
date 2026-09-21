using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arkana.Infrastructure.Broker;

internal sealed class CLIProxyManagementClient : ICLIProxyManagementClient
{
    private readonly HttpClient _http;
    private readonly CLIProxyManagementOptions _options;
    private readonly string _slot;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public CLIProxyManagementClient(HttpClient http, CLIProxyManagementOptions options, string slot)
    { _http = http; _options = options; _slot = slot; }

    public async Task<CLIProxyOAuthStart> StartOAuthAsync(CancellationToken ct = default)
    {
        // The custom broker uses the configured HTTPS redirect and the stock
        // broker falls back to its local loopback callback when unset.
        using var request = new HttpRequestMessage(HttpMethod.Get, "v0/management/antigravity-auth-url?is_webui=true");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var response = await SendAsync(request, ct);
        var dto = await ReadAsync<AuthStartDto>(response, ct);
        if (string.IsNullOrWhiteSpace(dto.Url)) throw new InvalidOperationException("Broker OAuth start returned no authorization URL.");
        if (!Uri.TryCreate(dto.Url, UriKind.Absolute, out var authorizationUri))
            throw new InvalidOperationException("Broker OAuth start returned an invalid authorization URL.");
        if (authorizationUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || authorizationUri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || authorizationUri.Host.Equals("[::1]", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Antigravity broker is not configured with a server HTTPS callback.");
        return new(_slot, dto.Url, DateTimeOffset.UtcNow.AddMinutes(10));
    }

    public async Task<CLIProxyOAuthCallbackResult> SubmitOAuthCallbackAsync(string state, string? code, string? oauthError, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(state) || state.Length > 256 || state.Any(char.IsWhiteSpace))
            throw new ArgumentException("OAuth state is invalid.", nameof(state));
        if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(oauthError))
            throw new ArgumentException("OAuth code or error is required.");
        if (code is not null && code.Length > 8192)
            throw new ArgumentException("OAuth code is too long.", nameof(code));
        if (oauthError is not null && oauthError.Length > 1024)
            throw new ArgumentException("OAuth error is too long.", nameof(oauthError));

        using var request = new HttpRequestMessage(HttpMethod.Post, "v0/management/oauth-callback")
        {
            Content = JsonContent.Create(new
            {
                provider = "antigravity",
                state,
                code,
                error = oauthError
            })
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode)
            return new(CLIProxyOAuthCallbackDisposition.Accepted);
        if ((int)response.StatusCode == 409)
            return new(CLIProxyOAuthCallbackDisposition.AlreadyProcessed);

        var status = (int)response.StatusCode;
        throw new HttpRequestException($"Broker OAuth callback failed with HTTP {status}.");
    }

    public async Task<CLIProxyOAuthStatus> GetOAuthStatusAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "v0/management/auth-files"), ct);
        var files = await ReadAsync<AuthFilesDto>(response, ct);
        var mapped = Map(files);
        var active = mapped.FirstOrDefault(x => !x.Disabled);
        return new(_slot, active is null ? "pending" : "connected", null, active?.StableAuthId);
    }

    public async Task<CLIProxyAuthFiles> ListAuthFilesAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "v0/management/auth-files"), ct);
        return new(_slot, Map(await ReadAsync<AuthFilesDto>(response, ct)));
    }

    public Task DisableAsync(string stableAuthId, CancellationToken ct = default) => SetDisabledAsync(stableAuthId, true, ct);
    public Task EnableAsync(string stableAuthId, CancellationToken ct = default) => SetDisabledAsync(stableAuthId, false, ct);

    public async Task<IReadOnlyList<CLIProxyAuthFile>> ListByFuturePrefixAsync(string prefix, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Slots[_slot].FutureConsolidationPrefix) ||
            !string.Equals(prefix, _options.Slots[_slot].FutureConsolidationPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Prefix operations are reserved for an explicitly labeled future consolidation.");
        var result = await ListAuthFilesAsync(ct);
        return result.Files.Where(x => x.StableAuthId.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }

    public async Task DeleteAsync(string stableAuthId, CancellationToken ct = default)
    {
        var id = CLIProxyAuthIdentifier.Validate(stableAuthId);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"v0/management/auth-files/{Uri.EscapeDataString(id)}");
        using var response = await SendAsync(request, ct);
        var after = await ListAuthFilesAsync(ct);
        if (after.Files.Any(x => x.StableAuthId == id)) throw new InvalidOperationException("Broker delete was not verified.");
    }

    private async Task SetDisabledAsync(string stableAuthId, bool disabled, CancellationToken ct)
    {
        var id = CLIProxyAuthIdentifier.Validate(stableAuthId);
        var method = disabled ? HttpMethod.Post : HttpMethod.Post;
        using var request = new HttpRequestMessage(method, $"v0/management/auth-files/{Uri.EscapeDataString(id)}/{(disabled ? "disable" : "enable")}");
        request.Content = JsonContent.Create(new { auth_index = id });
        using var response = await SendAsync(request, ct);
        var after = await ListAuthFilesAsync(ct);
        var record = after.Files.FirstOrDefault(x => x.StableAuthId == id);
        if (record is null || record.Disabled != disabled) throw new InvalidOperationException("Broker mutation was not verified.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        { var status = (int)response.StatusCode; response.Dispose(); throw new HttpRequestException($"Broker management request failed with HTTP {status}."); }
        return response;
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var limited = new LimitedReadStream(stream, _options.MaxResponseBytes);
        return await JsonSerializer.DeserializeAsync<T>(limited, Json, ct) ?? throw new InvalidOperationException("Broker returned an empty response.");
    }

    private static CLIProxyAuthFile[] Map(AuthFilesDto dto) => (dto.Files ?? Array.Empty<AuthFileDto>()).Select(x =>
    {
        var id = x.AuthIndex ?? x.Id;
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Broker returned an auth record without a stable identifier.");
        return new CLIProxyAuthFile(CLIProxyAuthIdentifier.Validate(id), x.Disabled, x.Provider, x.AccountLabel);
    }).ToArray();

    private sealed record AuthStartDto([property: JsonPropertyName("url")] string? Url);
    private sealed record AuthFilesDto([property: JsonPropertyName("files")] AuthFileDto[]? Files);
    private sealed record AuthFileDto([property: JsonPropertyName("auth_index")] string? AuthIndex, [property: JsonPropertyName("id")] string? Id, [property: JsonPropertyName("disabled")] bool Disabled, [property: JsonPropertyName("provider")] string? Provider, [property: JsonPropertyName("account")] string? AccountLabel);
}

internal sealed class LimitedReadStream : Stream
{
    private readonly Stream _inner; private readonly int _limit; private int _read;
    public LimitedReadStream(Stream inner, int limit) { _inner = inner; _limit = limit > 0 ? limit : throw new ArgumentOutOfRangeException(nameof(limit)); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { var n = await _inner.ReadAsync(buffer, ct); _read += n; if (_read > _limit) throw new InvalidOperationException("Broker response exceeded the configured size limit."); return n; }
    public override int Read(byte[] buffer, int offset, int count) { var n = _inner.Read(buffer, offset, count); _read += n; if (_read > _limit) throw new InvalidOperationException("Broker response exceeded the configured size limit."); return n; }
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException(); public override long Position { get => _read; set => throw new NotSupportedException(); } public override void Flush() { } public override long Seek(long o, SeekOrigin w) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
}
