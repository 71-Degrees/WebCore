/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using Microsoft.Extensions.Logging;

namespace WebCore.Email;

/// <summary>Sends one HTML email. Each app chooses the provider (Azure Communication Services, SendGrid, SMTP) in its own startup.</summary>
public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default);
}

/// <summary>Stand-in when no email provider is configured, so nothing that depends on email fails to resolve or throws: it only logs that the send
/// was skipped. Unconfigured is safely off, never a startup crash for a missing secret.</summary>
public sealed class NullEmailSender(ILogger<NullEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        // A log is not the place for a full address: it is kept for the people who read logs, not for the recipient.
        logger.LogWarning("Email not sent (no email provider configured): {Subject} -> {To}", subject, EmailMask.Mask(toEmail));
        return Task.CompletedTask;
    }
}
