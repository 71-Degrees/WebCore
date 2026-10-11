/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Timing;

namespace WebCore.Tests.Timing;

public class DebouncerTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(900);

    private readonly ManualTimeProvider _time = new();

    [Fact]
    public async Task OnlyTheLatestTriggerRuns_AfterTheDelay()
    {
        using var debouncer = new Debouncer(Delay, _ => { }, _time);
        var log = new List<string>();
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        debouncer.Trigger(_ => { log.Add("first"); return Task.CompletedTask; });
        _time.Advance(TimeSpan.FromMilliseconds(500));
        debouncer.Trigger(_ => { log.Add("second"); ran.SetResult(); return Task.CompletedTask; });
        _time.Advance(Delay);
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["second"], log);
    }

    [Fact]
    public async Task Cancel_StopsThePendingAction()
    {
        using var debouncer = new Debouncer(Delay, _ => { }, _time);
        var ran = false;

        debouncer.Trigger(_ => { ran = true; return Task.CompletedTask; });
        debouncer.Cancel();
        _time.Advance(Delay * 2);
        await Task.Yield();

        Assert.False(ran);
    }

    [Fact]
    public void ThereMustBeSomewhereForErrorsToGo() =>
        Assert.Throws<ArgumentNullException>(() => new Debouncer(Delay, null!, _time));
}
