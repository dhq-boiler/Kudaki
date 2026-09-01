using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kudaki.App.Properties;
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
        Title = new BindableReactiveProperty<string>(string.Format(Strings.Update_Title_Format, update.Tag));
        Message = new BindableReactiveProperty<string>(string.Format(
            CanAutoUpdate ? Strings.Update_Message_AutoUpdate_Format : Strings.Update_Message_Manual_Format,
            update.Tag));
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
            ProgressText.Value = string.Format(Strings.Update_Progress_Format, f * 100);
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
            ErrorMessage.Value = string.Format(Strings.Update_Error_DownloadFailed_Format, ex.Message);
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
