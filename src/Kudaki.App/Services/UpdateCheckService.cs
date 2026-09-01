using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kudaki.App.Services;

// GitHub Releases の latest を取って、現バージョンより新しければ返す。
// 失敗系はすべて null に潰す (「更新ないし通知しない」= ネットワーク不通と同じ扱い)。
// 起動時に fire-and-forget で走らせる想定なので、例外を上に漏らさない。
public sealed class UpdateCheckService
{
    private const string LatestApiUrl = "https://api.github.com/repos/dhq-boiler/Kudaki/releases/latest";
    private const string ReleasesHtmlUrl = "https://github.com/dhq-boiler/Kudaki/releases/latest";

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Kudaki-UpdateCheck");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var json = await http.GetStringAsync(LatestApiUrl, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            var tag = doc.RootElement.TryGetProperty("tag_name", out var tagEl)
                ? tagEl.GetString()
                : null;
            var htmlUrl = doc.RootElement.TryGetProperty("html_url", out var urlEl)
                ? urlEl.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(tag)) return null;

            // "v0.2.0" → Version(0.2.0) 相当に。パース不能なら通知しない。
            var normalized = tag.TrimStart('v', 'V');
            if (!Version.TryParse(normalized, out var latest)) return null;

            var current = Assembly.GetEntryAssembly()?.GetName().Version;
            if (current is null) return null;

            // 数値比較 (Major.Minor.Build.Revision の順)。等しい / 古いなら null。
            if (latest <= current) return null;

            return new UpdateInfo(tag, htmlUrl ?? ReleasesHtmlUrl);
        }
        catch
        {
            return null;
        }
    }
}

public sealed record UpdateInfo(string Tag, string HtmlUrl);
