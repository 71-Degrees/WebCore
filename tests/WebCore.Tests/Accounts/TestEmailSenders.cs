/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using System.Text.RegularExpressions;
using WebCore.Email;

namespace WebCore.Tests.Accounts;

/// <summary>An email sender that keeps every message, so a test can read the six-digit code out of the mail.</summary>
internal sealed class CapturingSender : IEmailSender
{
    public List<(string To, string Subject, string Body)> Sent { get; } = new();

    public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        Sent.Add((toEmail, subject, htmlBody));
        return Task.CompletedTask;
    }

    public string LastCode() => Regex.Match(Sent[^1].Body, @"<strong>(\d{6})</strong>").Groups[1].Value;
}

/// <summary>An email sender whose provider refuses every message (throttled, blocked).</summary>
internal sealed class ThrowingSender : IEmailSender
{
    public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("smtp: daily user sending limit exceeded");
}
