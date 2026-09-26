using CQRSharp.Sample.Infrastructure.SelfTest;
using CQRSharp.Sample.Presentation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.ConfigureContainer(new DefaultServiceProviderFactory(SampleApplication.ValidatingProviderOptions));
builder.Services.AddSampleApplication();

using var host = builder.Build();
await host.StartAsync();

// The host runs (the outbox processor and the background queue need it) while the self-test drives it; Ctrl+C stops it.
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
var passed = await host.Services.GetRequiredService<SelfTestRunner>().RunAsync(lifetime.ApplicationStopping);

await host.StopAsync();
return passed ? 0 : 1;
