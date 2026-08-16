namespace Poyra.Modules.Webhooks.Infrastructure;

public static class WebhookRetryPolicy
{
    public static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(24),
    ];

    public static int MaxAttempts => Delays.Length + 1;

    public static TimeSpan? NextDelay(int attemptCountAfterFailure)
        => attemptCountAfterFailure >= MaxAttempts
            ? null
            : Delays[attemptCountAfterFailure - 1];
}
