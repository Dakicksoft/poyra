using Poyra.Modules.Payments.Contracts;
using Poyra.Modules.Payments.Features.ConfirmDirect;
using Poyra.Modules.Payments.Features.CreatePayment;
using Poyra.Modules.Routing.Contracts;
using Poyra.SharedKernel.Cqrs;
using Poyra.SharedKernel.Errors;

namespace Poyra.Modules.Payments.Infrastructure;

public sealed class PaymentInitiator(IDispatcher dispatcher) : IPaymentInitiator
{
    public async Task<ChargeResult> ChargeWithTokenAsync(
        long amountMinor, string currency, string cardToken, string? description,
        string? customerRef, CancellationToken ct)
    {
        // Bu yol yalnız abonelik/yeniden tahsilat tarafından çağrılır: müşteri ekranda
        // değildir, kart token'dan gelir. Kanal rotaya bu şekilde bildirilir.
        var created = await dispatcher.Send(new CreatePaymentCommand(
            amountMinor, currency, description, Installments: 1, ReturnUrl: null,
            CustomerRef: customerRef, CustomerIp: null,
            Channel: PaymentChannels.Subscription), ct);

        try
        {
            var result = await dispatcher.Send(new ConfirmDirectPaymentCommand(
                created.Id, CardNumber: null, ExpiryMonth: null, ExpiryYear: null, Cvv: null,
                HolderName: null, CardToken: cardToken, Program: null, ForceConnectorAccountId: null), ct);

            return new ChargeResult(
                result.Id,
                result.Status == Domain.PaymentStatus.Succeeded,
                result.LastError?.Code,
                result.LastError?.RawCode,
                result.LastError?.Message);
        }
        catch (PoyraException ex)
        {
            return new ChargeResult(created.Id, false, ex.Code, null, ex.Message);
        }
    }
}
