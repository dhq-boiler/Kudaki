using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kudaki.App.Models;

namespace Kudaki.App.Services.Mcp;

public enum ApprovalResult
{
    Approved,
    Rejected,
    TimedOut,
}

// AI からの提案 1 バッチ。承認/却下が確定するまで Completion.Task を await する。
public sealed class PendingChangeSet
{
    public Guid Id { get; } = Guid.NewGuid();
    public IReadOnlyList<PendingChange> Changes { get; init; } = Array.Empty<PendingChange>();
    public string Source { get; init; } = "";
    public DateTime SubmittedUtc { get; init; } = DateTime.UtcNow;

    // 承認されたときに MainViewModel に差し替える proposed WbsDocument。
    public WbsDocument Proposed { get; init; } = null!;

    internal TaskCompletionSource<ApprovalResult> Completion { get; }
        = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// 提案の投入・承認/却下シグナルを一元管理する singleton。
// - Kestrel の別スレッドから SubmitAsync (承認まで await)
// - UI スレッドから Approve / Reject
// - UI 側は Pending コレクションを ItemsSource にバインドする (v0.2 の Diff Panel)
public sealed class PendingChangesService
{
    private static readonly Lazy<PendingChangesService> _instance = new(() => new PendingChangesService());
    public static PendingChangesService Instance => _instance.Value;

    private readonly ObservableCollection<PendingChangeSet> _pending = new();
    public ReadOnlyObservableCollection<PendingChangeSet> Pending { get; }

    private PendingChangesService()
    {
        Pending = new ReadOnlyObservableCollection<PendingChangeSet>(_pending);
    }

    // 提案投入 → 承認/却下 or タイムアウトを待って結果を返す。
    // UI に見せる _pending 追加/削除は Dispatcher 上で。
    public async Task<ApprovalResult> SubmitAsync(
        PendingChangeSet set,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        await RunOnUiAsync(() => _pending.Add(set)).ConfigureAwait(false);

        try
        {
            var completionTask = set.Completion.Task;
            if (timeout is null)
            {
                using var reg = ct.Register(
                    () => set.Completion.TrySetResult(ApprovalResult.TimedOut));
                return await completionTask.ConfigureAwait(false);
            }

            using var timeoutCts = new CancellationTokenSource(timeout.Value);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var winner = await Task.WhenAny(
                completionTask,
                Task.Delay(Timeout.InfiniteTimeSpan, linked.Token)).ConfigureAwait(false);
            if (winner == completionTask) return await completionTask.ConfigureAwait(false);
            return ApprovalResult.TimedOut;
        }
        finally
        {
            await RunOnUiAsync(() => _pending.Remove(set)).ConfigureAwait(false);
        }
    }

    // UI (or テスト) から呼ぶ承認・却下シグナル。存在しない Id は no-op。
    public bool Approve(Guid setId)
    {
        var set = FindById(setId);
        return set is not null && set.Completion.TrySetResult(ApprovalResult.Approved);
    }

    public bool Reject(Guid setId)
    {
        var set = FindById(setId);
        return set is not null && set.Completion.TrySetResult(ApprovalResult.Rejected);
    }

    private PendingChangeSet? FindById(Guid setId)
    {
        return _pending.FirstOrDefault(s => s.Id == setId);
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
