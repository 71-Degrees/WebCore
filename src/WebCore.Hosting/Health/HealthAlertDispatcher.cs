/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Health;

namespace WebCore.Hosting.Health;

/// <summary>Sends an alert down every configured channel. One channel failing (a phone service being down) is logged and never stops the others, and never fails the publish that raised the alert.</summary>
public sealed class HealthAlertDispatcher(IEnumerable<IHealthNotifier> notifiers, ILogger<HealthAlertDispatcher> log)
{
    private readonly IReadOnlyList<IHealthNotifier> _notifiers = notifiers.ToList();

    /// <summary>How many channels are set up (0 means alerts go nowhere, which the dashboard says).</summary>
    public int ChannelCount => _notifiers.Count;

    public async Task DispatchAsync(IEnumerable<HealthAlert> alerts, CancellationToken cancellationToken = default)
    {
        foreach (var alert in alerts)
        {
            foreach (var notifier in _notifiers)
            {
                try
                {
                    await notifier.NotifyAsync(alert, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
                {
                    log.LogWarning(ex, "The {Channel} alert for {Signal} could not be sent; the other channels still were.", notifier.Channel, alert.Signal.Id);
                }
            }
        }
    }
}
