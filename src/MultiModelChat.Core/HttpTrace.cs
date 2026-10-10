namespace MultiModelChat;

/// <summary>One HTTP attempt as it went over the wire. Headers are not recorded, so keys never land in a trace.</summary>
public sealed record HttpExchange(
    DateTimeOffset At,
    string Method,
    Uri Uri,
    string? RequestBody,
    int? Status,
    string? ResponseBody,
    string? TransportError);

/// <summary>Every HTTP attempt made for one model, SDK retries included.</summary>
public sealed class HttpTrace
{
    readonly List<HttpExchange> _exchanges = [];
    readonly Lock _gate = new();

    public IReadOnlyList<HttpExchange> Exchanges
    {
        get { lock (_gate) return [.. _exchanges]; }
    }

    public int Count
    {
        get { lock (_gate) return _exchanges.Count; }
    }

    public IReadOnlyList<HttpExchange> Since(int index)
    {
        lock (_gate) return _exchanges.Skip(index).ToList();
    }

    internal void Add(HttpExchange exchange)
    {
        lock (_gate) _exchanges.Add(exchange);
    }
}

/// <summary>Records each attempt's request and response bodies, then passes the request on unchanged.</summary>
public sealed class TraceHandler(HttpTrace trace) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? requestBody = null;
        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync(cancellationToken);
            requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        var at = DateTimeOffset.UtcNow;
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            await response.Content.LoadIntoBufferAsync(cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            trace.Add(new(at, request.Method.Method, request.RequestUri!, requestBody, (int)response.StatusCode, responseBody, null));
            return response;
        }
        catch (Exception e)
        {
            trace.Add(new(at, request.Method.Method, request.RequestUri!, requestBody, null, null, e.GetType().Name));
            throw;
        }
    }
}
