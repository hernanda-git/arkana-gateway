using System.Net;

namespace Arkana.Application.Features.Chat;

public enum UpstreamFailureClass
{
    None = 0,
    Unauthorized,
    Forbidden,
    RateLimited,
    ClientError,
    ServerError,
    Timeout,
    Network,
    Cancelled,
    Unknown
}

public sealed record AccountAttemptResult(
    bool Succeeded,
    UpstreamFailureClass FailureClass = UpstreamFailureClass.None,
    int? StatusCode = null,
    TimeSpan? RetryAfter = null,
    string? ErrorMessage = null,
    bool StreamingCommitted = false)
{
    public bool Retryable => !Succeeded && !StreamingCommitted && FailureClass is UpstreamFailureClass.RateLimited or UpstreamFailureClass.ServerError or UpstreamFailureClass.Timeout or UpstreamFailureClass.Network;

    public static AccountAttemptResult Success() => new(true);
    public static AccountAttemptResult Failure(UpstreamFailureClass kind, int? status = null, TimeSpan? retryAfter = null, string? message = null, bool committed = false)
        => new(false, kind, status, retryAfter, message, committed);
}

public static class UpstreamFailureClassifier
{
    public static AccountAttemptResult Classify(HttpStatusCode status, string? retryAfter = null, string? message = null, bool streamingCommitted = false)
    {
        var code = (int)status;
        var kind = code switch
        {
            401 => UpstreamFailureClass.Unauthorized,
            403 => UpstreamFailureClass.Forbidden,
            429 => UpstreamFailureClass.RateLimited,
            >= 500 and <= 599 => UpstreamFailureClass.ServerError,
            >= 400 and <= 499 => UpstreamFailureClass.ClientError,
            _ => UpstreamFailureClass.Unknown
        };
        return AccountAttemptResult.Failure(kind, code, ParseRetryAfter(retryAfter), message, streamingCommitted);
    }

    public static AccountAttemptResult Classify(Exception exception, bool streamingCommitted = false)
    {
        if (exception is OperationCanceledException) return AccountAttemptResult.Failure(UpstreamFailureClass.Cancelled, committed: streamingCommitted);
        if (exception is TimeoutException or TaskCanceledException) return AccountAttemptResult.Failure(UpstreamFailureClass.Timeout, committed: streamingCommitted);
        if (exception is HttpRequestException) return AccountAttemptResult.Failure(UpstreamFailureClass.Network, committed: streamingCommitted);
        return AccountAttemptResult.Failure(UpstreamFailureClass.Unknown, committed: streamingCommitted);
    }

    public static TimeSpan? BoundedQuotaCooldown(AccountAttemptResult result, TimeSpan? configured = null)
    {
        if (result.FailureClass != UpstreamFailureClass.RateLimited) return null;
        var value = result.RetryAfter ?? configured ?? TimeSpan.FromSeconds(30);
        return value < TimeSpan.Zero ? TimeSpan.Zero : value > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : value;
    }

    private static TimeSpan? ParseRetryAfter(string? value)
        => int.TryParse(value, out var seconds) && seconds >= 0 ? TimeSpan.FromSeconds(Math.Min(seconds, 600)) : null;
}
