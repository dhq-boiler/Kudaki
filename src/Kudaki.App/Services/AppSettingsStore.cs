using System;
using System.IO;
using System.Threading;
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

    // v03-approval-attention t-settings-model: 承認待ちに気づかせるための通知設定。
    // 既定は全部 on (気づかずに timeout する方が実害が大きいので opt-out 方式)。
    public ApprovalNotificationSettings ApprovalNotification { get; set; } = new();
}

// v03-approval-attention: MCP の承認待ちが来たときの呼びかけ方。
// フォアグラウンド奪取は設定項目に置かない (段階的エスカレーション方針として常に行わない)。
public sealed class ApprovalNotificationSettings
{
    public bool Sound { get; set; } = true;
    public bool FlashTaskbar { get; set; } = true;
    public bool RestoreIfMinimized { get; set; } = true;

    // 無反応時の再催促間隔。0 なら 1 回鳴らして終わり。
    public int RepeatIntervalSeconds { get; set; } = 30;
}

// v03-mcp-auto-apply: 承認 UI をスキップする条件。Enabled=false ならすべて承認必須。
// フィールド粒度の on/off (RemainingHours のみ許可 / Notes 追記のみ許可 等) は将来拡張、
// 現状は「全部 auto に許すか、全部承認するか」の 2 択でシンプル運用。
public sealed class AutoApplyPolicy
{
    public bool Enabled { get; set; } = false;
}

// Load の結果。Failed=true は「ファイルはあるのに読めなかった」を意味する。
// ファイルが無いだけ (初回起動) は Failed=false + 既定値で返す。
public sealed record SettingsLoadResult(AppSettings Settings, bool Failed);

public interface IAppSettingsStore
{
    // 失敗を区別せず既定値で潰す簡易版。設定を「読んで書き戻す」用途には使わないこと。
    AppSettings Load();

    // 読み取り失敗を呼び出し元に伝える版。既存データを上書きする可能性がある処理は必ずこちらを使う。
    SettingsLoadResult LoadDetailed();

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

    public AppSettings Load() => LoadDetailed().Settings;

    // 2026-09-03 の事故対応: 旧実装は読み取り例外を握り潰して常に既定値を返していた。
    // アップデート時は新旧プロセスが一瞬重なり、旧プロセスの書き込み途中 (truncate 済みの
    // 空 / 途中ファイル) を新プロセスが読む窓がある。そこで既定値を返すと、呼び出し元が
    // 「タブは 0 個」と信じて空リストを書き戻し、開いていたタブ一覧が永久に消える。
    // 対策は 2 段構え: (1) 短いリトライで一過性の失敗を吸収する
    //                 (2) それでも駄目なら Failed=true と正直に伝えて上書きを止めさせる
    public SettingsLoadResult LoadDetailed()
    {
        if (!File.Exists(_path)) return new SettingsLoadResult(new AppSettings(), false);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var json = File.ReadAllText(_path);
                var parsed = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (parsed is not null) return new SettingsLoadResult(parsed, false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Kudaki.Settings] load attempt {attempt} failed: {ex.Message}");
            }
            Thread.Sleep(120);
        }
        return new SettingsLoadResult(new AppSettings(), true);
    }

    public void Save(AppSettings settings)
    {
        try
        {
            // 一時ファイルに書いてから置換する。File.WriteAllText を直接当てると
            // truncate と write の間に窓ができ、同時に読んだ側が空ファイルを掴む。
            var json = JsonSerializer.Serialize(settings, Options);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // 保存できなくても現セッションの culture は当たっている。次回起動で戻るだけ。
        }
    }
}
