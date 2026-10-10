using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiModelChat.Tests;

static class TestRows
{
    public const string Prompt1 = "Booking note: Morgan reserved three rooms in Porto. Which city is mentioned? Answer with the city only.";
    public const string Prompt2 = "How many rooms were reserved? Answer with the number only.";

    public static Dictionary<string, JsonElement> Options(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    public static ModelEntry ChatRow(string deployment, string keyEnv = "KEY_A", string options = "{}",
        string baseUrl = "https://res1.openai.azure.com/openai/v1/", int timeoutSeconds = 30, int maxRetries = 0) => new()
    {
        Name = deployment,
        CatalogId = deployment,
        Deployment = deployment,
        Protocol = Protocols.OpenAIChat,
        BaseUrl = baseUrl,
        ApiKeyEnv = keyEnv,
        Options = Options(options),
        TimeoutSeconds = timeoutSeconds,
        MaxRetries = maxRetries,
    };

    public static ModelEntry ClaudeRow(string deployment, string keyEnv = "KEY_C", string options = """{"max_tokens":1024}""",
        string resourceName = "res1", int timeoutSeconds = 30, int maxRetries = 0) => new()
    {
        Name = deployment,
        CatalogId = deployment,
        Deployment = deployment,
        Protocol = Protocols.AnthropicMessages,
        ResourceName = resourceName,
        ApiKeyEnv = keyEnv,
        Options = Options(options),
        TimeoutSeconds = timeoutSeconds,
        MaxRetries = maxRetries,
    };

    public static RunConfig Config(params ModelEntry[] rows) => new()
    {
        Run = new RunSettings { MaxParallel = 4, Prompts = [Prompt1, Prompt2] },
        Models = [.. rows],
    };

    public static readonly Dictionary<string, string> Keys = new()
    {
        ["KEY_A"] = "key-a",
        ["KEY_B"] = "key-b",
        ["KEY_C"] = "key-c",
        ["KEY_D"] = "key-d",
    };

    public static ConversationRunner Runner(FakeFoundryServer server, Dictionary<string, string>? keys = null) =>
        new(server.CreateTransport, name => (keys ?? Keys).GetValueOrDefault(name));

    public static JsonArray Messages(MultiModelChat.Simulation.FakeRequest request) => request.Json!["messages"]!.AsArray();
}
