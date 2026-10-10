using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiModelChat.Simulation;

public sealed record FakeRequest(string Method, string Host, string Path, string Query, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public JsonNode? Json => string.IsNullOrWhiteSpace(Body) ? null : JsonNode.Parse(Body);
    public string? Model => Json?["model"]?.GetValue<string>();
    public int UserTurns => Json?["messages"]?.AsArray().Count(m => m?["role"]?.GetValue<string>() == "user") ?? 0;
}

public sealed record FakeResponse(int Status, string Body);

/// <summary>
/// A stand-in for a Foundry resource. It answers with the response shapes documented for each API; it is not a
/// model and nothing here has been observed on Azure.
/// <list type="bullet">
/// <item>Chat Completions body and usage: https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/reasoning</item>
/// <item>reasoning_content on non-OpenAI reasoning models: https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/use-chat-reasoning</item>
/// <item>Chat Completions error envelope (error.code, error.message) and the content_filter finish reason: https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/use-chat-reasoning#handle-content-safety-responses</item>
/// <item>Messages stop_reason "refusal": https://platform.claude.com/docs/en/api/messages/create</item>
/// <item>choices[0].message.refusal: https://learn.microsoft.com/en-us/azure/developer/ai/how-to/extract-entities-using-structured-outputs#why-checking-for-refusal-is-important</item>
/// <item>Messages body, stop_reason and usage: https://platform.claude.com/docs/en/api/messages/create</item>
/// <item>Empty thinking text with a signature (Opus 5.5 default): https://platform.claude.com/docs/en/models/opus-5-5/migration-guide</item>
/// <item>Messages error envelope: https://platform.claude.com/docs/en/api/errors#error-shapes</item>
/// </list>
/// </summary>
public static class SimulatedFoundry
{
    public const string ChatCompletionsPath = "/openai/v1/chat/completions";
    public const string MessagesPath = "/anthropic/v1/messages";

    /// <summary>Default behaviour: "Porto" on the first turn, "3" on the second, for every deployment.</summary>
    public static FakeResponse Respond(FakeRequest request)
    {
        var answer = request.UserTurns <= 1 ? "Porto" : "3";
        var model = request.Model ?? "unknown";

        if (request.Path.EndsWith(ChatCompletionsPath, StringComparison.Ordinal))
        {
            var withReasoning = model.Contains("kimi", StringComparison.OrdinalIgnoreCase)
                || model.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
            var reasoning = withReasoning
                ? (request.UserTurns <= 1 ? "The note names one city, Porto." : "The note says three rooms.")
                : null;
            var reasoningTokens = reasoning is null ? 0 : EstimateTokens(reasoning);
            return ChatCompletion(model, answer, reasoning,
                promptTokens: EstimatePromptTokens(request),
                completionTokens: EstimateTokens(answer) + reasoningTokens,
                reasoningTokens: reasoningTokens);
        }

        if (request.Path.EndsWith(MessagesPath, StringComparison.Ordinal))
        {
            return Message(model, answer, thinkingSignature: $"sim-signature-{request.UserTurns}",
                inputTokens: EstimatePromptTokens(request),
                outputTokens: EstimateTokens(answer) + 24);
        }

        return new(404, """{"error":{"code":"404","message":"Resource not found"}}""");
    }

    public static FakeResponse ChatCompletion(
        string model, string? content, string? reasoning = null, string finishReason = "stop",
        long? promptTokens = 20, long? completionTokens = 5, long? cachedTokens = 0, long? reasoningTokens = 0,
        bool includeUsage = true, string? refusal = null)
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = content };
        if (refusal is not null) message["refusal"] = refusal;
        if (reasoning is not null) message["reasoning_content"] = reasoning;

        var body = new JsonObject
        {
            ["id"] = "chatcmpl-simulated",
            ["object"] = "chat.completion",
            ["created"] = 1791590400,
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["finish_reason"] = finishReason,
                ["message"] = message,
            }),
        };

        if (includeUsage)
        {
            var usage = new JsonObject();
            if (promptTokens is not null) usage["prompt_tokens"] = promptTokens;
            if (completionTokens is not null) usage["completion_tokens"] = completionTokens;
            if (promptTokens is not null && completionTokens is not null) usage["total_tokens"] = promptTokens + completionTokens;
            if (cachedTokens is not null) usage["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = cachedTokens };
            if (reasoningTokens is not null) usage["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = reasoningTokens };
            body["usage"] = usage;
        }

        return new(200, body.ToJsonString());
    }

    public static FakeResponse Message(
        string model, string text, string? thinkingSignature = null, string stopReason = "end_turn",
        long? inputTokens = 20, long? outputTokens = 5, long? cacheCreationInputTokens = 0, long? cacheReadInputTokens = 0)
    {
        var content = new JsonArray();
        if (thinkingSignature is not null)
        {
            // Opus 5.5 default: thinking.display is "omitted", so the block has empty text and a signature.
            content.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = thinkingSignature });
        }
        content.Add(new JsonObject { ["type"] = "text", ["text"] = text });

        var usage = new JsonObject();
        if (inputTokens is not null) usage["input_tokens"] = inputTokens;
        if (cacheCreationInputTokens is not null) usage["cache_creation_input_tokens"] = cacheCreationInputTokens;
        if (cacheReadInputTokens is not null) usage["cache_read_input_tokens"] = cacheReadInputTokens;
        if (outputTokens is not null) usage["output_tokens"] = outputTokens;

        var body = new JsonObject
        {
            ["id"] = "msg_simulated",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = model,
            ["content"] = content,
            ["stop_reason"] = stopReason,
            ["stop_sequence"] = null,
            ["usage"] = usage,
        };
        return new(200, body.ToJsonString());
    }

    /// <summary>Chat Completions error envelope (error.code, error.message).</summary>
    public static FakeResponse ChatCompletionsError(int status, string message, string code) =>
        new(status, new JsonObject
        {
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }.ToJsonString());

    /// <summary>Messages error envelope (type "error", error.type, error.message, request_id).</summary>
    public static FakeResponse MessagesError(int status, string type, string message) =>
        new(status, new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["type"] = type, ["message"] = message },
            ["request_id"] = "req_simulated",
        }.ToJsonString());

    /// <summary>Rough, deterministic stand-in for a tokenizer: about four characters per token.</summary>
    public static long EstimateTokens(string text) => Math.Max(1, (text.Length + 3) / 4);

    static long EstimatePromptTokens(FakeRequest request)
    {
        long total = 8;
        foreach (var message in request.Json?["messages"]?.AsArray() ?? [])
        {
            total += 4 + EstimateTokens(message?["content"] switch
            {
                JsonValue v => v.GetValue<string>(),
                JsonArray blocks => string.Concat(blocks.Select(b => b?["text"]?.GetValue<string>() ?? "")),
                _ => "",
            });
        }
        return total;
    }
}

/// <summary>Serves <see cref="SimulatedFoundry"/> in-process, for <c>dotnet run -- --simulate</c>.</summary>
public sealed class SimulatedFoundryHandler(Func<FakeRequest, FakeResponse>? responder = null) : HttpMessageHandler
{
    readonly Func<FakeRequest, FakeResponse> _responder = responder ?? SimulatedFoundry.Respond;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        var uri = request.RequestUri!;
        var reply = _responder(new FakeRequest(request.Method.Method, uri.Host, uri.AbsolutePath, uri.Query, headers, body));
        return new HttpResponseMessage((System.Net.HttpStatusCode)reply.Status)
        {
            Content = new StringContent(reply.Body, System.Text.Encoding.UTF8, "application/json"),
            RequestMessage = request,
        };
    }
}
