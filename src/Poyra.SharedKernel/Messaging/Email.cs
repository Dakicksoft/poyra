namespace Poyra.SharedKernel.Messaging;


public sealed record EmailMessage(
    Guid? TenantId,
    string ToEmail,
    string Subject,
    string BodyHtml,
    string BodyText,
    string Purpose);

public interface IEmailQueue
{
    Task EnqueueAsync(EmailMessage message, CancellationToken ct);
}

public interface IEmailTransport
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}
