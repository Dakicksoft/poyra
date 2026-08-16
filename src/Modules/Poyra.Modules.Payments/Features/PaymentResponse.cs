using System.Text.Json;
using Poyra.Modules.Payments.Domain;
using Riok.Mapperly.Abstractions;

namespace Poyra.Modules.Payments.Features;

public sealed record NextActionDto(
    string Type,
    string Url,
    string Method,
    IReadOnlyDictionary<string, string> Fields);

public sealed record PaymentErrorDto(string Code, string? RawCode, string? Message);

public sealed record PaymentResponse
{
    public required string Id { get; init; } // dış kimlik: pay_…
    public required PaymentStatus Status { get; init; } // JSON'da snake_case string
    public required long AmountMinor { get; init; }
    public required string Currency { get; init; }
    public string? Description { get; init; }
    public required string ClientSecret { get; init; }
    public int Installments { get; init; } = 1;

    public long? ChargedAmountMinor { get; init; }

    public string? ReturnUrl { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public NextActionDto? NextAction { get; init; }
    public PaymentErrorDto? LastError { get; init; }
}

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class PaymentMapper
{
    [MapProperty(nameof(PaymentIntent.PublicId), nameof(PaymentResponse.Id))]
    [MapperIgnoreTarget(nameof(PaymentResponse.NextAction))]
    [MapperIgnoreTarget(nameof(PaymentResponse.LastError))]
    [MapperIgnoreTarget(nameof(PaymentResponse.ChargedAmountMinor))]
    [MapperIgnoreSource(nameof(PaymentIntent.RoutingResultJson))]
    public partial PaymentResponse ToResponse(PaymentIntent intent);
}

internal static class PaymentPresenter
{
    public static PaymentResponse Present(PaymentMapper mapper, PaymentIntent intent, PaymentAttempt? lastAttempt)
    {
        var response = mapper.ToResponse(intent);

        if (lastAttempt is not null)
            response = response with { ChargedAmountMinor = lastAttempt.AmountMinor };

        if (intent.Status == PaymentStatus.RequiresAction
            && lastAttempt is { RedirectActionUrl: not null, RedirectFieldsJson: not null })
        {
            response = response with
            {
                NextAction = new NextActionDto(
                    lastAttempt.RedirectMethod == "GET" ? "redirect" : "redirect_form",
                    lastAttempt.RedirectActionUrl,
                    lastAttempt.RedirectMethod,
                    JsonSerializer.Deserialize<Dictionary<string, string>>(lastAttempt.RedirectFieldsJson)!),
            };
        }

        if (intent.Status == PaymentStatus.Failed && lastAttempt?.ErrorUnifiedCode is not null)
        {
            response = response with
            {
                LastError = new PaymentErrorDto(
                    lastAttempt.ErrorUnifiedCode, lastAttempt.ErrorRawCode, lastAttempt.ErrorRawMessage),
            };
        }

        return response;
    }
}
