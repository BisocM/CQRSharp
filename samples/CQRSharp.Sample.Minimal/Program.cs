// The smallest end-to-end CQRSharp app. With the CQRSharp meta-package the CQRSharp.* usings below are applied
// automatically (global usings); this sample uses ProjectReferences, so it imports them explicitly.
using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Interfaces.Markers.Query;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Mediation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// 1. Build a host and register CQRSharp. AddCqrsGenerated() wires the dispatcher AND the source-generated handler
//    routing in one call — it is the only registration you need.
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

// A query that returns a value, and its handler. The handler is discovered and wired by the CQRSharp source generator
// — there is no manual registration. QueryBase<TResult> supplies the request Context/Metadata plumbing for you.
public sealed class Greet(string name) : QueryBase<string>
{
    public string Name { get; } = name;
}

public sealed class GreetHandler : IQueryHandler<Greet, string>
{
    public Task<string> Handle(Greet query, CancellationToken cancellationToken)
        => Task.FromResult($"Hello, {query.Name}!");
}
