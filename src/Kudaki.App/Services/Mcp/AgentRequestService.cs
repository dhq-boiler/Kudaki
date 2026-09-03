using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using R3;

namespace Kudaki.App.Services.Mcp;

// ユーザーが AI エージェントに出す依頼の種類。
// 汎用エンベロープにしておいて後から estimate / review などを足せるようにする
// (Fable レビュー 2026-09-03: 汎用化のコストはほぼゼロ)。
public enum AgentRequestKind
{
    Breakdown,
}

// 「このタスクを砕いてくれ」のような、ユーザー → AI 方向の 1 件の依頼。
// AI が wait_for_request で受け取れるように、タスクの文脈を自己完結で持たせる。
public sealed class AgentRequest
{
    public Guid Id { get; } = Guid.NewGuid();
    public AgentRequestKind Kind { get; init; }
    public string DocumentId { get; init; } = "";
    public string TaskId { get; init; } = "";
    public string TaskTitle { get; init; } = "";

    // ルートから当該タスクの親までのタイトル列。AI に「どの文脈のタスクか」を伝える。
    public IReadOnlyList<string> AncestorTitles { get; init; } = Array.Empty<string>();

    // 分解時に子へ配分してもらうための現在値。これを渡さないと、着手済みタスクを
    // 砕いた瞬間に進捗が 0% に戻る (親の Estimate/Remaining は子があると無視されるため。
    // TaskNode.GetRolledUpRemainingHours 参照)。Fable レビューの指摘。
    public double? EstimateHours { get; init; }
    public double? RemainingHours { get; init; }

    public string? Notes { get; init; }

    public DateTime EnqueuedUtc { get; } = DateTime.UtcNow;
}

// per-doc の「ユーザー → AI」依頼チャネル。
//
// MCP transport が Stateless (McpHostService の WithHttpTransport(o => o.Stateless = true)) なので、
// Kudaki 側から AI に話しかける経路が存在しない (sampling / elicitation / notification は
// すべてサーバー → クライアント方向で、stateless では無効)。
// そこで PendingChangesService と同じ long-poll を裏返して使う:
//
//   - AI は wait_for_request を呼んでブロックする (= ウェイター登録)
//   - ユーザーが依頼を出すと、待機中のウェイターに即配送する
//   - 誰も待っていなければキューに積む。AI が後から wait_for_request を呼べば受け取れる
//
// これで「先に AI を待機させる」「先に依頼を出してから AI に処理させる」のどちらの順でも動く。
//
// スレッド方針は PendingChangesService と同じで、状態はすべて UI スレッド上でだけ触る。
// Dispatcher が直列化の役目を果たすので lock は要らない。
public sealed class AgentRequestService
{
    private readonly ObservableCollection<AgentRequest> _queue = new();
    private readonly List<TaskCompletionSource<AgentRequest?>> _waiters = new();

    public ReadOnlyObservableCollection<AgentRequest> Queue { get; }

    // 現在ブロック中の wait_for_request の本数。
    // 「AI 待機中」の唯一誠実な定義がこれ (Fable レビュー): list_documents を最近呼んだ、
    // といった過去の事実は現在の接続状態ではないので、それを接続中と表示するのは嘘になる。
    public BindableReactiveProperty<int> WaiterCount { get; } = new(0);

    public AgentRequestService()
    {
        Queue = new ReadOnlyObservableCollection<AgentRequest>(_queue);
    }

    // UI スレッドから呼ぶ。待機中の AI がいれば直接渡し、いなければキューに積む。
    public void Enqueue(AgentRequest request)
    {
        while (_waiters.Count > 0)
        {
            var waiter = _waiters[0];
            _waiters.RemoveAt(0);
            WaiterCount.Value = _waiters.Count;
            // TrySetResult が false = timeout / キャンセル済みの残骸。次のウェイターへ。
            if (waiter.TrySetResult(request)) return;
        }
        _queue.Add(request);
    }

    // UI スレッドから呼ぶ。まだ配送されていない依頼を取り消す。
    public bool CancelQueued(Guid id)
    {
        for (var i = 0; i < _queue.Count; i++)
        {
            if (_queue[i].Id != id) continue;
            _queue.RemoveAt(i);
            return true;
        }
        return false;
    }

    public int CancelAllQueued()
    {
        var count = _queue.Count;
        _queue.Clear();
        return count;
    }

    // MCP の wait_for_request から Kestrel スレッド上で呼ばれる。
    // キューに溜まっていれば即返し、空ならユーザーが依頼を出すまでブロックする。
    // timeout 到達・ct キャンセル (クライアント切断) では null を返してウェイターを外す。
    public async Task<AgentRequest?> WaitAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<AgentRequest?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await RunOnUiAsync(() =>
        {
            if (_queue.Count > 0)
            {
                var request = _queue[0];
                _queue.RemoveAt(0);
                tcs.TrySetResult(request);
                return;
            }
            _waiters.Add(tcs);
            WaiterCount.Value = _waiters.Count;
        }).ConfigureAwait(false);

        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var reg = linked.Token.Register(() => tcs.TrySetResult(null));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            // ウェイターを必ず外す。ここを漏らすと「AI 待機中」表示が残り続けて嘘になる。
            await RunOnUiAsync(() =>
            {
                if (_waiters.Remove(tcs)) WaiterCount.Value = _waiters.Count;
            }).ConfigureAwait(false);
        }
    }

    private static Task RunOnUiAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return dispatcher.InvokeAsync(action).Task;
    }
}
