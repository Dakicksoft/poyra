using Poyra.SharedKernel.Domain;
using Poyra.SharedKernel.Errors;

namespace Poyra.Modules.Payments.Domain;

public enum AttemptStatus
{
    Created,
    ThreeDsInitiated,
    Captured,
    Failed,
    Voided,
}

public static class AttemptStatusMap
{
    public static readonly IReadOnlyDictionary<AttemptStatus, string> ToDb =
        new Dictionary<AttemptStatus, string>
        {
            [AttemptStatus.Created] = "created",
            [AttemptStatus.ThreeDsInitiated] = "three_ds_initiated",
            [AttemptStatus.Captured] = "captured",
            [AttemptStatus.Failed] = "failed",
            [AttemptStatus.Voided] = "voided",
        };

    public static readonly IReadOnlyDictionary<string, AttemptStatus> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}


public sealed class PaymentAttempt : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }
    public Guid PaymentIntentId { get; init; }
    public int AttemptNo { get; init; }
    public string PublicId { get; private set; } = null!;
    public required string ConnectorKey { get; init; }
    public Guid ConnectorAccountId { get; init; }
    public AttemptStatus Status { get; private set; } = AttemptStatus.Created;
    public long AmountMinor { get; init; }
    public string Currency { get; init; } = "TRY";
    public int Installments { get; init; } = 1;

    public string? RedirectActionUrl { get; private set; }
    public string? RedirectFieldsJson { get; private set; }


    public string RedirectMethod { get; private set; } = "POST";


    public string? ConnectorStateJson { get; private set; }

    public string? ConnectorTxnId { get; private set; }
    public string? AuthCode { get; private set; }
    public string? MaskedPan { get; private set; }


    public string? CardProgram { get; set; }
    public string? CardBank { get; set; }
    public string? CardBrand { get; set; }

    public DateTimeOffset? CapturedAt { get; private set; }

    public int? LatencyMs { get; set; }
    public string? ErrorUnifiedCode { get; private set; }
    public string? ErrorRawCode { get; private set; }
    public string? ErrorRawMessage { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }


    public static PaymentAttempt Open(
        PaymentIntent intent, int attemptNo, string connectorKey, Guid connectorAccountId,
        long? chargedAmountMinor = null)
        => new()
        {
            TenantId = intent.TenantId,
            PaymentIntentId = intent.Id,
            AttemptNo = attemptNo,
            ConnectorKey = connectorKey,
            ConnectorAccountId = connectorAccountId,
            AmountMinor = chargedAmountMinor ?? intent.AmountMinor,
            Currency = intent.Currency,
            Installments = intent.Installments,
        };

    public void AttachCardFacts(string? program, string? bank, string? brand)
    {
        CardProgram = program;
        CardBank = bank;
        CardBrand = brand;
    }

    public void MarkInitiated(
        string actionUrl, string fieldsJson, string method = "POST", string? connectorStateJson = null)
    {
        Transition(AttemptStatus.ThreeDsInitiated, AttemptStatus.Created);
        RedirectActionUrl = actionUrl;
        RedirectFieldsJson = fieldsJson;
        RedirectMethod = method;
        ConnectorStateJson = connectorStateJson;
    }

    public void MarkCaptured(string? authCode, string? connectorTxnId, string? maskedPan, DateTimeOffset capturedAt)
    {
        Transition(AttemptStatus.Captured, AttemptStatus.ThreeDsInitiated);
        AuthCode = authCode;
        ConnectorTxnId = connectorTxnId;
        MaskedPan = maskedPan;
        CapturedAt = capturedAt;
    }

    public void MarkFailed(string unifiedCode, string? rawCode, string? rawMessage)
    {
        Transition(AttemptStatus.Failed, AttemptStatus.Created, AttemptStatus.ThreeDsInitiated);
        ErrorUnifiedCode = unifiedCode;
        ErrorRawCode = rawCode;
        ErrorRawMessage = rawMessage;
    }

    public void MarkDirectAuthorized(
        bool success, string? authCode, string? connectorTxnId, string? maskedPan,
        string unifiedCode, string? rawCode, string? rawMessage, DateTimeOffset now)
    {
        Transition(success ? AttemptStatus.Captured : AttemptStatus.Failed, AttemptStatus.Created);
        MaskedPan = maskedPan;
        if (success)
        {
            AuthCode = authCode;
            ConnectorTxnId = connectorTxnId;
            CapturedAt = now;
        }
        else
        {
            ErrorUnifiedCode = unifiedCode;
            ErrorRawCode = rawCode;
            ErrorRawMessage = rawMessage;
        }
    }

    public void MarkVoided() => Transition(AttemptStatus.Voided, AttemptStatus.Captured);

    private void Transition(AttemptStatus next, params AttemptStatus[] allowedFrom)
    {
        if (!allowedFrom.Contains(Status))
            throw new PoyraException(409, "attempt.invalid_state",
                $"Deneme '{AttemptStatusMap.ToDb[Status]}' → '{AttemptStatusMap.ToDb[next]}' geçişi geçersiz.");

        Status = next;
    }
}
