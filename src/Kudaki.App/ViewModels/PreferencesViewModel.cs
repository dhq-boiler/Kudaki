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

    // ---- MCP カテゴリ / 承認待ち通知 (v03-approval-attention t-settings-ui) ----
    public string NotifyHeader => Strings.Preferences_Notify_Header;
    public string NotifyHint => Strings.Preferences_Notify_Hint;
    public string NotifySoundLabel => Strings.Preferences_Notify_Sound_Label;
    public string NotifyFlashLabel => Strings.Preferences_Notify_Flash_Label;
    public string NotifyRestoreLabel => Strings.Preferences_Notify_Restore_Label;
    public string NotifyRepeatLabel => Strings.Preferences_Notify_Repeat_Label;

    [ObservableProperty] private bool _notifySound;
    [ObservableProperty] private bool _notifyFlashTaskbar;
    [ObservableProperty] private bool _notifyRestoreIfMinimized;
    [ObservableProperty] private int _notifyRepeatIntervalSeconds;

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

        // 現在の AutoApplyPolicy / ApprovalNotification を読み込み
        var settings = _settingsStore.Load();
        _autoApplyEnabled = settings.AutoApply.Enabled;
        _notifySound = settings.ApprovalNotification.Sound;
        _notifyFlashTaskbar = settings.ApprovalNotification.FlashTaskbar;
        _notifyRestoreIfMinimized = settings.ApprovalNotification.RestoreIfMinimized;
        _notifyRepeatIntervalSeconds = settings.ApprovalNotification.RepeatIntervalSeconds;
    }

    [RelayCommand]
    private void Ok()
    {
        // Language 側は LanguageService.Apply が内部で settings.Load → Save してくれる。
        // 実際に変更されたときだけ呼ぶ (無駄な Load/Save race を避ける、tab 永続化との衝突予防)。
        if (SelectedLanguageOption is not null && SelectedLanguageOption.Value != _languageService.Selected)
        {
            _languageService.Apply(SelectedLanguageOption.Value);
        }
        // AutoApply / ApprovalNotification は自分で settings.Load → 部分上書き → Save
        // (LanguageService と同 pattern)。変更があったときだけ save する。
        var currentSettings = _settingsStore.Load();
        var notify = currentSettings.ApprovalNotification;
        var dirty = currentSettings.AutoApply.Enabled != AutoApplyEnabled
            || notify.Sound != NotifySound
            || notify.FlashTaskbar != NotifyFlashTaskbar
            || notify.RestoreIfMinimized != NotifyRestoreIfMinimized
            || notify.RepeatIntervalSeconds != NotifyRepeatIntervalSeconds;
        if (dirty)
        {
            currentSettings.AutoApply.Enabled = AutoApplyEnabled;
            notify.Sound = NotifySound;
            notify.FlashTaskbar = NotifyFlashTaskbar;
            notify.RestoreIfMinimized = NotifyRestoreIfMinimized;
            // 負値は「繰り返さない」に丸める (0 と同義、DispatcherTimer に負の Interval は渡せない)。
            notify.RepeatIntervalSeconds = Math.Max(0, NotifyRepeatIntervalSeconds);
            _settingsStore.Save(currentSettings);
        }

        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);
}

// 左カテゴリの1項目。Key は XAML の DataTrigger で使う識別子 (不変)。
public sealed record PreferencesCategory(string DisplayName, string Key);

// Language ComboBox の要素。
public sealed record LanguageOption(AppLanguage Value, string Display);
