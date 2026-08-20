using System.Buffers.Text;
using System.Security.Cryptography;
using Poyra.SharedKernel.Domain;
using Poyra.SharedKernel.Errors;

namespace Poyra.Modules.Payments.Domain;

public sealed class PaymentIntent : ITenantOwned, IAuditable
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid TenantId { get; init; }

    public Guid? ProfileId { get; init; }

    public string PublicId { get; private set; } = null!;

    public long AmountMinor { get; init; }
    public string Currency { get; init; } = "TRY";
    public PaymentStatus Status { get; private set; } = PaymentStatus.Created;
    public string? Description { get; init; }
    public int Installments { get; init; } = 1;

    /// <summary>
    /// İşyerinin müşteri anahtarı. Risk motorunun hız (velocity) sayaçları buna göre
    /// gruplanır; yoksa aynı kişinin 20 denemesi 20 ayrı müşteri gibi görünür.
    /// </summary>
    public string? CustomerRef { get; init; }

    public string? CustomerIp { get; init; }

    public string? RiskDecisionJson { get; set; }

    public string? ReturnUrl { get; init; }

    /// <summary>
    /// Ödemenin doğduğu kanal (bkz. PaymentChannels: api · link · field · subscription).
    /// Oluşturma anında bilinir ve bir daha değişmez — rota kuralları buna bakabilir.
    /// Alan eklenmeden önceki kayıtlarda NULL kalır; geriye dönük "api" yazmak, gerçekte
    /// ödeme linkinden gelmiş bir tahsilata uydurma kanal atfetmek olurdu.
    /// </summary>
    public string? Channel { get; init; }

    /// <summary>Rota kararı + gerekçesi (jsonb) — "neden bu POS" açıklanabilirliği.</summary>
    public string? RoutingResultJson { get; set; }

    public string ClientSecret { get; init; } = null!;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static PaymentIntent Create(
        Guid tenantId, Guid? profileId, Money amount, string? description,
        int installments = 1, string? returnUrl = null,
        string? customerRef = null, string? customerIp = null, string? channel = null)
        => new()
        {
            Channel = channel,
            TenantId = tenantId,
            ProfileId = profileId,
            AmountMinor = amount.AmountMinor,
            Currency = amount.Currency,
            Description = description,
            Installments = installments,
            ReturnUrl = returnUrl,
            CustomerRef = customerRef,
            CustomerIp = customerIp,
            ClientSecret = "psec_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24)),
        };

    public void MarkRequiresAction() => Transition(PaymentStatus.RequiresAction, PaymentStatus.Created);
    public void MarkSucceeded() => Transition(PaymentStatus.Succeeded, PaymentStatus.RequiresAction);

    public void MarkSucceededDirect() => Transition(PaymentStatus.Succeeded, PaymentStatus.Created);
    public void MarkFailed() => Transition(PaymentStatus.Failed, PaymentStatus.Created, PaymentStatus.RequiresAction);
    public void MarkCancelled() => Transition(PaymentStatus.Cancelled, PaymentStatus.Succeeded); // void

    private void Transition(PaymentStatus next, params PaymentStatus[] allowedFrom)
    {
        if (!allowedFrom.Contains(Status))
            throw new PoyraException(409, "payment.invalid_state",
                $"'{PaymentStatusMap.ToDb[Status]}' durumundan '{PaymentStatusMap.ToDb[next]}' durumuna geçilemez.");

        Status = next;
    }
}
