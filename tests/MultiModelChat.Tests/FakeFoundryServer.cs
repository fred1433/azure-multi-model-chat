using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MultiModelChat.Simulation;

namespace MultiModelChat.Tests;

/// <summary>
/// A real HTTP server on 127.0.0.1 that records every request body the SDKs emit and answers with
/// documented response shapes (see SimulatedFoundry for the source of each shape).
/// </summary>
public sealed class FakeFoundryServer : IAsyncDisposable
{
    readonly WebApplication _app;
    public Uri BaseUri { get; }
    public ConcurrentQueue<FakeRequest> Requests { get; } = new();

    /// <summary>Replace to script answers; may delay to simulate a slow model.</summary>
    public Func<FakeRequest, CancellationToken, Task<FakeResponse>> Responder { get; set; } =
        (request, _) => Task.FromResult(SimulatedFoundry.Respond(request));

    FakeFoundryServer(WebApplication app, Uri baseUri)
    {
        _app = app;
        BaseUri = baseUri;
    }

    public static async Task<FakeFoundryServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        FakeFoundryServer? server = null;

        app.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var headers = context.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString());
            var request = new FakeRequest(
                context.Request.Method,
                headers.GetValueOrDefault("x-original-host", context.Request.Host.Host),
                context.Request.Path.Value ?? "",
                context.Request.QueryString.Value ?? "",
                headers,
                body);
            server!.Requests.Enqueue(request);

            FakeResponse reply;
            try
            {
                reply = await server.Responder(request, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                return; // the client gave up
            }

            context.Response.StatusCode = reply.Status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(reply.Body);
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new FakeFoundryServer(app, new Uri(address));
        return server;
    }

    /// <summary>Requests whose body names this deployment, in arrival order.</summary>
    public List<FakeRequest> For(string deployment) => Requests.Where(r => r.Model == deployment).ToList();

    /// <summary>Transport for the SDKs: same path and query, but sent to this server instead of Azure.</summary>
    public HttpMessageHandler CreateTransport() => new LoopbackRedirectHandler(BaseUri) { InnerHandler = new SocketsHttpHandler() };

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>Rewrites https://{resource}.../path to http://127.0.0.1:{port}/path and keeps the original host in a header.</summary>
public sealed class LoopbackRedirectHandler(Uri target) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var original = request.RequestUri!;
        request.Headers.TryAddWithoutValidation("x-original-host", original.Host);
        request.Headers.TryAddWithoutValidation("x-original-scheme", original.Scheme);
        request.RequestUri = new Uri(target, original.PathAndQuery);
        return base.SendAsync(request, cancellationToken);
    }
}
