using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MultiModelChat;

/// <summary>The two wire protocols a Foundry deployment can speak in this runner.</summary>
public static class Protocols
{
    /// <summary>OpenAI-compatible Chat Completions on <c>https://{resource}.openai.azure.com/openai/v1/</c>.</summary>
    public const string OpenAIChat = "openai-chat";

    /// <summary>Anthropic Messages on <c>https://{resource}.services.ai.azure.com/anthropic/v1/messages</c>.</summary>
    public const string AnthropicMessages = "anthropic-messages";
}

/// <summary>Root of <c>models.json</c>.</summary>
public sealed class RunConfig
{
    public RunSettings Run { get; init; } = new();
    public List<ModelEntry> Models { get; init; } = [];
}

public sealed class RunSettings
{
    /// <summary>How many models are called at the same time.</summary>
    public int MaxParallel { get; init; } = 4;

    /// <summary>Prompts sent in order, as one conversation per model.</summary>
    public List<string> Prompts { get; init; } = [];
}

/// <summary>One row of the configuration table.</summary>
public sealed class ModelEntry
{
    /// <summary>Display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Model identifier as listed in the Foundry catalog (documentation only).</summary>
    public string CatalogId { get; init; } = "";

    /// <summary>Deployment name: the value actually sent in the request's <c>model</c> field.</summary>
    public string Deployment { get; init; } = "";

    /// <summary><see cref="Protocols.OpenAIChat"/> or <see cref="Protocols.AnthropicMessages"/>.</summary>
    public string Protocol { get; init; } = "";

    /// <summary>openai-chat rows only: <c>https://{resource}.openai.azure.com/openai/v1/</c>.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>anthropic-messages rows only: the Foundry resource name.</summary>
    public string? ResourceName { get; init; }

    /// <summary>Name of the environment variable that holds this row's API key. The key itself never goes in the file.</summary>
    public string ApiKeyEnv { get; init; } = "";

    /// <summary>Request-body fields passed as-is to this model's API (top-level JSON properties).</summary>
    public Dictionary<string, JsonElement> Options { get; init; } = [];

    /// <summary>Per-turn deadline, enforced by the runner and given to the SDK.</summary>
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>SDK automatic retries. 0 means one HTTP attempt per turn.</summary>
    public int MaxRetries { get; init; } = 0;

    public bool Enabled { get; init; } = true;

    /// <summary>True for rows added as suggestions rather than requested.</summary>
    public bool Suggested { get; init; }

    public string? Notes { get; init; }

    public List<string>? Docs { get; init; }
}

public sealed class ConfigException(string message) : Exception(message);

public static partial class ConfigLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, // a typo in models.json is an error, never ignored
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly HashSet<string> RunnerOwnedFields = ["model", "messages", "stream", "stream_options"];

    [GeneratedRegex(@"^https://[a-z0-9][a-z0-9-]{0,62}\.(openai\.azure\.com|services\.ai\.azure\.com)/openai/v1/?$", RegexOptions.IgnoreCase)]
    private static partial Regex V1BaseUrl();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceNamePattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex TopLevelField();

    public static RunConfig Parse(string json)
    {
        RunConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<RunConfig>(json, JsonOptions);
        }
        catch (JsonException e)
        {
            throw new ConfigException($"models.json is not valid: {e.Message}");
        }

        if (config is null) throw new ConfigException("models.json is empty.");
        if (config.Run.Prompts.Count == 0) throw new ConfigException("run.prompts needs at least one prompt.");
        if (config.Run.MaxParallel is < 1 or > 32) throw new ConfigException("run.maxParallel must be between 1 and 32.");
        var duplicate = config.Models.GroupBy(m => m.Name).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new ConfigException($"Model name \"{duplicate.Key}\" appears twice.");
        return config;
    }

    public static RunConfig Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// Checks one row before anything is sent. Returns the problems; an empty list means the row can run.
    /// </summary>
    /// <param name="getEnv">Reads environment variables (injectable for tests).</param>
    /// <param name="allowPlaceholders">True in simulation mode, where YOUR-RESOURCE placeholders are fine.</param>
    public static List<string> Validate(ModelEntry m, Func<string, string?> getEnv, bool allowPlaceholders = false)
    {
        var problems = new List<string>();
        void Require(string value, string field)
        {
            if (string.IsNullOrWhiteSpace(value)) problems.Add($"{field} is required.");
        }

        Require(m.Name, "name");
        Require(m.CatalogId, "catalogId");
        Require(m.Deployment, "deployment");
        Require(m.ApiKeyEnv, "apiKeyEnv");

        switch (m.Protocol)
        {
            case Protocols.OpenAIChat:
                if (m.ResourceName is not null)
                    problems.Add("resourceName is only for anthropic-messages rows; openai-chat rows take baseUrl.");
                if (string.IsNullOrWhiteSpace(m.BaseUrl))
                    problems.Add("baseUrl is required for openai-chat rows (https://<resource>.openai.azure.com/openai/v1/).");
                else if (m.BaseUrl.Contains('?'))
                    problems.Add("baseUrl must not carry a query string: the /openai/v1/ route takes no api-version.");
                else if (!V1BaseUrl().IsMatch(m.BaseUrl))
                    problems.Add($"baseUrl \"{m.BaseUrl}\" is not a v1 route. Expected https://<resource>.openai.azure.com/openai/v1/ or https://<resource>.services.ai.azure.com/openai/v1/.");
                break;

            case Protocols.AnthropicMessages:
                if (m.BaseUrl is not null)
                    problems.Add("anthropic-messages rows take resourceName, not baseUrl: the Anthropic.Foundry C# client always builds https://<resourceName>.services.ai.azure.com/anthropic and would silently ignore a base URL.");
                if (string.IsNullOrWhiteSpace(m.ResourceName))
                    problems.Add("resourceName is required for anthropic-messages rows.");
                else if (!ResourceNamePattern().IsMatch(m.ResourceName))
                    problems.Add($"resourceName \"{m.ResourceName}\" should be the bare resource name, not a URL.");
                if (!m.Options.TryGetValue("max_tokens", out var maxTokens) || maxTokens.ValueKind != JsonValueKind.Number)
                    problems.Add("options.max_tokens is required by the Messages API.");
                break;

            default:
                problems.Add($"protocol \"{m.Protocol}\" is unknown. Use \"{Protocols.OpenAIChat}\" or \"{Protocols.AnthropicMessages}\".");
                break;
        }

        foreach (var key in m.Options.Keys)
        {
            if (RunnerOwnedFields.Contains(key))
                problems.Add($"options.{key} cannot be set: the runner sends it.");
            else if (!TopLevelField().IsMatch(key))
                problems.Add($"options.\"{key}\" cannot be transmitted as a top-level request field; nest objects as JSON values instead.");
        }

        if (problems.Count == 0)
        {
            try
            {
                OptionBridge.VerifyTransmission(m);
            }
            catch (OptionNotTransmittableException e)
            {
                problems.Add(e.Message);
            }
        }

        if (m.TimeoutSeconds is < 1 or > 3600) problems.Add("timeoutSeconds must be between 1 and 3600.");
        if (m.MaxRetries is < 0 or > 10) problems.Add("maxRetries must be between 0 and 10.");

        if (!allowPlaceholders &&
            ((m.BaseUrl ?? "").Contains("your-resource", StringComparison.OrdinalIgnoreCase) ||
             (m.ResourceName ?? "").Contains("your-resource", StringComparison.OrdinalIgnoreCase)))
            problems.Add("replace the YOUR-RESOURCE placeholder with your Foundry resource name.");

        if (!string.IsNullOrWhiteSpace(m.ApiKeyEnv) && string.IsNullOrWhiteSpace(getEnv(m.ApiKeyEnv)))
            problems.Add($"environment variable {m.ApiKeyEnv} is not set (the key is read from the environment, never from models.json).");

        return problems;
    }
}
