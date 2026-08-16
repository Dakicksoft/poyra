namespace Poyra.Modules.Payments.Domain;

public enum PaymentStatus
{
    Created,
    RequiresPaymentMethod,
    RequiresConfirmation,
    RequiresAction, // 3DS bekleniyor
    Processing,
    RequiresCapture,
    PartiallyCaptured,
    Succeeded,
    Failed,
    Cancelled,
    Expired,
}

public static class PaymentStatusMap
{
    public static readonly IReadOnlyDictionary<PaymentStatus, string> ToDb =
        new Dictionary<PaymentStatus, string>
        {
            [PaymentStatus.Created] = "created",
            [PaymentStatus.RequiresPaymentMethod] = "requires_payment_method",
            [PaymentStatus.RequiresConfirmation] = "requires_confirmation",
            [PaymentStatus.RequiresAction] = "requires_action",
            [PaymentStatus.Processing] = "processing",
            [PaymentStatus.RequiresCapture] = "requires_capture",
            [PaymentStatus.PartiallyCaptured] = "partially_captured",
            [PaymentStatus.Succeeded] = "succeeded",
            [PaymentStatus.Failed] = "failed",
            [PaymentStatus.Cancelled] = "cancelled",
            [PaymentStatus.Expired] = "expired",
        };

    public static readonly IReadOnlyDictionary<string, PaymentStatus> FromDb =
        ToDb.ToDictionary(kv => kv.Value, kv => kv.Key);
}
