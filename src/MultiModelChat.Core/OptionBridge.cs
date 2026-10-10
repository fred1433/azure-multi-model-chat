using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace MultiModelChat;

public sealed class OptionNotTransmittableException(string message) : Exception(message);

/// <summary>
/// Turns a row's free-form <c>options</c> block into each SDK's native request object, through
/// <see cref="ChatOptions.RawRepresentationFactory"/>. Nothing goes through ChatOptions.AdditionalProperties,
/// which adapters are free to ignore.
/// </summary>
public static class OptionBridge
{
    /// <summary>
    /// The factory returns a NEW native options object on every call: adapters may mutate it, so it must
    /// never be shared between calls or between parallel models.
    /// </summary>
    public static Func<IChatClient, object?> CreateFactory(ModelEntry m) => m.Protocol switch
    {
        Protocols.OpenAIChat => _ => BuildOpenAI(m),
        Protocols.AnthropicMessages => _ => BuildAnthropic(m),
        _ => throw new OptionNotTransmittableException($"protocol \"{m.Protocol}\" is unknown."),
    };

    public static ChatOptions CreateChatOptions(ModelEntry m) => new() { RawRepresentationFactory = CreateFactory(m) };

    /// <summary>OpenAI SDK: fields not modeled on ChatCompletionOptions are written with JsonPatch.</summary>
    public static ChatCompletionOptions BuildOpenAI(ModelEntry m)
    {
        var options = new ChatCompletionOptions();
        foreach (var (key, value) in m.Options)
        {
            options.Patch.Set(Encoding.UTF8.GetBytes("$." + key), Encoding.UTF8.GetBytes(value.GetRawText()));
        }
        return options;
    }

    /// <summary>Anthropic SDK: the request body is built from raw JSON, so any documented field can be sent.</summary>
    public static MessageCreateParams BuildAnthropic(ModelEntry m)
    {
        var body = new Dictionary<string, JsonElement>(m.Options)
        {
            ["model"] = JsonSerializer.SerializeToElement(m.Deployment),
            // The adapter appends the conversation to this list; the SDK requires the field to exist.
            ["messages"] = JsonSerializer.SerializeToElement(Array.Empty<object>()),
        };
        return MessageCreateParams.FromRawUnchecked(
            new Dictionary<string, JsonElement>(),
            new Dictionary<string, JsonElement>(),
            body);
    }

    /// <summary>
    /// Builds the native object once and checks that every configured option is present, with the same JSON value,
    /// in what the SDK will serialize. Throws before anything is sent if one would be lost.
    /// </summary>
    public static void VerifyTransmission(ModelEntry m)
    {
        IReadOnlyDictionary<string, JsonElement> emitted = m.Protocol switch
        {
            Protocols.OpenAIChat => ReadObject(ModelReaderWriter.Write(BuildOpenAI(m), ModelReaderWriterOptions.Json, OpenAI.OpenAIContext.Default).ToString()),
            Protocols.AnthropicMessages => BuildAnthropic(m).RawBodyData,
            _ => throw new OptionNotTransmittableException($"protocol \"{m.Protocol}\" is unknown."),
        };

        var lost = m.Options
            .Where(o => !emitted.TryGetValue(o.Key, out var sent) || !JsonElement.DeepEquals(sent, o.Value))
            .Select(o => o.Key)
            .ToList();
        if (lost.Count > 0)
            throw new OptionNotTransmittableException($"options {string.Join(", ", lost)} would not reach the {m.Protocol} request body as configured.");
    }

    static Dictionary<string, JsonElement> ReadObject(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }
}
