using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kudaki.App.Services;
using R3;

namespace Kudaki.App.ViewModels;

public sealed partial class UpdatePromptViewModel : ObservableObject
{
    private readonly UpdateInfo _update;
    private readonly UpdateDownloadService _downloader = new();

    public BindableReactiveProperty<string> Title { get; }
    public BindableReactiveProperty<string> Message { get; }
    public BindableReactiveProperty<double> ProgressFraction { get; } = new(0.0);
    public BindableReactiveProperty<string> ProgressText { get; } = new("");
    public BindableReactiveProperty<bool> IsDownloading { get; } = new(false);
    public BindableReactiveProperty<string?> ErrorMessage { get; } = new(null);

    // View 側で View.Close() に折り返す (bool は installer 起動に成功したか)。
    public Action<bool>? RequestClose { get; set; }

    public bool CanAutoUpdate => !string.IsNullOrWhiteSpace(_update.AssetDownloadUrl);

    public UpdatePromptViewModel(UpdateInfo update)
    {
        _update = update;
        Title = new BindableReactiveProperty<string>($"Kudaki {update.Tag} が利用可能です");
        Message = new BindableReactiveProperty<string>(
            CanAutoUpdate
                ? $"新しいバージョン {update.Tag} をダウンロードしてインストールします。" +
                  " インストール中に Kudaki は自動的に再起動します。"
                : $"新しいバージョン {update.Tag} が公開されています。" +
                  " ブラウザで Releases ページを開いて手動でダウンロードしてください。");
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (!CanAutoUpdate) return;
        IsDownloading.Value = true;
        ErrorMessage.Value = null;

        var progress = new Progress<double>(f =>
        {
            ProgressFraction.Value = f;
            ProgressText.Value = $"ダウンロード中... {f * 100:0}%";
        });

        try
        {
            var installerPath = await _downloader
                .DownloadAsync(_update.AssetDownloadUrl!, progress)
                .ConfigureAwait(true);

            // 現プロセスの PID を渡してインストーラーに終了待ちさせる。
            var pid = Environment.ProcessId;
            var psi = new ProcessStartInfo(installerPath, $"--auto-update --pid={pid}")
            {
                UseShellExecute = true
            };
            Process.Start(psi);

            RequestClose?.Invoke(true);
        }
        catch (Exception ex)
        {
            ErrorMessage.Value = $"ダウンロードに失敗したっす: {ex.Message}";
            IsDownloading.Value = false;
        }
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_update.HtmlUrl) { UseShellExecute = true });
        }
        catch { }
        RequestClose?.Invoke(false);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);
}
