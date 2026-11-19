using Wawshni.Bot.Handlers;
using Xunit;

namespace Wawshni.Bot.Tests;

public sealed class TelegramTextChunkerTests
{
    [Fact]
    public void Splits_long_messages_at_line_boundaries_and_keeps_all_text()
    {
        var input = string.Join("\n", Enumerable.Repeat("واجب عربي وتفاصيله", 20));
        var chunks = TelegramTextChunker.Split(input, 60);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 60));
        Assert.Equal(input.Replace("\n", ""), string.Concat(chunks).Replace("\n", ""));
    }

    [Fact]
    public void Does_not_split_surrogate_pairs()
    {
        var chunks = TelegramTextChunker.Split(new string('a', 4) + "😀" + new string('b', 4), 5);
        Assert.Equal(3, chunks.Count);
        Assert.DoesNotContain(chunks, chunk => chunk.EndsWith('\uD83D') || chunk.StartsWith('\uDE00'));
        Assert.Equal("aaaa😀bbbb", string.Concat(chunks));
    }

    [Fact]
    public void Empty_message_produces_a_telegram_safe_chunk() => Assert.Equal(new[] { " " }, TelegramTextChunker.Split(""));
}
