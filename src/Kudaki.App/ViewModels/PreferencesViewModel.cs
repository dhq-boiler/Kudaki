using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kudaki.App.Models;
using Kudaki.App.Properties;
using Kudaki.App.Services;

namespace Kudaki.App.ViewModels;

// 環境設定ダイアログの ViewModel。
// Kata の PreferencesViewModel と同じく「左カテゴリ / 右 DataTemplate 切替」構造。
// v0.3 時点は General カテゴリ (Language 選択) のみ。将来 (診断ログ / MCP port など)
// を足すときは Categories に 1 行 + PreferencesWindow.xaml に DataTemplate 1 個。
public sealed partial class PreferencesViewModel : ObservableObject
{
    private readonly ILanguageService _languageService;
    private readonly IAppSettingsStore _settingsStore;

    // View 側で Close を呼び戻す用。true = OK, false = Cancel。
    public Action<bool>? RequestClose { get; set; }

    public string DialogTitle => Strings.Preferences_Title;
    public string OkLabel => Strings.Common_Ok;
    public string CancelLabel => Strings.Common_Cancel;

    public IReadOnlyList<PreferencesCategory> Categories { get; }

    [ObservableProperty] private PreferencesCategory _selectedCategory;

    // ---- General カテゴリ ----
    public string LanguageLabel => Strings.Preferences_Language_Label;
    public string LanguageHint => Strings.Preferences_Language_Hint;

    // 各言語の呼称は自言語で書く。切替中の言語に関わらず同じ表記になるので resx を分けない
    // (System だけは「OS に従う / Follow OS」を切り替えたいので resx 側で管理)。
    public IReadOnlyList<LanguageOption> LanguageOptions { get; } = new[]
    {
        new LanguageOption(AppLanguage.System, Strings.Preferences_Language_System),
        new LanguageOption(AppLanguage.Japanese, "日本語"),
        new LanguageOption(AppLanguage.English, "English"),
    };

    [ObservableProperty] private LanguageOption? _selectedLanguageOption;

    // ---- MCP カテゴリ (v03-mcp-auto-apply t-settings-model) ----
    public string McpAutoApplyLabel => Strings.Preferences_Mcp_AutoApply_Label;
    public string McpAutoApplyHint => Strings.Preferences_Mcp_AutoApply_Hint;

    [ObservableProperty] private bool _autoApplyEnabled;

    public PreferencesViewModel(ILanguageService languageService, IAppSettingsStore settingsStore)
    {
        _languageService = languageService;
        _settingsStore = settingsStore;

        Categories = new[]
        {
            new PreferencesCategory(Strings.Preferences_Category_General, "general"),
            new PreferencesCategory(Strings.Preferences_Category_Mcp, "mcp"),
        };
        _selectedCategory = Categories[0];

        // 現在保存されている言語を選択状態に。
        var current = _languageService.Selected;
        foreach (var opt in LanguageOptions)
        {
            if (opt.Value == current)
            {
                _selectedLanguageOption = opt;
                break;
            }
        }
        _selectedLanguageOption ??= LanguageOptions[0];

        // 現在の AutoApplyPolicy を読み込み
        _autoApplyEnabled = _settingsStore.Load().AutoApply.Enabled;
    }

    [RelayCommand]
    private void Ok()
    {
        // Language 側は LanguageService.Apply が内部で settings.Load → Save してくれる。
        if (SelectedLanguageOption is not null)
        {
            _languageService.Apply(SelectedLanguageOption.Value);
        }
        // AutoApply 側は自分で settings.Load → 部分上書き → Save (LanguageService と同 pattern)。
        var settings = _settingsStore.Load();
        settings.AutoApply.Enabled = AutoApplyEnabled;
        _settingsStore.Save(settings);

        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);
}

// 左カテゴリの1項目。Key は XAML の DataTrigger で使う識別子 (不変)。
public sealed record PreferencesCategory(string DisplayName, string Key);

// Language ComboBox の要素。
public sealed record LanguageOption(AppLanguage Value, string Display);
