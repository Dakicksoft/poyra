using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Poyra.SharedKernel.Cqrs;
using Shouldly;
using Xunit;

namespace Poyra.Tests.Unit;

// --- Test mesajları ----------------------------------------------------------
public sealed record PingCommand(string Name) : ICommand<string>;

public sealed class PingHandler : ICommandHandler<PingCommand, string>
{
    public Task<string> Handle(PingCommand command, CancellationToken ct)
        => Task.FromResult($"pong:{command.Name}");
}

public sealed class PingValidator : AbstractValidator<PingCommand>
{
    public PingValidator() => RuleFor(x => x.Name).NotEmpty();
}

public sealed record ToplaQuery(int A, int B) : IQuery<int>;

public sealed class ToplaHandler : IQueryHandler<ToplaQuery, int>
{
    public Task<int> Handle(ToplaQuery query, CancellationToken ct)
        => Task.FromResult(query.A + query.B);
}

public sealed record SahipsizCommand : ICommand<int>; // bilerek handler'sız

// --- Testler -----------------------------------------------------------------
public sealed class DispatcherTests
{
    private static IServiceProvider BuildProvider()
        => new ServiceCollection()
            .AddPoyraCqrs(typeof(DispatcherTests).Assembly)
            .BuildServiceProvider(validateScopes: true);

    [Fact]
    public async Task Send_komutu_dogru_handlera_yonlendirmeli()
    {
        using var scope = BuildProvider().CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var result = await dispatcher.Send(new PingCommand("poyra"));

        result.ShouldBe("pong:poyra");
    }

    [Fact]
    public async Task Ask_sorguyu_dogru_handlera_yonlendirmeli()
    {
        using var scope = BuildProvider().CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var result = await dispatcher.Ask(new ToplaQuery(40, 2));

        result.ShouldBe(42);
    }

    [Fact]
    public async Task Send_dogrulama_hatasinda_ValidationException_firlatmali()
    {
        using var scope = BuildProvider().CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var ex = await Should.ThrowAsync<ValidationException>(
            () => dispatcher.Send(new PingCommand("")));

        ex.Errors.ShouldContain(e => e.PropertyName == nameof(PingCommand.Name));
    }

    [Fact]
    public async Task Send_handler_kayitli_degilse_anlasilir_hata_vermeli()
    {
        using var scope = BuildProvider().CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => dispatcher.Send(new SahipsizCommand()));

        ex.Message.ShouldContain(nameof(SahipsizCommand));
    }
}
