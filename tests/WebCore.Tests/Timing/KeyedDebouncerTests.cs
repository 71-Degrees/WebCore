/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Timing;

namespace WebCore.Tests.Timing;

public class KeyedDebouncerTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly ManualTimeProvider _time = new();
    private readonly List<Exception> _errors = [];

    private KeyedDebouncer<string> Debouncer(IEqualityComparer<string>? comparer = null) => new(Delay, _errors.Add, _time, comparer);

    private static Func<CancellationToken, Task> Records(List<string> log, string what, TaskCompletionSource? done = null) => _ =>
    {
        log.Add(what);
        done?.TrySetResult();
        return Task.CompletedTask;
    };

    [Fact]
    public async Task TheActionRunsOnceTheDelayHasPassed_AndNotBefore()
    {
        using var debouncer = Debouncer();
        var log = new List<string>();
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        debouncer.Trigger("k", Records(log, "save", ran));
        _time.Advance(Delay - TimeSpan.FromMilliseconds(1));
        Assert.Empty(log);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await ran.Task.WaitAsync(Patience);
        Assert.Equal(["save"], log);
    }

    [Fact]
    public async Task ARepeatedTrigger_ReplacesThePendingOne_SoOnlyTheLastRuns()
    {
        using var debouncer = Debouncer();
        var log = new List<string>();
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        debouncer.Trigger("k", Records(log, "first"));
        _time.Advance(TimeSpan.FromMilliseconds(400));
        debouncer.Trigger("k", Records(log, "second"));
        _time.Advance(TimeSpan.FromMilliseconds(400));   // 800 ms after the first, but only 400 after the second
        Assert.Empty(log);

        debouncer.Trigger("k", Records(log, "third", ran));
        _time.Advance(Delay);
        await ran.Task.WaitAsync(Patience);
        Assert.Equal(["third"], log);
    }

    [Fact]
    public async Task ATriggerAfterTheActionHasRun_RunsAgain()
    {
        using var debouncer = Debouncer();
        var log = new List<string>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        debouncer.Trigger("k", Records(log, "one", first));
        _time.Advance(Delay);
        await first.Task.WaitAsync(Patience);
        debouncer.Trigger("k", Records(log, "two", second));
        _time.Advance(Delay);
        await second.Task.WaitAsync(Patience);

        Assert.Equal(["one", "two"], log);
    }

    [Fact]
    public async Task ANewTrigger_SignalsTheTokenOfAnActionStillRunning()
    {
        using var debouncer = Debouncer();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        debouncer.Trigger("k", async token =>
        {
            started.SetResult(token);
            await finish.Task;
        });
        _time.Advance(Delay);
        var token = await started.Task.WaitAsync(Patience);
        Assert.False(token.IsCancellationRequested);

        debouncer.Trigger("k", _ => Task.CompletedTask);

        Assert.True(token.IsCancellationRequested);
        finish.SetResult();
    }

    [Fact]
    public async Task Cancel_StopsAPendingAction()
    {
        using var debouncer = Debouncer();
        var log = new List<string>();

        debouncer.Trigger("k", Records(log, "never"));
        debouncer.Cancel("k");
        _time.Advance(Delay * 2);
        await Task.Yield();

        Assert.Empty(log);
        debouncer.Cancel("nothing-pending");   // harmless
    }

    [Fact]
    public async Task CancelAll_StopsEveryKey()
    {
        using var debouncer = Debouncer();
        var log = new List<string>();

        debouncer.Trigger("a", Records(log, "a"));
        debouncer.Trigger("b", Records(log, "b"));
        debouncer.CancelAll();
        _time.Advance(Delay * 2);
        await Task.Yield();

        Assert.Empty(log);
    }

    [Fact]
    public async Task Dispose_CancelsWhatIsPending_AndRefusesNewTriggers()
    {
        var debouncer = Debouncer();
        var log = new List<string>();

        debouncer.Trigger("k", Records(log, "pending"));
        debouncer.Dispose();
        debouncer.Trigger("k", Records(log, "after dispose"));
        _time.Advance(Delay * 2);
        await Task.Yield();

        Assert.Empty(log);
    }

    [Fact]
    public async Task DifferentKeys_AreIndependent()
    {
        using var debouncer = Debouncer();
        var log = new List<string>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        debouncer.Trigger("a", Records(log, "a"));
        debouncer.Trigger("b", Records(log, "b"));
        debouncer.Trigger("a", Records(log, "a again", both));   // supersedes only a
        _time.Advance(Delay);
        await both.Task.WaitAsync(Patience);
        await Task.Delay(50);

        Assert.Equivalent(new[] { "b", "a again" }, log);
    }

    [Fact]
    public async Task AFailingAction_GoesToOnError_AndTheNextTriggerStillRuns()
    {
        using var debouncer = Debouncer();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new List<string>();
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        debouncer.Trigger("k", _ =>
        {
            failed.SetResult();
            throw new InvalidOperationException("boom");
        });
        _time.Advance(Delay);
        await failed.Task.WaitAsync(Patience);
        await Task.Delay(50);
        debouncer.Trigger("k", Records(log, "next", ran));
        _time.Advance(Delay);
        await ran.Task.WaitAsync(Patience);

        Assert.Equal("boom", Assert.Single(_errors).Message);
        Assert.Equal(["next"], log);
    }

    [Fact]
    public async Task AnActionThatStopsBecauseItWasSuperseded_IsNotAnError()
    {
        using var debouncer = Debouncer();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        debouncer.Trigger("k", async token =>
        {
            started.SetResult(token);
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                stopped.SetResult();
            }
        });
        _time.Advance(Delay);
        await started.Task.WaitAsync(Patience);
        debouncer.Trigger("k", _ => Task.CompletedTask);   // supersedes: the first action's delay throws OperationCanceledException
        await stopped.Task.WaitAsync(Patience);
        await Task.Delay(50);

        Assert.Empty(_errors);
    }

    [Fact]
    public async Task TheComparerDecidesWhatIsTheSameKey()
    {
        using var debouncer = Debouncer(StringComparer.OrdinalIgnoreCase);
        var log = new List<string>();
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        debouncer.Trigger("Key", Records(log, "first"));
        debouncer.Trigger("KEY", Records(log, "second", ran));
        _time.Advance(Delay);
        await ran.Task.WaitAsync(Patience);
        await Task.Delay(50);

        Assert.Equal(["second"], log);
    }

    [Fact]
    public async Task TheActionContinuesOnTheCallersSynchronizationContext()
    {
        var context = new InlineContext();
        var original = SynchronizationContext.Current;
        SynchronizationContext? seenByAction = null;
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var debouncer = Debouncer();

        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            debouncer.Trigger("k", _ =>
            {
                seenByAction = SynchronizationContext.Current;
                ran.SetResult();
                return Task.CompletedTask;
            });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }

        _time.Advance(Delay);
        await ran.Task.WaitAsync(Patience);

        Assert.Same(context, seenByAction);
    }

    /// <summary>A stand-in for a UI thread: runs what is posted to it straight away, with itself as the current context while it does.</summary>
    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                d(state);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}
