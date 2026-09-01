using System.Diagnostics;
using System.Linq;
using System.Windows;
using R3;

namespace Kudaki.Installer;

public partial class App : Application
{
    // インストーラー自身の起動フラグ。MainWindow から参照する。
    public bool IsUninstallMode { get; private set; }
    public bool IsSilent { get; private set; }
    public bool IsAutoUpdateMode { get; private set; }
    // --pid=<n>: 上書き前に終了を待つ既存の Kudaki プロセス ID (自動更新用)。
    public int? WaitForProcessId { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        WpfProviderInitializer.SetDefaultObservableSystem(
            ex => Debug.WriteLine($"[R3] unhandled: {ex}"));

        IsUninstallMode = e.Args.Any(a => string.Equals(a, "--uninstall", System.StringComparison.OrdinalIgnoreCase));
        IsSilent = e.Args.Any(a => string.Equals(a, "--silent", System.StringComparison.OrdinalIgnoreCase));
        IsAutoUpdateMode = e.Args.Any(a => string.Equals(a, "--auto-update", System.StringComparison.OrdinalIgnoreCase));

        var pidArg = e.Args.FirstOrDefault(a => a.StartsWith("--pid=", System.StringComparison.OrdinalIgnoreCase));
        if (pidArg is not null && int.TryParse(pidArg.AsSpan("--pid=".Length), out var pid))
        {
            WaitForProcessId = pid;
        }

        base.OnStartup(e);
    }
}
