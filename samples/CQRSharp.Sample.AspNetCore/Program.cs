// A minimal API over CQRSharp. Run it and try, for example:
//   curl -i -X POST localhost:5000/orders -H "Idempotency-Key: k1" -H "Content-Type: application/json" -d '{"sku":"A-1","quantity":2}'
//   (the same again answers with the same order; another body under k1 is a 422; quantity 0 is a 400 validation problem)
//   curl -i localhost:5000/orders/{id}        (a caller gets three lookups at once, then one a second; past that, a 429 with Retry-After)
//   curl -i -X DELETE localhost:5000/orders/{id}
using System.Text.Json;
using CQRSharp.Sample.AspNetCore;
using CQRSharp.Sample.AspNetCore.Orders;
using CQRSharp.Sample.AspNetCore.SelfTest;

// With the switch, the app serves on a free loopback port, calls itself, and exits with the verdict (CI runs it so).
const string SelfTestSwitch = "--self-test";
var selfTest = args.Contains(SelfTestSwitch);

var builder = WebApplication.CreateSlimBuilder(args.Where(arg => arg != SelfTestSwitch).ToArray());
if (selfTest) builder.WebHost.UseUrls("http://127.0.0.1:0");

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, SampleJsonContext.Default));
builder.Services.AddHttpContextAccessor();

builder.Services.AddCqrsGenerated(b => b
    .UseIdempotency(i => i.ReplayResultsWith(new JsonSerializerOptions { TypeInfoResolver = SampleJsonContext.Default }))
    .UseRateLimiting(o =>
    {
        o.MaxTokens = 3;
        o.ReplenishRatePerSecond = 1;
    })
    .ValidateOnStart());
builder.Services.AddCqrsProblemDetails();
builder.Services.AddSingleton<OrderBook>();

var app = builder.Build();
app.UseExceptionHandler();
app.MapOrders();

if (!selfTest)
{
    await app.RunAsync();
    return 0;
}

await app.StartAsync();
var passed = await HttpSelfTest.RunAsync(new Uri(app.Urls.Single()), app.Logger, app.Lifetime.ApplicationStopping);
await app.StopAsync();
return passed ? 0 : 1;
