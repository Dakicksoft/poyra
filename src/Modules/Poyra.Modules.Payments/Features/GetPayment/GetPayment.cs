using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Poyra.SharedKernel.Cqrs;

namespace Poyra.Modules.Payments.Features.GetPayment;

public sealed record GetPaymentQuery(string PublicId) : IQuery<PaymentResponse?>;

public sealed class GetPaymentHandler(PaymentsDbContext db, PaymentMapper mapper)
    : IQueryHandler<GetPaymentQuery, PaymentResponse?>
{
    public async Task<PaymentResponse?> Handle(GetPaymentQuery query, CancellationToken ct)
    {
        var intent = await db.PaymentIntents.AsNoTracking()
            .SingleOrDefaultAsync(p => p.PublicId == query.PublicId, ct);
        if (intent is null)
            return null;

        var lastAttempt = await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.PaymentIntentId == intent.Id)
            .OrderByDescending(a => a.AttemptNo)
            .FirstOrDefaultAsync(ct);

        return PaymentPresenter.Present(mapper, intent, lastAttempt);
    }
}

public sealed class GetPaymentEndpoint(IDispatcher dispatcher) : EndpointWithoutRequest<PaymentResponse>
{
    public override void Configure()
    {
        Get("/v1/payments/{id}");
        Description(x => x.WithTags("Payments"));
        Summary(s => s.Summary = "Ödemeyi dış kimliğiyle (pay_…) getirir; bekleyen 3DS formu ve son hatayı içerir.");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var id = Route<string>("id")!;
        var result = await dispatcher.Ask(new GetPaymentQuery(id), ct);
        if (result is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(result, ct);
    }
}
