using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CQRSharp.Tests.Integrations.AspNetCore;

/// <summary>
///     Spins up an in-memory minimal-API host. No CQRSharp requests or handlers are declared by these tests (endpoints
///     return results / throw exceptions directly), so nothing is added to the assembly-wide generated registration.
/// </summary>
internal sealed class AspNetCoreTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private AspNetCoreTestHost(WebApplication app)
    {
        _app = app;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public static async Task<AspNetCoreTestHost> StartAsync(Action<IServiceCollection>? configureServices,
        Action<WebApplication> configureApp)
    {
        // Production, so no developer exception page competes with the exception-handler middleware.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        configureApp(app);
        await app.StartAsync();
        return new AspNetCoreTestHost(app);
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}
