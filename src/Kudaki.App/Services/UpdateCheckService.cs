using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kudaki.App.Services;

// GitHub Releases の latest を取って、現バージョンより新しければ返す。
// asset (KudakiSetup.exe) の直接 DL URL とサイズも一緒に返して、
// UpdateDownloadService から拾えるようにしてある。
// 失敗系はすべて null に潰す (ネットワーク不通と同じ扱い)。
public sealed class UpdateCheckService
{
    private const string LatestApiUrl = "https://api.github.com/repos/dhq-boiler/Kudaki/releases/latest";
    private const string ReleasesHtmlUrl = "https://github.com/dhq-boiler/Kudaki/releases/latest";

    // 自動更新用アセットは KudakiSetup.exe に固定 (先頭一致で探す)。
    private const string AssetNamePrefix = "KudakiSetup";

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

            var normalized = tag.TrimStart('v', 'V');
            if (!Version.TryParse(normalized, out var latest)) return null;

            var current = Assembly.GetEntryAssembly()?.GetName().Version;
            if (current is null) return null;
            if (latest <= current) return null;

            string? assetUrl = null;
            long? assetSize = null;
            if (doc.RootElement.TryGetProperty("assets", out var assetsEl) &&
                assetsEl.ValueKind == JsonValueKind.Array)
            {
                var chosen = assetsEl.EnumerateArray()
                    .FirstOrDefault(a =>
                        a.TryGetProperty("name", out var n) &&
                        n.GetString()?.StartsWith(AssetNamePrefix, StringComparison.OrdinalIgnoreCase) == true);
                if (chosen.ValueKind == JsonValueKind.Object)
                {
                    if (chosen.TryGetProperty("browser_download_url", out var uEl))
                        assetUrl = uEl.GetString();
                    if (chosen.TryGetProperty("size", out var sEl) &&
                        sEl.TryGetInt64(out var s))
                        assetSize = s;
                }
            }

            return new UpdateInfo(tag, htmlUrl ?? ReleasesHtmlUrl, assetUrl, assetSize);
        }
        catch
        {
            return null;
        }
    }
}

// AssetDownloadUrl / AssetSize が null なら自動更新は不可、ブラウザ経由 fallback のみ。
public sealed record UpdateInfo(string Tag, string HtmlUrl, string? AssetDownloadUrl, long? AssetSize);
