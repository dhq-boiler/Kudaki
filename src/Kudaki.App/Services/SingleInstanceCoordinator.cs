using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kudaki.App.Services;

// v0.3 で「シングルトンインスタンス + マルチドキュメント」に舵を切ったのに伴い、
// 2 個目以降の起動は 1 個目に arg (open <path> / activate) を forward して自身は終了させる。
//
// 責務境界:
//   ここは「Mutex 取得 + Named Pipe による IPC」だけを持つ。
//   受信メッセージをどう解釈して UI に反映するか (前面化 / LoadFromPathAsync) は
//   App.xaml.cs 側のコールバックに任せる。
//
// スコープ:
//   Mutex は "Local\..." で作るので、同一ユーザーセッション内でのみ排他される。
//   複数ユーザー同時ログオン (Windows のセッション分離) は共存可能。
public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly string _mutexName;
    private readonly string _pipeName;
    private Mutex? _mutex;
    private bool _mutexOwned;
    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;

    public SingleInstanceCoordinator(string mutexName, string pipeName)
    {
        _mutexName = mutexName;
        _pipeName = pipeName;
    }

    // Mutex 取得を試みる。true = このプロセスが 1 個目、false = 既に別プロセスが存在。
    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);
        _mutexOwned = createdNew;
        return createdNew;
    }

    // 1 個目に 1 行メッセージを送信する (2 個目のみ呼ぶ)。
    // 1 個目が瀕死 / 起動途中で Pipe server が立ってなければ false。
    public bool TryForward(string message, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.Out, PipeOptions.None);
            client.Connect((int)timeout.TotalMilliseconds);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(message);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SingleInstance] forward failed: {ex}");
            return false;
        }
    }

    // 1 個目が Pipe server として listen を開始する。
    // onMessage は Pipe worker thread から呼ばれるので、UI 触るなら呼び出し側で Dispatcher marshal 必須。
    public void StartServer(Action<string> onMessage)
    {
        if (_serverTask is not null) return;
        _serverCts = new CancellationTokenSource();
        _serverTask = Task.Run(() => RunLoopAsync(onMessage, _serverCts.Token));
    }

    private async Task RunLoopAsync(Action<string> onMessage, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server, new UTF8Encoding(false));
                string? line;
                while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                {
                    try { onMessage(line); }
                    catch (Exception ex) { Debug.WriteLine($"[SingleInstance] handler error: {ex}"); }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SingleInstance] server loop error: {ex}");
                try { await Task.Delay(500, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public void Dispose()
    {
        try { _serverCts?.Cancel(); } catch { /* best effort */ }

        // Server loop は最大 2 秒で停止させる (WaitForConnectionAsync 抜けきるのを待つ)。
        // ここで無限に待つとアプリ終了が hang する。
        try { _serverTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }

        try { _serverCts?.Dispose(); } catch { /* best effort */ }

        if (_mutex is not null)
        {
            try
            {
                if (_mutexOwned) _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned by current thread; ignore.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SingleInstance] mutex release failed: {ex}");
            }
            _mutex.Dispose();
        }

        _mutex = null;
        _mutexOwned = false;
        _serverCts = null;
        _serverTask = null;
    }
}
