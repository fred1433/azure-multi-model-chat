using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MultiModelChat;

public enum TurnStatus
{
    Ok,
    /// <summary>HTTP 200 but stopped by the token limit (finish_reason "length", stop_reason "max_tokens").</summary>
    Truncated,
    /// <summary>HTTP 200 with no answer text.</summary>
    Empty,
    Failed,
    TimedOut,
    NotRun,
}

public sealed record TurnReport(
    int Index,
    string Prompt,
    TurnStatus Status,
    string? Answer,
    string? Reasoning,
    TokenCounts Tokens,
    TimeSpan? Latency,
    int Attempts,
    string? FinishReason,
    string? Detail,
    int SignedThinkingBlocks = 0)
{
    public bool Succeeded => Status == TurnStatus.Ok;
}

public sealed record ModelReport(ModelEntry Entry, IReadOnlyList<string> ConfigProblems, IReadOnlyList<TurnReport> Turns, HttpTrace Trace)
{
    public bool Skipped => ConfigProblems.Count > 0;
}

public sealed record RunReport(DateTimeOffset StartedAt, IReadOnlyList<string> Prompts, IReadOnlyList<ModelReport> Models);

/// <summary>
/// Sends the same conversation to every configured model: bounded parallelism, one deadline per model and turn,
/// and a failure in one model never blocks the others. Display is left to the caller.
/// </summary>
public sealed class ConversationRunner(
    Func<HttpMessageHandler>? transportFactory = null,
    Func<string, string?>? getEnv = null,
    bool allowPlaceholders = false)
{
    readonly Func<string, string?> _getEnv = getEnv ?? Environment.GetEnvironmentVariable;

    public async Task<RunReport> RunAsync(RunConfig config, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var rows = config.Models.Where(m => m.Enabled).ToList();

        // Every row is checked before the first request goes out.
        var problems = rows.ToDictionary(m => m, m => ConfigLoader.Validate(m, _getEnv, allowPlaceholders));

        using var gate = new SemaphoreSlim(config.Run.MaxParallel);
        var tasks = rows.Select(async m =>
        {
            if (problems[m].Count > 0)
                return new ModelReport(m, problems[m], [], new HttpTrace());

            await gate.WaitAsync(cancellationToken);
            try
            {
                return await RunModelAsync(m, config.Run.Prompts, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        });

        return new RunReport(startedAt, config.Run.Prompts, await Task.WhenAll(tasks));
    }

    async Task<ModelReport> RunModelAsync(ModelEntry m, IReadOnlyList<string> prompts, CancellationToken cancellationToken)
    {
        var trace = new HttpTrace();
        using var http = new HttpClient(new TraceHandler(trace) { InnerHandler = transportFactory?.Invoke() ?? new SocketsHttpHandler() })
        {
            Timeout = Timeout.InfiniteTimeSpan, // the per-turn deadline below is the one that counts
        };
        using var client = ChatClientFactory.Create(m, _getEnv(m.ApiKeyEnv)!, http);

        var history = new List<ChatMessage>();
        var turns = new List<TurnReport>();
        TurnReport? previous = null;

        for (var i = 0; i < prompts.Count; i++)
        {
            if (previous is { Succeeded: false })
            {
                turns.Add(new(i + 1, prompts[i], TurnStatus.NotRun, null, null, TokenCounts.Unknown, null, 0, null,
                    $"turn {previous.Index} did not succeed ({previous.Status})"));
                continue;
            }

            history.Add(new ChatMessage(ChatRole.User, prompts[i]));
            previous = await RunTurnAsync(m, client, trace, history, i + 1, prompts[i], cancellationToken);
            turns.Add(previous);
        }

        return new ModelReport(m, [], turns, trace);
    }

    static async Task<TurnReport> RunTurnAsync(
        ModelEntry m, IChatClient client, HttpTrace trace, List<ChatMessage> history, int index, string prompt, CancellationToken cancellationToken)
    {
        var firstExchange = trace.Count;
        var stopwatch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(m.TimeoutSeconds));

        try
        {
            var response = await client.GetResponseAsync(history, OptionBridge.CreateChatOptions(m), deadline.Token);
            stopwatch.Stop();

            var exchanges = trace.Since(firstExchange);
            var lastOk = exchanges.LastOrDefault(e => e.Status is >= 200 and < 300);
            var tokens = TokenCounts.FromResponseBody(m.Protocol, lastOk?.ResponseBody);

            var answer = response.Text;
            var reasoning = string.Join("\n", response.Messages
                .SelectMany(msg => msg.Contents.OfType<TextReasoningContent>())
                .Select(r => r.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t)));

            // Claude's thinking blocks arrive with empty text and a signature by default; they are kept for the next turn.
            var signedThinking = response.Messages
                .SelectMany(msg => msg.Contents.OfType<TextReasoningContent>())
                .Count(r => string.IsNullOrEmpty(r.Text) && !string.IsNullOrEmpty(r.ProtectedData));

            var nativeFinish = NativeFinishReason(m.Protocol, lastOk?.ResponseBody) ?? response.FinishReason?.Value;
            var status = response.FinishReason == ChatFinishReason.Length || nativeFinish is "length" or "max_tokens" ? TurnStatus.Truncated
                : string.IsNullOrWhiteSpace(answer) ? TurnStatus.Empty
                : TurnStatus.Ok;

            // The history keeps the SDK's own message objects, never text rebuilt from the display.
            history.AddRange(m.Protocol == Protocols.OpenAIChat ? WithoutReasoning(response.Messages) : response.Messages);

            return new(index, prompt, status, answer, reasoning.Length > 0 ? reasoning : null, tokens, stopwatch.Elapsed,
                exchanges.Count, nativeFinish, status switch
                {
                    TurnStatus.Truncated => "stopped by the token limit before finishing",
                    TurnStatus.Empty => "HTTP 200 with no answer text",
                    _ => null,
                }, signedThinking);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(index, prompt, TurnStatus.TimedOut, null, null, TokenCounts.Unknown, stopwatch.Elapsed,
                trace.Count - firstExchange, null, $"no answer within {m.TimeoutSeconds}s");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var exchanges = trace.Since(firstExchange);
            return new(index, prompt, TurnStatus.Failed, null, null, TokenCounts.Unknown, stopwatch.Elapsed,
                exchanges.Count, null, ServerMessage(exchanges.LastOrDefault()) ?? $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Microsoft's guidance for reasoning models on Chat Completions: send back the final answer, not the
    /// reasoning_content. The pinned OpenAI adapter already leaves it out; this keeps it that way if that changes.
    /// (Claude is different: its thinking blocks go back untouched.)
    /// </summary>
    static IEnumerable<ChatMessage> WithoutReasoning(IEnumerable<ChatMessage> messages) =>
        messages.Select(msg =>
        {
            var clone = msg.Clone();
            clone.Contents = [.. msg.Contents.Where(c => c is not TextReasoningContent)];
            return clone;
        });

    /// <summary>finish_reason (Chat Completions) or stop_reason (Messages), as the server wrote it.</summary>
    static string? NativeFinishReason(string protocol, string? body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body ?? "");
            var root = doc.RootElement;
            if (protocol == Protocols.AnthropicMessages)
                return root.TryGetProperty("stop_reason", out var stop) && stop.ValueKind == JsonValueKind.String ? stop.GetString() : null;
            return root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String
                ? finish.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The server's own words for a non-2xx answer: error.message in both APIs' error bodies.</summary>
    static string? ServerMessage(HttpExchange? exchange)
    {
        if (exchange is not { Status: >= 400 }) return null;
        var message = exchange.ResponseBody;
        try
        {
            using var doc = JsonDocument.Parse(exchange.ResponseBody ?? "");
            if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var text))
                message = text.GetString();
        }
        catch (JsonException)
        {
        }
        return $"HTTP {exchange.Status}: {message}";
    }
}
