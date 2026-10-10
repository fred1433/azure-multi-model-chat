using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Anthropic.Foundry;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace MultiModelChat;

/// <summary>
/// The only place where the two protocols differ: each row becomes an <see cref="IChatClient"/>
/// built from that vendor's official SDK. Everything after this uses one code path.
/// </summary>
public static class ChatClientFactory
{
    public static IChatClient Create(ModelEntry m, string apiKey, HttpClient http) => m.Protocol switch
    {
        Protocols.OpenAIChat => CreateOpenAIChat(m, apiKey, http),
        Protocols.AnthropicMessages => CreateAnthropicMessages(m, apiKey, http),
        _ => throw new ArgumentException($"protocol \"{m.Protocol}\" is unknown."),
    };

    /// <summary>OpenAI .NET SDK pointed at the Foundry v1 route; the deployment name goes in the model field.</summary>
    static IChatClient CreateOpenAIChat(ModelEntry m, string apiKey, HttpClient http)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(m.BaseUrl!),
            Transport = new HttpClientPipelineTransport(http),
            RetryPolicy = new ClientRetryPolicy(m.MaxRetries),
            NetworkTimeout = TimeSpan.FromSeconds(m.TimeoutSeconds),
        };
        return new ChatClient(m.Deployment, new ApiKeyCredential(apiKey), options).AsIChatClient();
    }

    /// <summary>Anthropic.Foundry: the URL is built from the resource name, the key goes in x-api-key.</summary>
    static IChatClient CreateAnthropicMessages(ModelEntry m, string apiKey, HttpClient http)
    {
        var client = new AnthropicFoundryClient(new AnthropicFoundryApiKeyCredentials(apiKey, m.ResourceName!))
        {
            HttpClient = http,
            MaxRetries = m.MaxRetries,
            Timeout = TimeSpan.FromSeconds(m.TimeoutSeconds),
        };
        return client.AsIChatClient(m.Deployment);
    }
}
