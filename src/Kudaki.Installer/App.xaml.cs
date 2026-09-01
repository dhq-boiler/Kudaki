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

    protected override void OnStartup(StartupEventArgs e)
    {
        WpfProviderInitializer.SetDefaultObservableSystem(
            ex => Debug.WriteLine($"[R3] unhandled: {ex}"));

        IsUninstallMode = e.Args.Any(a => string.Equals(a, "--uninstall", System.StringComparison.OrdinalIgnoreCase));
        IsSilent = e.Args.Any(a => string.Equals(a, "--silent", System.StringComparison.OrdinalIgnoreCase));

        base.OnStartup(e);
    }
}
