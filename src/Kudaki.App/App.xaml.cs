using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Kudaki.App.Services.Mcp;
using Kudaki.App.ViewModels;
using R3;

namespace Kudaki.App;

public partial class App : Application
{
    // Kudaki プロセス内で走る MCP サーバー (Streamable HTTP stateless)。
    // 起動は OnStartup 内で fire-and-forget、停止は OnExit で待って落とす。
    private McpHostService? _mcpHost;

    protected override void OnStartup(StartupEventArgs e)
    {
        // R3 の WPF SynchronizationContext を初期化。
        // 未処理例外は Debug 出力へ (プロダクションではロガーに差し替え可)。
        WpfProviderInitializer.SetDefaultObservableSystem(
            ex => Debug.WriteLine($"[R3] unhandled: {ex}"));

        base.OnStartup(e);

        // MCP サーバーを非同期起動 (UI 側の立ち上げをブロックしない)。
        _mcpHost = new McpHostService();
        _ = Task.Run(async () =>
        {
            try
            {
                await _mcpHost.StartAsync().ConfigureAwait(false);
                Debug.WriteLine($"[Kudaki.Mcp] listening on {_mcpHost.EndpointUrl}");
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine($"[Kudaki.Mcp] failed to start: {ex}");
            }
        });

        // コマンドライン引数で最初のパスが .yaml / .wbs.yaml なら起動時に開く。
        //   例: Kudaki.App.exe C:\path\to\plan.wbs.yaml
        if (e.Args.Length == 0) return;

        var path = e.Args[0];
        if (!File.Exists(path)) return;

        // MainWindow が完全に立ち上がってから開く。
        Dispatcher.BeginInvoke(new System.Action(async () =>
        {
            if (MainWindow?.DataContext is MainViewModel vm)
            {
                await vm.LoadFromPathAsync(path).ConfigureAwait(true);
            }
        }));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // MCP サーバーを終了。ここで await できないので同期待ちに落とす。
        // shutdown 経路で例外が飛んでも Kudaki 本体は既に落ちる方向なので握り潰す。
        try
        {
            _mcpHost?.StopAsync().GetAwaiter().GetResult();
        }
        catch (System.Exception ex)
        {
            Debug.WriteLine($"[Kudaki.Mcp] stop failed: {ex}");
        }
        base.OnExit(e);
    }

    // MainWindow の InitializeComponent 後に呼ぶ。GitHub API を fire-and-forget。
    internal void ScheduleUpdateCheck()
    {
        Dispatcher.BeginInvoke(new System.Action(async () =>
        {
            if (MainWindow?.DataContext is MainViewModel vm)
            {
                await vm.CheckForUpdatesAsync().ConfigureAwait(true);
            }
        }));
    }
}
