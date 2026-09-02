using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kudaki.App.Models;

namespace Kudaki.App.Services;

// アプリ全体の設定 (ドキュメントに紐付かないもの)。
// 現状は Language + タブ復元。将来 (診断ログ on/off や MCP port など) はここに増やす。
public sealed class AppSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AppLanguage Language { get; set; } = AppLanguage.System;

    // v0.3 t-tab-restore-on-launch: 起動時に復元するタブ (絶対パス列、開かれた順)。
    // null / 空リストなら復元しない。壊れたファイルはスキップして継続。
    public System.Collections.Generic.List<string> OpenDocuments { get; set; } = new();

    // v0.3 t-tab-restore-on-launch: 前回アクティブだったタブのパス。復元後に該当タブへ切替。
    // null なら最後に開いた doc がそのままアクティブ。
    public string? ActiveDocumentPath { get; set; }

    // v03-mcp-auto-apply t-settings-model: MCP propose_changes を承認 UI 経由せず
    // 即適用するかのユーザー設定。ユーザーが「上限」を決める、AI 側は緩められない
    // (Fable レビュー: AI が引数で auto-apply 要求するのは事故の元)。
    public AutoApplyPolicy AutoApply { get; set; } = new();
}

// v03-mcp-auto-apply: 承認 UI をスキップする条件。Enabled=false ならすべて承認必須。
// フィールド粒度の on/off (RemainingHours のみ許可 / Notes 追記のみ許可 等) は将来拡張、
// 現状は「全部 auto に許すか、全部承認するか」の 2 択でシンプル運用。
public sealed class AutoApplyPolicy
{
    public bool Enabled { get; set; } = false;
}

public interface IAppSettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

// %LOCALAPPDATA%/Kudaki/settings.json に置く。
// 起動を止めないため、読み書きの失敗は握りつぶして既定値で続ける (Kata と同方針)。
public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    private readonly string _path;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 2026-09-02 発覚バグ対策: PropertyNamingPolicy は書き込み時のみ効くケースがあり、
        // 読み込み時に disk の "openDocuments" (camelCase) を C# の OpenDocuments (PascalCase) に
        // マップし損ねて default (new List<string>()) が返り、次の Save で openDocuments=[] を
        // 書き出す症状があった。case-insensitive にして両方向マッチを保証する。
        PropertyNameCaseInsensitive = true,
    };

    public JsonAppSettingsStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kudaki");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // 保存できなくても現セッションの culture は当たっている。次回起動で戻るだけ。
        }
    }
}
