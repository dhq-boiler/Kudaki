using System.Globalization;
using Kudaki.App.Models;

namespace Kudaki.App.Services;

public interface ILanguageService
{
    AppLanguage Selected { get; }

    // 選択を保存し、現在のスレッドにも culture を当てる。
    // WPF は文字列を生成時 1 回しか読まないので、既に開いている画面は変わらない。
    // 中途半端に混ざるより「次に開く画面から揃う」ほうが分かりやすい。
    void Apply(AppLanguage language);

    // 最初の Window を作る前に呼ぶ。settings.json をロードして culture を当てる。
    void Initialize();
}

public sealed class LanguageService : ILanguageService
{
    private readonly IAppSettingsStore _store;
    private AppSettings _settings = new();

    public LanguageService(IAppSettingsStore store) => _store = store;

    public AppLanguage Selected => _settings.Language;

    public void Initialize()
    {
        _settings = _store.Load();
        ApplyToThread(_settings.Language);
    }

    public void Apply(AppLanguage language)
    {
        // 別コードパスが同時期にファイルを触った可能性があるので、書く直前に読み直す。
        // キャッシュだけ更新して保存すると相手側の書き込みを踏み潰す。
        _settings = _store.Load();
        _settings.Language = language;
        _store.Save(_settings);
        ApplyToThread(language);
    }

    private static void ApplyToThread(AppLanguage language)
    {
        var culture = Resolve(language);
        if (culture is null)
        {
            // OS 追従。既定のまま触らない
            CultureInfo.DefaultThreadCurrentUICulture = null;
            return;
        }

        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    // OS 追従なら null を返す。
    private static CultureInfo? Resolve(AppLanguage language) => language switch
    {
        AppLanguage.Japanese => new CultureInfo("ja"),
        AppLanguage.English => new CultureInfo("en"),
        _ => null,
    };
}
