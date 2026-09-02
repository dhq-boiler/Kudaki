using Kudaki.App.ViewModels;
using Kudaki.App.Views;
using System.Windows;

namespace Kudaki.App.Services;

// 環境設定ダイアログを開くだけの薄いサービス。IFileDialogService / IUpdatePromptService
// と揃えて View 依存を境界にまとめる。
public interface IPreferencesDialogService
{
    // 現在の Owner Window に対して modal で開く。OK が押されたら true。
    bool Show();
}

public sealed class WpfPreferencesDialogService : IPreferencesDialogService
{
    private readonly Window _owner;
    private readonly ILanguageService _languageService;
    private readonly IAppSettingsStore _settingsStore;

    public WpfPreferencesDialogService(Window owner, ILanguageService languageService, IAppSettingsStore settingsStore)
    {
        _owner = owner;
        _languageService = languageService;
        _settingsStore = settingsStore;
    }

    public bool Show()
    {
        var vm = new PreferencesViewModel(_languageService, _settingsStore);
        var dialog = new PreferencesWindow(vm) { Owner = _owner };
        var result = dialog.ShowDialog();
        return result == true;
    }
}
