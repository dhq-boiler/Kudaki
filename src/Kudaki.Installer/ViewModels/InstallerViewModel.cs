using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kudaki.Installer.Services;
using R3;

namespace Kudaki.Installer.ViewModels;

public enum InstallerStep
{
    Welcome,
    InstallPath,
    Progress,
    Done,
    Error
}

public sealed partial class InstallerViewModel : ObservableObject
{
    private readonly InstallerService _installer = new();

    public BindableReactiveProperty<InstallerStep> Step { get; } = new(InstallerStep.Welcome);
    public BindableReactiveProperty<string> InstallPath { get; }
    public BindableReactiveProperty<bool> CreateStartMenuShortcut { get; } = new(true);
    public BindableReactiveProperty<bool> CreateDesktopShortcut { get; } = new(false);
    public BindableReactiveProperty<double> ProgressFraction { get; } = new(0.0);
    public BindableReactiveProperty<string> ProgressMessage { get; } = new("");
    public BindableReactiveProperty<string?> ErrorMessage { get; } = new(null);
    public BindableReactiveProperty<bool> LaunchAfterInstall { get; } = new(true);

    // Done ページで launcher に流す (VM が UI を持たない原則から、Close は View に任せる Action)
    public Action? RequestClose { get; set; }

    public string ProductNameWithVersion => $"{InstallerService.ProductName} {InstallerService.VersionString}";

    public bool IsUninstallMode { get; }
    public BindableReactiveProperty<string> DoneMessage { get; }

    public InstallerViewModel() : this(uninstallMode: false) { }

    public InstallerViewModel(bool uninstallMode)
    {
        IsUninstallMode = uninstallMode;
        InstallPath = new BindableReactiveProperty<string>(_installer.DefaultInstallPath);
        DoneMessage = new BindableReactiveProperty<string>(
            uninstallMode
                ? $"{InstallerService.ProductName} のアンインストールが完了しました。"
                : $"{InstallerService.ProductName} {InstallerService.VersionString} のインストールが完了しました。");
    }

    // アンインストール時は Loaded 時に自動起動する。
    public async Task RunUninstallAsync()
    {
        Step.Value = InstallerStep.Progress;
        ProgressFraction.Value = 0.0;
        ProgressMessage.Value = "開始しています...";
        ErrorMessage.Value = null;

        var progress = new Progress<InstallStep>(step =>
        {
            ProgressMessage.Value = step.Message;
            ProgressFraction.Value = step.Fraction;
        });

        try
        {
            await Task.Run(() => _installer.Uninstall(progress)).ConfigureAwait(true);
            Step.Value = InstallerStep.Done;
        }
        catch (Exception ex)
        {
            ErrorMessage.Value = ex.Message;
            Step.Value = InstallerStep.Error;
        }
    }

    [RelayCommand]
    private void GoNext()
    {
        Step.Value = Step.Value switch
        {
            InstallerStep.Welcome => InstallerStep.InstallPath,
            InstallerStep.InstallPath => InstallerStep.Progress,
            _ => Step.Value
        };
    }

    [RelayCommand]
    private void GoBack()
    {
        Step.Value = Step.Value switch
        {
            InstallerStep.InstallPath => InstallerStep.Welcome,
            _ => Step.Value
        };
    }

    [RelayCommand]
    private void BrowseInstallPath()
    {
        // ShellCommonDialog を使わず、簡易的に OpenFileDialog を「ディレクトリ選択」に転用するのは
        // WPF では自然でないので、FolderBrowserDialog (Windows Forms) を採用。
        // ProjectReference を増やしたくないので Type.GetType 経由で参照する手もあるが、
        // .NET 10 では Microsoft.Win32.OpenFolderDialog が使える。
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "インストール先を選択",
            FolderName = InstallPath.Value
        };
        if (dlg.ShowDialog() == true)
        {
            // 選ばれたディレクトリの下に Kudaki サブフォルダを作るのが自然。
            // ただし既に末尾が Kudaki ならそのまま採用。
            var chosen = dlg.FolderName;
            if (!chosen.EndsWith("Kudaki", StringComparison.OrdinalIgnoreCase))
            {
                chosen = Path.Combine(chosen, "Kudaki");
            }
            InstallPath.Value = chosen;
        }
    }

    [RelayCommand]
    private async Task RunInstallAsync()
    {
        Step.Value = InstallerStep.Progress;
        ProgressFraction.Value = 0.0;
        ProgressMessage.Value = "開始しています...";
        ErrorMessage.Value = null;

        var progress = new Progress<InstallStep>(step =>
        {
            ProgressMessage.Value = step.Message;
            ProgressFraction.Value = step.Fraction;
        });

        try
        {
            await _installer.InstallAsync(
                new InstallOptions
                {
                    InstallPath = InstallPath.Value,
                    CreateStartMenuShortcut = CreateStartMenuShortcut.Value,
                    CreateDesktopShortcut = CreateDesktopShortcut.Value
                },
                progress).ConfigureAwait(true);

            Step.Value = InstallerStep.Done;
        }
        catch (Exception ex)
        {
            ErrorMessage.Value = ex.Message;
            Step.Value = InstallerStep.Error;
        }
    }

    [RelayCommand]
    private void CloseAndOptionallyLaunch()
    {
        if (LaunchAfterInstall.Value)
        {
            try
            {
                var exePath = Path.Combine(InstallPath.Value, InstallerService.TargetExeName);
                if (File.Exists(exePath))
                {
                    Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
                }
            }
            catch
            {
                // 起動失敗しても閉じ処理は継続する
            }
        }
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke();
    }
}
