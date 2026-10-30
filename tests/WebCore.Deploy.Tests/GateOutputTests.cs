/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Deploy;

namespace WebCore.Deploy.Tests;

public class GateOutputTests
{
    [Fact]
    public void LinesThatSayWhy_AreKept_AndTheRestIsLeftOut()
    {
        const string output = "Quick check of what changed\n  Running Ship.Tests: 2 test class(es)...\n  did not complete\n    | error MSB3027: Could not copy Ship.dll\nA check could not run (23 s).";

        var why = GateOutput.Why(output);

        Assert.Equal(["  did not complete", "    | error MSB3027: Could not copy Ship.dll", "A check could not run (23 s)."], why);
    }

    [Fact]
    public void WhenNoLineLooksLikeAReason_TheLastLinesAreShown_NotNothing()
    {
        var output = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"step {i} ok"));

        var why = GateOutput.Why(output, tail: 5);

        Assert.Equal(["step 36 ok", "step 37 ok", "step 38 ok", "step 39 ok", "step 40 ok"], why);
    }

    [Fact]
    public void NoMoreThanTheMostLinesAreMarked()
    {
        var output = string.Join('\n', Enumerable.Range(1, 50).Select(i => $"Ns.C.T{i} [FAIL]"));

        Assert.Equal(30, GateOutput.Why(output).Count);
        Assert.Empty(GateOutput.Why(string.Empty));
    }
}
