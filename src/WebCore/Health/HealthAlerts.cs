/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Health;

/// <summary>Why an alert was raised.</summary>
public enum HealthAlertKind
{
    /// <summary>A signal turned red.</summary>
    Red,

    /// <summary>A signal that was red no longer is.</summary>
    Recovered,

    /// <summary>A signal that should have reported has gone quiet.</summary>
    Stale,
}

/// <summary>What a person is told: short enough for a phone's lock screen.</summary>
public sealed record HealthAlert(string Environment, HealthAlertKind Kind, HealthSignal Signal, string Title, string Message);

/// <summary>Delivers an alert somewhere a person will see it (a phone push, an email). One implementation per channel; the dispatcher calls every one that is configured.</summary>
public interface IHealthNotifier
{
    /// <summary>A short name for logs ("ntfy", "email").</summary>
    string Channel { get; }

    Task NotifyAsync(HealthAlert alert, CancellationToken cancellationToken = default);
}

/// <summary>The rule for when a person is bothered, in one place: when something turns red, when it recovers, and once when something goes quiet. Amber never wakes anyone.</summary>
public static class HealthAlertPolicy
{
    public static HealthAlert? ForTransition(string environment, HealthTransition transition)
    {
        var signal = transition.Signal;
        if (signal.Status == HealthStatus.Red)
        {
            return Make(environment, HealthAlertKind.Red, signal, "red");
        }

        return transition.Previous == HealthStatus.Red ? Make(environment, HealthAlertKind.Recovered, signal, "recovered") : null;
    }

    public static HealthAlert ForStale(string environment, HealthSignal signal, DateTimeOffset now) =>
        Make(environment, HealthAlertKind.Stale, signal, $"quiet since {signal.AsOf.ToUniversalTime():d MMM HH:mm} UTC ({(now - signal.AsOf).TotalHours:0} h ago)");

    private static HealthAlert Make(string environment, HealthAlertKind kind, HealthSignal signal, string what) =>
        new(environment, kind, signal, $"[{environment}] {signal.Title} {what}", signal.Headline + (string.IsNullOrWhiteSpace(signal.Detail) ? string.Empty : " - " + signal.Detail));
}
