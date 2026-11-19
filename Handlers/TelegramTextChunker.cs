namespace Wawshni.Bot.Handlers;

public static class TelegramTextChunker
{
    public static IReadOnlyList<string> Split(string text, int maxLength = 3500)
    {
        if (maxLength < 2) throw new ArgumentOutOfRangeException(nameof(maxLength));
        if (string.IsNullOrEmpty(text)) return [" "];
        var chunks = new List<string>();
        var offset = 0;
        while (offset < text.Length)
        {
            var remaining = text.Length - offset;
            if (remaining <= maxLength) { chunks.Add(text[offset..]); break; }
            var end = offset + maxLength;
            if (char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
            var newline = text.LastIndexOf('\n', end - 1, end - offset);
            if (newline > offset + (maxLength / 2)) end = newline;
            var chunk = text[offset..end].TrimEnd();
            if (chunk.Length > 0) chunks.Add(chunk);
            offset = end;
            while (offset < text.Length && char.IsWhiteSpace(text[offset])) offset++;
        }
        return chunks;
    }
}
