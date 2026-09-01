using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Kudaki.App.Services;

// 更新用インストーラー EXE を %TEMP% にダウンロード。
// 進捗は 0.0 - 1.0 の Fraction で通知。Content-Length が取れなければ 0 のまま。
public sealed class UpdateDownloadService
{
    public async Task<string> DownloadAsync(
        string url,
        IProgress<double> progress,
        CancellationToken ct = default)
    {
        var path = Path.Combine(Path.GetTempPath(), $"KudakiSetup-{Guid.NewGuid():N}.exe");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Kudaki-UpdateDownload");

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1L;
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fs = File.Create(path);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            downloaded += read;
            if (total > 0) progress.Report((double)downloaded / total);
        }

        return path;
    }
}
