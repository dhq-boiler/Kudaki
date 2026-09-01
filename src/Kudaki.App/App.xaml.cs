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

        var hasStartupFile = e.Args.Length > 0 && File.Exists(e.Args[0]);

        // MCP サーバーを非同期起動 (UI 側の立ち上げをブロックしない)。
        // 完了時に Landing の progress を進める (75%)。起動時にファイル引数がなければ
        // ここから 100% まで進めて Landing を閉じる。
        _mcpHost = new McpHostService();
        _ = Task.Run(async () =>
        {
            try
            {
                await _mcpHost.StartAsync().ConfigureAwait(false);
                Debug.WriteLine($"[Kudaki.Mcp] listening on {_mcpHost.EndpointUrl}");
                MainViewModel.Current?.ReportLoading(75, "MCP サーバー起動");

                if (!hasStartupFile)
                {
                    // Landing を一瞬見せる余韻。ロードするものが何もないので短めで閉じる。
                    await Task.Delay(200).ConfigureAwait(false);
                    MainViewModel.Current?.ReportLoading(100, "準備完了");
                }
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine($"[Kudaki.Mcp] failed to start: {ex}");
                // 起動失敗しても Landing は閉じないと UI が使えないので閉じる。
                MainViewModel.Current?.ReportLoading(100, "MCP サーバー起動失敗 (ログ参照)");
            }
        });

        if (!hasStartupFile) return;
        var path = e.Args[0];

        // MainWindow が完全に立ち上がってから開く。
        // LoadFromPathAsync 側で progress を 80→95→100 に進めるので追加処理は不要。
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
