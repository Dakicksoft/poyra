using System.Collections.Concurrent;
using System.Linq.Expressions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.DependencyInjection;

namespace Poyra.SharedKernel.Cqrs;

public sealed class Dispatcher(IServiceProvider services) : IDispatcher
{
    private static readonly ConcurrentDictionary<Type, object> InvokerCache = new();

    public Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken ct = default)
        => DispatchAsync<TResult>(command, typeof(ICommandHandler<,>), ct);

    public Task<TResult> Ask<TResult>(IQuery<TResult> query, CancellationToken ct = default)
        => DispatchAsync<TResult>(query, typeof(IQueryHandler<,>), ct);

    private async Task<TResult> DispatchAsync<TResult>(object message, Type openHandlerInterface, CancellationToken ct)
    {
        await ValidateAsync(message, ct);

        var handlerInterface = openHandlerInterface.MakeGenericType(message.GetType(), typeof(TResult));
        var handler = services.GetService(handlerInterface)
            ?? throw new InvalidOperationException(
                $"'{message.GetType().Name}' için kayıtlı handler yok ({handlerInterface.Name}). AddPoyraCqrs çağrısına modül assembly'si verildi mi?");

        var invoker = (Func<object, object, CancellationToken, Task<TResult>>)InvokerCache.GetOrAdd(
            message.GetType(), _ => BuildInvoker<TResult>(message.GetType(), handlerInterface));

        return await invoker(handler, message, ct);
    }

    private async Task ValidateAsync(object message, CancellationToken ct)
    {
        var validatorType = typeof(IValidator<>).MakeGenericType(message.GetType());
        List<ValidationFailure>? failures = null;

        foreach (var validator in services.GetServices(validatorType).OfType<IValidator>())
        {
            var result = await validator.ValidateAsync(new ValidationContext<object>(message), ct);
            if (!result.IsValid)
                (failures ??= []).AddRange(result.Errors);
        }

        if (failures is { Count: > 0 })
            throw new ValidationException(failures);
    }

    private static Func<object, object, CancellationToken, Task<TResult>> BuildInvoker<TResult>(
        Type messageType, Type handlerInterface)
    {
        var handleMethod = handlerInterface.GetMethod("Handle")!;

        var handler = Expression.Parameter(typeof(object), "handler");
        var message = Expression.Parameter(typeof(object), "message");
        var token = Expression.Parameter(typeof(CancellationToken), "ct");

        var call = Expression.Call(
            Expression.Convert(handler, handlerInterface),
            handleMethod,
            Expression.Convert(message, messageType),
            token);

        return Expression
            .Lambda<Func<object, object, CancellationToken, Task<TResult>>>(call, handler, message, token)
            .Compile();
    }
}
