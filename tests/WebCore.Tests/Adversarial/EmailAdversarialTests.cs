/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using Microsoft.Extensions.Logging;
using WebCore.Email;

namespace WebCore.Tests.Adversarial;

// The no-provider email stand-in must not write a full recipient address into the logs.
public sealed class EmailAdversarialTests
{
    [Fact]
    public async Task NullEmailSender_LogsTheMaskedAddress_NeverTheRawOne()
    {
        var logger = new RecordingLogger();
        var sender = new NullEmailSender(logger);

        await sender.SendAsync("sam@example.com", "Your Test code", "<p>body</p>");

        var message = Assert.Single(logger.Messages);
        Assert.Contains("sa***@example.com", message);
        Assert.DoesNotContain("sam@example.com", message);
    }

    private sealed class RecordingLogger : ILogger<NullEmailSender>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
