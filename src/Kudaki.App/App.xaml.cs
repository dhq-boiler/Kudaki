using System.Diagnostics;
using System.IO;
using System.Windows;
using Kudaki.App.ViewModels;
using R3;

namespace Kudaki.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // R3 の WPF SynchronizationContext を初期化。
        // 未処理例外は Debug 出力へ (プロダクションではロガーに差し替え可)。
        WpfProviderInitializer.SetDefaultObservableSystem(
            ex => Debug.WriteLine($"[R3] unhandled: {ex}"));

        base.OnStartup(e);

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
