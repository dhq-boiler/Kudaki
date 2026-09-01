using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Kudaki.App.Properties;
using Kudaki.App.Services;
using Kudaki.App.Services.Mcp;
using Kudaki.App.ViewModels;
using R3;

namespace Kudaki.App;

public partial class App : Application
{
    // Kudaki プロセス内で走る MCP サーバー (Streamable HTTP stateless)。
    // 起動は OnStartup 内で fire-and-forget、停止は OnExit で待って落とす。
    private McpHostService? _mcpHost;

    // 表示言語サービス。起動時に settings.json をロードして
    // DefaultThreadCurrentUICulture を当てるため、MainWindow ctor より前に Initialize する。
    public ILanguageService LanguageService { get; } =
        new LanguageService(new JsonAppSettingsStore());

    protected override void OnStartup(StartupEventArgs e)
    {
        // 表示言語を確定。以降に生成される WPF Window / TextBlock の
        // resx 参照はこの culture で解決される。
        LanguageService.Initialize();

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
                MainViewModel.Current?.ReportLoading(75, Strings.Landing_Status_McpStarted);

                if (!hasStartupFile)
                {
                    // Landing を一瞬見せる余韻。ロードするものが何もないので短めで閉じる。
                    await Task.Delay(200).ConfigureAwait(false);
                    MainViewModel.Current?.ReportLoading(100, Strings.Landing_Status_Ready);
                }
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine($"[Kudaki.Mcp] failed to start: {ex}");
                // 起動失敗しても Landing は閉じないと UI が使えないので閉じる。
                MainViewModel.Current?.ReportLoading(100, Strings.Landing_Status_McpFailed);
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
        // McpHostService 側で 2s の強制打ち切り timeout を持たせているが、念のため
        // OnExit 側でも 3s の Wait timeout を掛けてプロセスが hang しないようにする。
        try
        {
            var stop = _mcpHost?.StopAsync();
            stop?.Wait(System.TimeSpan.FromSeconds(3));
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
