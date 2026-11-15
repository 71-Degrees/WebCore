/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Ai;

namespace WebCore.Tests.Ai;

public class GeminiApiTests
{
    private const string Reply = """{"candidates":[{"content":{"parts":[{"text":"Here: "},{"text":"[1,2]"}]}}],"usageMetadata":{"promptTokenCount":12,"candidatesTokenCount":5}}""";

    [Fact]
    public void ReplyText_JoinsTheParts() => Assert.Equal("Here: [1,2]", GeminiApi.ReplyText(Reply));

    [Fact]
    public void ReplyText_IsEmpty_WhenThereAreNoCandidates() => Assert.Equal(string.Empty, GeminiApi.ReplyText("""{"candidates":[]}"""));

    [Fact]
    public void Usage_ReadsTheTokenCounts() => Assert.Equal((12L, 5L), GeminiApi.Usage(Reply));

    [Fact]
    public void Usage_IsZero_WhenTheReplyDoesNotSay() => Assert.Equal((0L, 0L), GeminiApi.Usage("{}"));

    [Fact]
    public void ArrayIn_FindsTheArrayInsideProse() => Assert.Equal(2, GeminiApi.ArrayIn("Sure, here it is: [{\"a\":1},{\"a\":2}] hope that helps").Count);

    [Fact]
    public void ArrayIn_IsEmpty_WhenTheArrayIsNotValid() => Assert.Empty(GeminiApi.ArrayIn("[not json"));
}
