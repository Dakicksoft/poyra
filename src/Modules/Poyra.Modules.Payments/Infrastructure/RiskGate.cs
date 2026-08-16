using System.Text.Json;
using Poyra.Modules.Payments.Contracts;
using Poyra.Modules.Payments.Domain;
using Poyra.Modules.Routing.Contracts;
using Poyra.SharedKernel.Errors;

namespace Poyra.Modules.Payments.Infrastructure;

public static class RiskGate
{
    public static async Task ApplyAsync(
        IRiskGate risk,
        PaymentsDbContext db,
        PaymentIntent intent,
        CardFacts? cardFacts,
        string? maskedPan,
        string flow,
        CancellationToken ct)
    {
        var decision = await risk.AssessAsync(new RiskContext(
            intent.PublicId, intent.AmountMinor, intent.Currency, intent.Installments, flow,
            intent.CustomerRef, intent.CustomerIp, Country: null,
            cardFacts?.Bin, cardFacts?.BankCode, cardFacts?.Program, cardFacts?.Brand,
            cardFacts?.CardType, cardFacts?.IsCommercial, maskedPan), ct);

        if (decision.Outcome == RiskOutcomes.Allow)
            return;

        intent.RiskDecisionJson = JsonSerializer.Serialize(new
        {
            outcome = decision.Outcome,
            rule = decision.RuleName,
            reason = decision.Reason,
            signals = decision.Signals,
        });

        db.PaymentEvents.Add(PaymentEvent.For(intent, $"risk.{decision.Outcome}", actor: "risk", new
        {
            rule = decision.RuleName,
            reason = decision.Reason,
            flow,
        }));

        if (decision.Blocks)
        {
            await db.SaveChangesAsync(ct);
            throw new PoyraException(409, "risk.blocked",
                decision.Reason ?? "İşlem risk kuralı gereği reddedildi.");
        }

        if (decision.RequiresThreeDs && flow == "direct")
        {
            await db.SaveChangesAsync(ct);
            throw new PoyraException(409, "risk.three_ds_required",
                decision.Reason
                ?? "Bu işlem 3D Secure gerektiriyor — kart doğrulamasız (direct) akış kullanılamaz.");
        }
    }
}
