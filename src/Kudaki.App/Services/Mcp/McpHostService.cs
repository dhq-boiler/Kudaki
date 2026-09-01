using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace Kudaki.App.Services.Mcp;

// Kudaki.App プロセス内で Streamable HTTP (stateless) の MCP サーバーを立てる。
// project-mcp-roadmap の方針:
//   Transport: Streamable HTTP (stateless) — MCP 2025 仕様の最新
//   Host: WPF プロセス内 ASP.NET Core Kestrel (in-process)
//   Tools: 同アセンブリ内の [McpServerToolType] 属性クラスを自動発見
public sealed class McpHostService
{
    public const int DefaultPort = 27650;

    private WebApplication? _app;
    private CancellationTokenSource? _lifetimeCts;

    public int Port { get; private set; }
    public bool IsRunning => _app is not null;
    public string EndpointUrl => $"http://localhost:{Port}/mcp";

    public async Task StartAsync(int port = DefaultPort, CancellationToken ct = default)
    {
        if (_app is not null) return;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // WPF が Environment.CurrentDirectory を弄る可能性を回避
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = "Kudaki.Mcp",
        });

        // WPF ログには混ぜたくないので console/EventLog を捨てる (Debug は残す)
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.WebHost.UseUrls($"http://localhost:{port}");

        builder.Services
            .AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithToolsFromAssembly();  // Kudaki.App アセンブリ内の [McpServerToolType] を拾う

        _app = builder.Build();
        _app.MapMcp("/mcp");

        _lifetimeCts = new CancellationTokenSource();
        await _app.StartAsync(_lifetimeCts.Token).ConfigureAwait(false);
        Port = port;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        var app = _app;
        var cts = _lifetimeCts;
        if (app is null) return;

        try
        {
            await app.StopAsync(ct).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            cts?.Dispose();
            _app = null;
            _lifetimeCts = null;
            Port = 0;
        }
    }
}
