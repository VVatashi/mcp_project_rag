using ProjectRag.Core.Code.Chunks;

namespace ProjectRag.Code.Tests;

internal static class ChunkAssertions
{
    public static void AssertInvariants(string path, string content, IReadOnlyList<CodeChunk> chunks, CodeChunkingOptions options)
    {
        int[] lineEnds = new int[content.Length + 1];
        for (int index = 0; index < content.Length; index++)
            lineEnds[index + 1] = lineEnds[index] + (content[index] == '\n' ? 1 : 0);

        int previousStart = 0;
        int previousEnd = 0;
        foreach (CodeChunk chunk in chunks)
        {
            string where = $"{path}: {chunk.Heading}";
            Assert.True(chunk.Start >= 0 && chunk.End > chunk.Start && chunk.End <= content.Length, where);
            Assert.True(chunk.End - chunk.Start <= options.MaxChars, where);
            Assert.True(chunk.OverlapChars >= 0 && chunk.OverlapChars < chunk.End - chunk.Start, where);
            Assert.True(chunk.Start >= previousStart, where);
            Assert.True(chunk.Start + chunk.OverlapChars >= previousEnd, where);
            Assert.True(chunk.Heading.StartsWith(path + " > ", StringComparison.Ordinal), where);
            Assert.Equal(1 + lineEnds[chunk.Start], chunk.LineStart);
            Assert.Equal(1 + lineEnds[chunk.End - 1], chunk.LineEnd);
            Assert.False(content.AsSpan(chunk.Start, chunk.End - chunk.Start).IsWhiteSpace(), where);
            previousStart = chunk.Start;
            previousEnd = chunk.End;
        }
    }
}
