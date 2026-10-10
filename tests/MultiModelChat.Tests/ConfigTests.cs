using static MultiModelChat.Tests.TestRows;

namespace MultiModelChat.Tests;

public class ConfigTests
{
    static List<string> Problems(ModelEntry row) => ConfigLoader.Validate(row, name => Keys.GetValueOrDefault(name));

    [Theory]
    [InlineData("https://res1.openai.azure.com/openai/v1/")]
    [InlineData("https://res1.services.ai.azure.com/openai/v1/")]
    public void Both_documented_v1_base_urls_are_accepted(string baseUrl) =>
        Assert.Empty(Problems(ChatRow("gpt-6-astra", baseUrl: baseUrl)));

    [Theory]
    [InlineData("https://res1.openai.azure.com/openai/v1/?api-version=2024-10-21", "takes no api-version")]
    [InlineData("https://res1.openai.azure.com/openai/deployments/gpt-6-astra/", "is not a v1 route")]
    [InlineData("http://res1.openai.azure.com/openai/v1/", "is not a v1 route")]
    [InlineData("https://res1.services.ai.azure.com/anthropic/", "is not a v1 route")]
    public void Other_base_urls_are_rejected(string baseUrl, string expected) =>
        Assert.Contains(Problems(ChatRow("gpt-6-astra", baseUrl: baseUrl)), p => p.Contains(expected));

    [Fact]
    public void Claude_row_with_a_base_url_is_rejected_instead_of_silently_ignored()
    {
        var row = ClaudeRow("claude-opus-5-5");
        row = new ModelEntry
        {
            Name = row.Name, CatalogId = row.CatalogId, Deployment = row.Deployment, Protocol = row.Protocol,
            ResourceName = row.ResourceName, ApiKeyEnv = row.ApiKeyEnv, Options = row.Options,
            BaseUrl = "https://res1.services.ai.azure.com/anthropic/",
        };
        Assert.Contains(Problems(row), p => p.Contains("would silently ignore a base URL"));
    }

    [Fact]
    public void Unknown_field_in_models_json_is_an_error()
    {
        var json = """{"run":{"prompts":["hi"]},"models":[{"name":"a","deploymnet":"typo"}]}""";
        var e = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(json));
        Assert.Contains("deploymnet", e.Message);
    }

    [Fact]
    public void Missing_key_is_reported_by_name_without_reading_the_file_for_it() =>
        Assert.Contains(Problems(ChatRow("grok-4.7", keyEnv: "NOT_SET")), p => p.Contains("NOT_SET is not set"));

    [Fact]
    public void Claude_rows_need_max_tokens() =>
        Assert.Contains(Problems(ClaudeRow("claude-haiku-5-5", options: "{}")), p => p.Contains("max_tokens is required"));

    [Fact]
    public void Placeholders_are_refused_outside_simulation()
    {
        var row = ChatRow("grok-4.7", baseUrl: "https://YOUR-RESOURCE.openai.azure.com/openai/v1/");
        Assert.Contains(Problems(row), p => p.Contains("YOUR-RESOURCE"));
        Assert.Empty(ConfigLoader.Validate(row, name => Keys.GetValueOrDefault(name), allowPlaceholders: true));
    }

    [Fact]
    public void Shipped_models_json_lists_the_nine_requested_models_and_two_suggestions()
    {
        var config = ConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "models.json"));
        string[] requested = ["Kimi-K2.6", "claude-haiku-5-5", "claude-opus-5-5", "gpt-6-astra", "gpt-5.4-mini",
            "grok-4.7", "gpt-5.6-terra", "gpt-5.6-luna", "DeepSeek-V4-Flash"];
        Assert.Equal(requested, config.Models.Where(m => m.Enabled).Select(m => m.CatalogId));
        Assert.All(config.Models.Where(m => !m.Enabled), m => Assert.True(m.Suggested));
        Assert.Equal(2, config.Models.Count(m => m.Suggested));
        Assert.All(config.Models, m => Assert.Empty(ConfigLoader.Validate(m, _ => "x", allowPlaceholders: true)));
    }
}
