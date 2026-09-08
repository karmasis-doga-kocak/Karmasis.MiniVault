using System.Net;
using System.Net.Http;
using System.Text;
using Karmasis.MiniVault.Client.Internal;

namespace Karmasis.MiniVault.Client.Tests.TestDoubles;

public sealed class StubHandler : HttpMessageHandler
{
    private readonly object _sync = new();
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _script = new();
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string?> RequestBodies { get; } = new();

    /// <summary>Invoked after a scripted response is dequeued, outside the lock — lets a test observe/signal
    /// on a specific response (e.g. via a <see cref="System.Threading.Tasks.TaskCompletionSource{TResult}"/>)
    /// without racing the handler's own bookkeeping.</summary>
    public Action<HttpResponseMessage>? OnResponse { get; set; }

    public StubHandler Enqueue(HttpStatusCode status, object? body = null, Action<HttpResponseMessage>? configure = null)
    {
        _script.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status);
            if (body is not null) response.Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json");
            configure?.Invoke(response);
            return response;
        });
        return this;
    }

    public StubHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder) { _script.Enqueue(responder); return this; }
    public StubHandler Enqueue(Exception exception) { _script.Enqueue(_ => throw exception); return this; }
    public int Remaining => _script.Count;

    /// <summary>Answers every request the script no longer covers. Useful for background-refresh tests, where
    /// the number of ticks — and therefore of requests — is not deterministic. When null, an unscripted request
    /// throws instead.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage>? Fallback { get; set; }

    /// <summary>Builds a JSON response the way <see cref="Enqueue(HttpStatusCode, object?, Action{HttpResponseMessage})"/>
    /// does, for use from a <see cref="Fallback"/> responder.</summary>
    public static HttpResponseMessage JsonResponse(HttpStatusCode status, object? body = null, Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage(status);
        if (body is not null) response.Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json");
        configure?.Invoke(response);
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync().ConfigureAwait(false);

        HttpResponseMessage response;
        lock (_sync)
        {
            Requests.Add(request);
            RequestBodies.Add(body);
            var responder = _script.Count > 0
                ? _script.Dequeue()
                : Fallback ?? throw new InvalidOperationException($"No scripted response for {request.Method} {request.RequestUri}");
            response = responder(request);
        }

        OnResponse?.Invoke(response);
        return response;
    }
}
