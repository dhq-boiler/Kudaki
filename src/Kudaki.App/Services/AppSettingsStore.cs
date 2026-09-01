using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kudaki.App.Models;

namespace Kudaki.App.Services;

// アプリ全体の設定 (ドキュメントに紐付かないもの)。
// 現状は Language のみ。将来 (診断ログ on/off や MCP port など) はここに増やす。
public sealed class AppSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AppLanguage Language { get; set; } = AppLanguage.System;
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
