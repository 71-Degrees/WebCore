/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebCore.Health;
using WebCore.Hosting.Health;
using WebCore.Storage;
using WebCore.Timing;

namespace WebCore.Hosting.Tests;

/// <summary>Alerts reach a person once, through every working channel, and a silent nightly job is noticed without nagging.</summary>
public sealed class HealthAlertingTests : IDisposable
{
    private sealed class RecordingNotifier(string channel, bool fails = false) : IHealthNotifier
    {
        public List<HealthAlert> Sent { get; } = [];

        public string Channel => channel;

        public Task NotifyAsync(HealthAlert alert, CancellationToken cancellationToken = default)
        {
            if (fails)
            {
                throw new HttpRequestException("the phone service is down");
            }

            Sent.Add(alert);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wc-health-alert-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly HealthSignalStore _store;

    public HealthAlertingTests() => _store = new HealthSignalStore(new FileDocumentStore(_folder));

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private HealthSignal Nightly(string id, double hoursAgo) =>
        new(id, "Quizzes", "Quiz checks", HealthStatus.Green, "all fine", _time.GetUtcNow().AddHours(-hoursAgo), ExpectedEveryHours: 24);

    private static HealthAlert AnAlert(HealthAlertKind kind) =>
        new("production", kind, new HealthSignal("q", "Quizzes", "Quiz checks", HealthStatus.Red, "3 problems", DateTimeOffset.UtcNow), "[production] Quiz checks red", "3 problems");

    [Fact]
    public async Task AFailingChannel_DoesNotStopTheOthers_OrFailTheCaller()
    {
        var broken = new RecordingNotifier("push", fails: true);
        var working = new RecordingNotifier("email");
        var dispatcher = new HealthAlertDispatcher([broken, working], NullLogger<HealthAlertDispatcher>.Instance);

        await dispatcher.DispatchAsync([AnAlert(HealthAlertKind.Red)]);

        Assert.Single(working.Sent);
        Assert.Equal(2, dispatcher.ChannelCount);
    }

    [Fact]
    public async Task ASignalThatWentQuiet_IsAnnouncedOnce_AndAgainOnlyAfterItReportedAndWentQuietAgain()
    {
        var notifier = new RecordingNotifier("push");
        var watchdog = new HealthWatchdog(_store, new HealthAlertDispatcher([notifier], NullLogger<HealthAlertDispatcher>.Instance), new HealthEnvironment("production"), _time, NullLogger<HealthWatchdog>.Instance);

        await _store.PublishAsync("production", [Nightly("quiz-checks", hoursAgo: 40)]);
        Assert.Equal(1, await watchdog.CheckOnceAsync());
        Assert.Equal(0, await watchdog.CheckOnceAsync());
        Assert.Single(notifier.Sent);

        await _store.PublishAsync("production", [Nightly("quiz-checks", hoursAgo: 1)]);
        Assert.Equal(0, await watchdog.CheckOnceAsync());
        _time.Advance(TimeSpan.FromHours(48));
        Assert.Equal(1, await watchdog.CheckOnceAsync());
        Assert.Equal(2, notifier.Sent.Count);
        Assert.All(notifier.Sent, a => Assert.Equal(HealthAlertKind.Stale, a.Kind));
    }

    [Fact]
    public async Task TheWatchdog_OnlyLooksAtItsOwnEnvironment()
    {
        var notifier = new RecordingNotifier("push");
        var watchdog = new HealthWatchdog(_store, new HealthAlertDispatcher([notifier], NullLogger<HealthAlertDispatcher>.Instance), new HealthEnvironment("production"), _time, NullLogger<HealthWatchdog>.Instance);

        await _store.PublishAsync("staging", [Nightly("quiz-checks", hoursAgo: 90)]);

        Assert.Equal(0, await watchdog.CheckOnceAsync());
        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task APhonePush_IsOnePostToTheTopic_UrgentForRed_WithAPlainAsciiTitle()
    {
        var handler = new FakeHandler();
        var notifier = new NtfyNotifier(new FakeFactory(handler), Options.Create(new NtfyOptions { Url = "https://ntfy.example/private-topic", ClickUrl = "https://site/admin/health" }));

        await notifier.NotifyAsync(AnAlert(HealthAlertKind.Red) with { Title = "[production] Quiz checks – red" });

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://ntfy.example/private-topic", request.RequestUri!.ToString());
        Assert.Equal("urgent", request.Headers.GetValues("Priority").Single());
        Assert.Equal("[production] Quiz checks - red", request.Headers.GetValues("Title").Single());
        Assert.Equal("https://site/admin/health", request.Headers.GetValues("Click").Single());
    }

    [Fact]
    public async Task WithNoTopicConfigured_NothingIsSent()
    {
        var handler = new FakeHandler();
        var notifier = new NtfyNotifier(new FakeFactory(handler), Options.Create(new NtfyOptions()));

        await notifier.NotifyAsync(AnAlert(HealthAlertKind.Red));

        Assert.Empty(handler.Requests);
    }
}
