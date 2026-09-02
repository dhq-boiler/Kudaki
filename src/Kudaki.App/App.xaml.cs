using System;
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
    // シングルトンインスタンス化のための IPC 識別子。
    // Local\ プレフィックスで同一ユーザーセッション内のみ排他。
    private const string SingleInstanceMutexName = @"Local\Kudaki.SingleInstance";
    private const string SingleInstancePipeName = "Kudaki.Instance";

    // Kudaki プロセス内で走る MCP サーバー (Streamable HTTP stateless)。
    // 起動は OnStartup 内で fire-and-forget、停止は OnExit で待って落とす。
    private McpHostService? _mcpHost;

    // シングルトン化 Coordinator。1 個目は Pipe server を張って、
    // 2 個目からの「開くファイル」や「アクティブ化」要求を受け取る。
    private SingleInstanceCoordinator? _singleInstance;

    // AppSettings の永続化 store。LanguageService と MainViewModel (タブ復元) の両方が共有する。
    // 片方が Save するときは Load → 部分上書き → Save の順で他方の field を維持する。
    public IAppSettingsStore SettingsStore { get; } = new JsonAppSettingsStore();

    // 表示言語サービス。起動時に settings.json をロードして
    // DefaultThreadCurrentUICulture を当てるため、MainWindow ctor より前に Initialize する。
    public ILanguageService LanguageService { get; }

    public App()
    {
        LanguageService = new LanguageService(SettingsStore);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // シングルトン化: 2 個目以降は 1 個目に arg を forward して自身は即座に終了。
        // Mutex 取得に成功したプロセスだけが「本物」として先に進む。
        _singleInstance = new SingleInstanceCoordinator(SingleInstanceMutexName, SingleInstancePipeName);
        if (!_singleInstance.TryAcquire())
        {
            var message = (e.Args.Length > 0 && File.Exists(e.Args[0]))
                ? $"open {Path.GetFullPath(e.Args[0])}"
                : "activate";
            _singleInstance.TryForward(message, TimeSpan.FromSeconds(5));
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown(0);
            return;
        }

        // 表示言語を確定。以降に生成される WPF Window / TextBlock の
        // resx 参照はこの culture で解決される。
        LanguageService.Initialize();

        // R3 の WPF SynchronizationContext を初期化。
        // 未処理例外は Debug 出力へ (プロダクションではロガーに差し替え可)。
        WpfProviderInitializer.SetDefaultObservableSystem(
            ex => Debug.WriteLine($"[R3] unhandled: {ex}"));

        base.OnStartup(e);

        // MainWindow が作られたので Pipe server を張って 2 個目以降の要求を待つ。
        // ここより前だと OnSingleInstanceMessage が MainWindow null で発火し得るので、
        // base.OnStartup 後 (= MainWindow ctor 完了後) に開始する。
        _singleInstance.StartServer(OnSingleInstanceMessage);

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
                await ReportLoadingSafelyAsync(75, Strings.Landing_Status_McpStarted).ConfigureAwait(false);

                if (!hasStartupFile)
                {
                    // Landing を一瞬見せる余韻。ロードするものが何もないので短めで閉じる。
                    await Task.Delay(200).ConfigureAwait(false);
                    await ReportLoadingSafelyAsync(100, Strings.Landing_Status_Ready).ConfigureAwait(false);
                }
            }
            catch (System.Exception ex)
            {
                Debug.WriteLine($"[Kudaki.Mcp] failed to start: {ex}");
                // 起動失敗しても Landing は閉じないと UI が使えないので閉じる。
                await ReportLoadingSafelyAsync(100, Strings.Landing_Status_McpFailed).ConfigureAwait(false);
            }
        });

        // 前回開いてたタブを復元 (t-tab-restore-on-launch)。MainWindow ctor 完了後に BeginInvoke で
        // キューに積む。同 path の重複は OpenInNewTabAsync 側で防ぐので、後段の起動 arg と被っても
        // 1 タブに収まる。復元完了後に PersistWatcher を有効化して、以後の Documents 変化 /
        // ActiveDocument 切替を都度 settings.json に反映 (crash 時も直近状態が残る)。
        Dispatcher.BeginInvoke(new System.Action(async () =>
        {
            if (MainWindow?.DataContext is MainViewModel vm)
            {
                // Restore が個別 path の例外 (例: YAML パース失敗) で throw した場合でも
                // watcher が有効化されないと settings.json が更新されなくなるので finally で確実に呼ぶ。
                try
                {
                    await vm.RestoreOpenDocumentsAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Kudaki.Restore] failed: {ex}");
                }
                finally
                {
                    vm.EnablePersistWatcher();
                }
            }
        }));

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
        // 開いてるタブ一覧を settings.json に永続化 (t-tab-restore-on-launch)。
        // Shutdown フローの最初にやる (crash 時は失われる、shutdown 経由なら次回復元される)。
        try
        {
            if (MainWindow?.DataContext is MainViewModel vm)
            {
                vm.PersistOpenDocuments();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Kudaki.Tabs] persist failed: {ex}");
        }

        // MCP サーバーを終了。ここで await できないので同期待ちに落とす。
        // McpHostService 側で 2s の強制打ち切り timeout を持たせているが、念のため
        // OnExit 側でも 3s の Wait timeout を掛けてプロセスが hang しないようにする。
        try
        {
            var stop = _mcpHost?.StopAsync();
            stop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Kudaki.Mcp] stop failed: {ex}");
        }

        // シングルトン Coordinator を解放 (Pipe server 停止 + Mutex release)。
        try { _singleInstance?.Dispose(); }
        catch (Exception ex) { Debug.WriteLine($"[Kudaki.SingleInstance] dispose failed: {ex}"); }
        _singleInstance = null;

        base.OnExit(e);
    }

    // Pipe server で受信した 2 個目以降の要求を UI thread に marshal して処理する。
    // プロトコル:
    //   "open <absolute-path>"  該当ファイルを開く (現状は単一ドキュメントで置き換え、
    //                            マルチドキュメント UI 実装後は新規タブとして開く)
    //   "activate"               何も開かず前面化のみ
    private void OnSingleInstanceMessage(string message)
    {
        Dispatcher.BeginInvoke(new System.Action(async () =>
        {
            BringMainWindowToFront();

            const string openPrefix = "open ";
            if (message.StartsWith(openPrefix, StringComparison.Ordinal))
            {
                var path = message.Substring(openPrefix.Length);
                if (File.Exists(path) && MainWindow?.DataContext is MainViewModel vm)
                {
                    await vm.LoadFromPathAsync(path).ConfigureAwait(true);
                }
            }
        }));
    }

    // MainWindow を最前面に強制する。最小化されていれば通常状態に戻す。
    // Activate だけだと他アプリからフォアグラウンド奪取が失敗するケースがあるので、
    // Topmost の一瞬 ON/OFF トリックで確実に前に出す (SetForegroundWindow の代替)。
    private void BringMainWindowToFront()
    {
        if (MainWindow is null) return;
        if (MainWindow.WindowState == WindowState.Minimized)
        {
            MainWindow.WindowState = WindowState.Normal;
        }
        MainWindow.Activate();
        MainWindow.Topmost = true;
        MainWindow.Topmost = false;
        MainWindow.Focus();
    }

    // MCP 起動 Task が MainWindow ctor (MainViewModel.Current 設定) より先に完了する race を吸収。
    // UI thread で MainViewModel.Current が用意できるまで最大 5 秒待って ReportLoading する。
    // これがないと MCP 起動失敗時に Landing が閉じず、Kudaki の 2 個目起動でスプラッシュ hang していた。
    private Task ReportLoadingSafelyAsync(int percent, string status)
    {
        var tcs = new TaskCompletionSource();
        Dispatcher.BeginInvoke(new System.Action(async () =>
        {
            for (var i = 0; i < 50 && MainViewModel.Current is null; i++)
            {
                await Task.Delay(100).ConfigureAwait(true);
            }
            MainViewModel.Current?.ReportLoading(percent, status);
            tcs.SetResult();
        }));
        return tcs.Task;
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
