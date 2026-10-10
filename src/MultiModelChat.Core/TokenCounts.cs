using System.Text.Json;

namespace MultiModelChat;

/// <summary>
/// Token counts for one turn, normalized across the two APIs. A field the server did not send stays null
/// and prints as "n/a", never as 0.
/// </summary>
/// <param name="Input">Total input tokens for the request.</param>
/// <param name="Output">Total output tokens (reasoning included where the API counts it there).</param>
/// <param name="Details">Sub-counts as the API reported them, never added to the totals twice.</param>
public sealed record TokenCounts(long? Input, long? Output, IReadOnlyDictionary<string, long?> Details)
{
    public static readonly TokenCounts Unknown = new(null, null, new Dictionary<string, long?>());

    public static string Show(long? value) => value?.ToString() ?? "n/a";

    public string InOut => $"{Show(Input)} / {Show(Output)}";

    public static TokenCounts FromResponseBody(string protocol, string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return Unknown;
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                return Unknown;
            return protocol == Protocols.AnthropicMessages ? FromAnthropicUsage(usage) : FromOpenAIUsage(usage);
        }
        catch (JsonException)
        {
            return Unknown;
        }
    }

    /// <summary>
    /// Chat Completions: input = prompt_tokens, output = completion_tokens. Cached tokens are part of
    /// prompt_tokens and reasoning tokens part of completion_tokens, so both stay as details only.
    /// </summary>
    public static TokenCounts FromOpenAIUsage(JsonElement usage) => new(
        Number(usage, "prompt_tokens"),
        Number(usage, "completion_tokens"),
        new Dictionary<string, long?>
        {
            ["cached_tokens"] = Number(usage, "prompt_tokens_details", "cached_tokens"),
            ["reasoning_tokens"] = Number(usage, "completion_tokens_details", "reasoning_tokens"),
        });

    /// <summary>
    /// Messages API: total input = input_tokens + cache_creation_input_tokens + cache_read_input_tokens
    /// (prompt-caching documentation). The three parts are kept as details, plus
    /// output_tokens_details.thinking_tokens, which is already inside output_tokens.
    /// </summary>
    public static TokenCounts FromAnthropicUsage(JsonElement usage)
    {
        var input = Number(usage, "input_tokens");
        var created = Number(usage, "cache_creation_input_tokens");
        var read = Number(usage, "cache_read_input_tokens");
        return new(
            input is null ? null : input + (created ?? 0) + (read ?? 0),
            Number(usage, "output_tokens"),
            new Dictionary<string, long?>
            {
                ["input_tokens"] = input,
                ["cache_creation_input_tokens"] = created,
                ["cache_read_input_tokens"] = read,
                ["thinking_tokens"] = Number(usage, "output_tokens_details", "thinking_tokens"),
            });
    }

    static long? Number(JsonElement obj, params string[] path)
    {
        var current = obj;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) return null;
        }
        return current.ValueKind == JsonValueKind.Number && current.TryGetInt64(out var n) ? n : null;
    }
}
