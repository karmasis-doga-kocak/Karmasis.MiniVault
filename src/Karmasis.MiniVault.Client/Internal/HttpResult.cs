using Karmasis.MiniVault.Contracts;

namespace Karmasis.MiniVault.Client.Internal;

/// <summary>The outcome of a conditional GET: either a fresh body or a 304 indicating the cached copy is current.</summary>
internal sealed class HttpResult<T>
{
    public HttpResult(int status, T? body, string? eTag, ErrorResponse? error)
    {
        Status = status;
        Body = body;
        ETag = eTag;
        Error = error;
    }

    public int Status { get; }
    public T? Body { get; }
    public string? ETag { get; }
    public ErrorResponse? Error { get; }
    public bool NotModified => Status == 304;
}
