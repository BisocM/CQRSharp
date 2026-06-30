using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// 1. Register CQRSharp. AddCqrsGenerated() wires the dispatcher AND the source-generated handler routing in one call.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddCqrsGenerated();

using var host = builder.Build();
await host.StartAsync();

// 2. CQRSharp services are scoped, so resolve ICqrsDispatcher from a scope, then Send the request.
using (var scope = host.Services.CreateScope())
{
    var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
    var greeting = await dispatcher.Send(new Greet("world"));
    Console.WriteLine(greeting); // -> Hello, world!
}

await host.StopAsync();

// A query that returns a value, and its handler. The handler is discovered and wired by the CQRSharp source generator.
// The CQRSharp.* types here (QueryBase, IQueryHandler, ICqrsDispatcher, AddCqrsGenerated) need no using directives —
// the meta-package applies them as global usings.
public sealed class Greet(string name) : QueryBase<string>
{
    public string Name { get; } = name;
}

public sealed class GreetHandler : IQueryHandler<Greet, string>
{
    public Task<string> Handle(Greet query, CancellationToken cancellationToken)
        => Task.FromResult($"Hello, {query.Name}!");
}
