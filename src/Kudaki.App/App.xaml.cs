using System.IO;
using System.Windows;
using Kudaki.App.ViewModels;

namespace Kudaki.App;

public partial class App : Application
{
    // コマンドライン引数で最初のパスが .yaml / .wbs.yaml なら起動時に開く。
    //   例: Kudaki.App.exe C:\path\to\plan.wbs.yaml
    // 存在しないパスやエラーは黙って無視 (通常起動と同じ状態で立ち上がる)。
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length == 0) return;

        var path = e.Args[0];
        if (!File.Exists(path)) return;

        // MainWindow が完全に立ち上がってから開く。
        // Dispatcher.BeginInvoke で ContentRendered 相当のタイミングまで遅延させる。
        await Dispatcher.BeginInvoke(async () =>
        {
            if (MainWindow?.DataContext is MainViewModel vm)
            {
                await vm.LoadFromPathAsync(path).ConfigureAwait(true);
            }
        }).Task.ConfigureAwait(false);
    }
}
