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

        // Kestrel は port 衝突時に「Failed to bind」を投げるが、環境によっては起動が
        // 数秒 hang したり、内部で握られて呼び出し元まで伝わらないことがある。
        // 先に TcpListener で明示的に検査して、衝突時は即例外にする。
        // (シングルトン化前の防波堤; B ルート実装後も別ソフトによる占有ケースで役立つ)
        if (!IsPortAvailable(port))
        {
            throw new InvalidOperationException(
                $"MCP port {port} is already in use. Another Kudaki instance (or another program) may be listening.");
        }

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
            // Kestrel の default graceful は 30 秒待つ。Kudaki の shutdown で
            // UI スレッドを 30 秒ブロックする(= プロセスが残る)のは筋悪なので、
            // 内部 lifetime を即キャンセルしてから最大 2 秒で強制終了する。
            cts?.Cancel();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                await app.StopAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* 想定内 (2s タイムアウト) */ }
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

    private static bool IsPortAvailable(int port)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }
}
