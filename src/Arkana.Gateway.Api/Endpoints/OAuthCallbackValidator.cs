using Microsoft.AspNetCore.Http;

namespace Arkana.Gateway.Api.Endpoints;

public static class OAuthCallbackValidator
{
    public static bool TryValidate(
        HttpRequest request,
        string? code,
        string? state,
        string? error,
        out string message)
    {
        if (HasDuplicate(request, "code")
            || HasDuplicate(request, "state")
            || HasDuplicate(request, "error"))
        {
            message = "Authorization callback contains duplicate parameters.";
            return false;
        }

        if (!IsSafeState(state))
        {
            message = "Authorization callback state is invalid.";
            return false;
        }

        var hasCode = !string.IsNullOrWhiteSpace(code);
        var hasError = !string.IsNullOrWhiteSpace(error);
        if (hasCode == hasError)
        {
            message = "Authorization callback must contain exactly one result.";
            return false;
        }

        if (hasCode && !IsSafeOpaque(code, 8192))
        {
            message = "Authorization callback code is invalid.";
            return false;
        }

        if (hasError && !IsSafeOpaque(error, 1024))
        {
            message = "Authorization callback error is invalid.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool HasDuplicate(HttpRequest request, string name)
        => request.Query.TryGetValue(name, out var values) && values.Count > 1;

    private static bool IsSafeState(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= 256
            && value.All(ch => ch is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '-' or '_' or '.' or '~');

    private static bool IsSafeOpaque(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(ch => ch is >= '\x21' and <= '\x7e');
}
