using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Kudaki.Installer.Services;

// Kudaki v0.1 の最小インストーラーロジック。
//   - Embedded の Payload.zip を install 先へ展開
//   - スタートメニュー / デスクトップの .lnk を WshShell.CreateShortcut (COM 動的) で作成
//   - HKCU\...\Uninstall\Kudaki に登録 (アンインストーラーは自分自身をコピーして --uninstall 起動)
//   - --uninstall 引数の場合は逆順で撤去
//
// 現状の割り切り:
//   - HKCU に閉じてる (管理者権限不要、ユーザー固有インストール)
//   - .wbs.yaml のファイル関連付けは v0.2 で検討
//   - ロールバックは撤去のみ、部分失敗時は残骸が残り得る (最小 MVP)
public sealed class InstallerService
{
    public const string AppName = "Kudaki";
    public const string ProductName = "Kudaki";
    public const string PublisherName = "dhq_boiler";
    public const string VersionString = "0.1.0";

    // Kudaki.App の実行 EXE 名 (Kudaki.App.csproj で AssemblyName=Kudaki 指定)。
    public const string TargetExeName = "Kudaki.exe";

    // インストール後に配置するインストーラー自身の名前 (アンインストール用)。
    public const string UninstallerExeName = "KudakiUninstaller.exe";

    // HKCU アンインストーラー登録キー。
    private const string UninstallRegKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Kudaki";

    public string DefaultInstallPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Kudaki");

    public async Task InstallAsync(InstallOptions options, IProgress<InstallStep> progress, CancellationToken ct = default)
    {
        // ---- 1. インストール先の準備 ----
        progress.Report(new InstallStep("インストール先を準備中...", 0.05));
        Directory.CreateDirectory(options.InstallPath);

        // ---- 2. Embedded payload の展開 ----
        progress.Report(new InstallStep("ファイルを展開中...", 0.10));
        await Task.Run(() => ExtractPayload(options.InstallPath, progress, ct), ct).ConfigureAwait(false);

        // ---- 3. アンインストーラー本体を配置 ----
        progress.Report(new InstallStep("アンインストーラーを配置中...", 0.80));
        var currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("インストーラー自身のパスを取得できませんでした");
        var uninstallerPath = Path.Combine(options.InstallPath, UninstallerExeName);
        File.Copy(currentExe, uninstallerPath, overwrite: true);

        // ---- 4. ショートカット作成 ----
        var targetExePath = Path.Combine(options.InstallPath, TargetExeName);
        if (options.CreateStartMenuShortcut)
        {
            progress.Report(new InstallStep("スタートメニューにショートカットを作成中...", 0.85));
            CreateShortcut(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    $"{AppName}.lnk"),
                targetExePath,
                options.InstallPath);
        }
        if (options.CreateDesktopShortcut)
        {
            progress.Report(new InstallStep("デスクトップにショートカットを作成中...", 0.90));
            CreateShortcut(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"{AppName}.lnk"),
                targetExePath,
                options.InstallPath);
        }

        // ---- 5. アンインストール情報を HKCU に登録 ----
        progress.Report(new InstallStep("システムに登録中...", 0.95));
        RegisterUninstaller(options.InstallPath, uninstallerPath, targetExePath);

        progress.Report(new InstallStep("完了", 1.0));
    }

    public void Uninstall(IProgress<InstallStep> progress)
    {
        progress.Report(new InstallStep("アンインストール情報を読み取り中...", 0.05));
        var installPath = GetRegisteredInstallLocation();

        progress.Report(new InstallStep("ショートカットを削除中...", 0.20));
        SafeDelete(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            $"{AppName}.lnk"));
        SafeDelete(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"{AppName}.lnk"));

        progress.Report(new InstallStep("レジストリ登録を削除中...", 0.40));
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegKeyPath, throwOnMissingSubKey: false);
        }
        catch { /* 無ければ無視 */ }

        // 自分自身 (アンインストーラー) がインストールディレクトリ内にいるので、
        // ここでは削除できない。cmd を投げて遅延削除する。
        progress.Report(new InstallStep("ファイルを削除中...", 0.60));
        if (installPath is not null && Directory.Exists(installPath))
        {
            ScheduleDirectoryRemoval(installPath);
        }

        progress.Report(new InstallStep("完了", 1.0));
    }

    private static void ExtractPayload(string installPath, IProgress<InstallStep> progress, CancellationToken ct)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("Payload.zip")
            ?? throw new InvalidOperationException("Payload.zip リソースが見つかりません。");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var total = zip.Entries.Count;
        var done = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var destPath = Path.GetFullPath(Path.Combine(installPath, entry.FullName));

            // Zip Slip ガード: 展開先が installPath 配下にあることを確認。
            if (!destPath.StartsWith(Path.GetFullPath(installPath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"不正な zip エントリ: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destPath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);
            }

            done++;
            // 0.10 → 0.80 の間を展開に割り当てる
            var pct = 0.10 + (0.70 * ((double)done / total));
            progress.Report(new InstallStep($"ファイルを展開中... ({done}/{total})", pct));
        }
    }

    // WshShell.CreateShortcut は Windows Script Host の COM オブジェクト経由。
    // NuGet 追加不要、reflection で動的にアクセスする。
    private static void CreateShortcut(string linkPath, string targetPath, string workingDirectory)
    {
        var linkDir = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(linkDir))
        {
            Directory.CreateDirectory(linkDir);
        }
        SafeDelete(linkPath);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell が利用できません");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(linkPath);
            try
            {
                shortcut.TargetPath = targetPath;
                shortcut.WorkingDirectory = workingDirectory;
                shortcut.IconLocation = targetPath;
                shortcut.Description = ProductName;
                shortcut.Save();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void RegisterUninstaller(string installPath, string uninstallerPath, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallRegKeyPath, writable: true);
        key.SetValue("DisplayName", ProductName);
        key.SetValue("DisplayVersion", VersionString);
        key.SetValue("Publisher", PublisherName);
        key.SetValue("InstallLocation", installPath);
        key.SetValue("DisplayIcon", exePath);
        key.SetValue("UninstallString", $"\"{uninstallerPath}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" --uninstall --silent");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("URLInfoAbout", "https://github.com/dhq-boiler/Kudaki");
    }

    private static string? GetRegisteredInstallLocation()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallRegKeyPath);
        return key?.GetValue("InstallLocation") as string;
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 無視 */ }
    }

    // 自プロセスが握っているアンインストーラー本体を含むディレクトリを、
    // プロセス終了後にデタッチ起動した PowerShell から遅延削除する。
    // 単発の rmdir だと EXE が握られたまま失敗して残骸になるので、
    // 一度 tmp にコピーした ps1 を実行、リトライループで確実に消す。
    private static void ScheduleDirectoryRemoval(string dir)
    {
        // 一時ファイル (.ps1) を作って PowerShell に食わせる。
        // 引数長制限や cmd の for /L 括弧のパース事情を避ける。
        var scriptPath = Path.Combine(Path.GetTempPath(),
            $"kudaki-cleanup-{Guid.NewGuid():N}.ps1");
        var script = $@"
Start-Sleep -Seconds 3
for ($i = 0; $i -lt 30; $i++) {{
    try {{
        Remove-Item -Recurse -Force -LiteralPath '{dir.Replace("'", "''")}' -ErrorAction Stop
        break
    }}
    catch {{
        Start-Sleep -Seconds 1
    }}
}}
Remove-Item -Force -LiteralPath $PSCommandPath -ErrorAction SilentlyContinue
";
        File.WriteAllText(scriptPath, script);

        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        };
        System.Diagnostics.Process.Start(psi);
    }
}

public sealed class InstallOptions
{
    public string InstallPath { get; set; } = "";
    public bool CreateStartMenuShortcut { get; set; } = true;
    public bool CreateDesktopShortcut { get; set; } = false;
}

public readonly record struct InstallStep(string Message, double Fraction);
