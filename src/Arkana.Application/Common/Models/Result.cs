namespace Arkana.Application.Common.Models;

/// <summary>
/// Standard result wrapper for all application operations.
/// </summary>
public class Result<T>
{
    public bool IsSuccess { get; init; }
    public T? Data { get; init; }
    public string? Error { get; init; }
    public int StatusCode { get; init; } = 200;

    /// <summary>Creates a successful result with the given data.</summary>
    public static Result<T> Success(T data) => new() { IsSuccess = true, Data = data, StatusCode = 200 };
    /// <summary>Creates a success result with status 201 (Created).</summary>
    public static Result<T> Created(T data) => new() { IsSuccess = true, Data = data, StatusCode = 201 };
    /// <summary>Creates a failure result with the specified error message and status code.</summary>
    public static Result<T> Failure(string error, int statusCode = 400) => new() { Error = error, StatusCode = statusCode };
    /// <summary>Creates a 404 Not Found result.</summary>
    public static Result<T> NotFound(string error = "Resource not found") => new() { Error = error, StatusCode = 404 };
    /// <summary>Creates a 401 Unauthorized result.</summary>
    public static Result<T> Unauthorized(string error = "Unauthorized") => new() { Error = error, StatusCode = 401 };
}
