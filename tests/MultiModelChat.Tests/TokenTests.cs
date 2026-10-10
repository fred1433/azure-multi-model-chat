using System.Text.Json;
using MultiModelChat.Simulation;
using static MultiModelChat.Tests.TestRows;

namespace MultiModelChat.Tests;

public class TokenTests
{
    static JsonElement Usage(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Anthropic_input_is_the_sum_of_uncached_cache_creation_and_cache_read()
    {
        var counts = TokenCounts.FromAnthropicUsage(Usage(
            """{"input_tokens":10,"cache_creation_input_tokens":20,"cache_read_input_tokens":30,"output_tokens":4}"""));
        Assert.Equal(60, counts.Input);
        Assert.Equal(4, counts.Output);
        Assert.Equal(10, counts.Details["input_tokens"]);
        Assert.Equal(20, counts.Details["cache_creation_input_tokens"]);
        Assert.Equal(30, counts.Details["cache_read_input_tokens"]);
    }

    [Fact]
    public void OpenAI_cached_tokens_are_already_inside_prompt_tokens()
    {
        var counts = TokenCounts.FromOpenAIUsage(Usage(
            """{"prompt_tokens":60,"completion_tokens":9,"prompt_tokens_details":{"cached_tokens":30},"completion_tokens_details":{"reasoning_tokens":5}}"""));
        Assert.Equal(60, counts.Input);
        Assert.Equal(9, counts.Output); // reasoning tokens are part of completion_tokens, not added again
        Assert.Equal(30, counts.Details["cached_tokens"]);
        Assert.Equal(5, counts.Details["reasoning_tokens"]);
    }

    [Fact]
    public void Absent_field_shows_as_not_available_never_zero()
    {
        var counts = TokenCounts.FromOpenAIUsage(Usage("""{"prompt_tokens":12,"prompt_tokens_details":{"cached_tokens":null}}"""));
        Assert.Null(counts.Output);
        Assert.Null(counts.Details["cached_tokens"]);
        Assert.Equal("12 / n/a", counts.InOut);
        Assert.Equal("n/a / n/a", TokenCounts.FromResponseBody(Protocols.OpenAIChat, """{"choices":[]}""").InOut);
    }

    [Fact]
    public async Task Counts_are_read_from_the_native_usage_block_per_turn()
    {
        await using var server = await FakeFoundryServer.StartAsync();
        server.Responder = (r, _) => Task.FromResult(r.UserTurns == 1
            ? SimulatedFoundry.Message(r.Model!, "Porto", inputTokens: 10, cacheCreationInputTokens: 20, cacheReadInputTokens: 30, outputTokens: 7)
            : SimulatedFoundry.Message(r.Model!, "3", inputTokens: 15, cacheCreationInputTokens: null, cacheReadInputTokens: null, outputTokens: 3));

        var report = await Runner(server).RunAsync(Config(ClaudeRow("claude-haiku-5-5")), TestContext.Current.CancellationToken);

        var turns = report.Models[0].Turns;
        Assert.Equal("60 / 7", turns[0].Tokens.InOut);
        Assert.Equal("15 / 3", turns[1].Tokens.InOut); // absent cache fields are not invented
        Assert.Null(turns[1].Tokens.Details["cache_read_input_tokens"]);
    }
}
