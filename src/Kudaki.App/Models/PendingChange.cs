using System;
using System.Collections.Generic;

namespace Kudaki.App.Models;

// MCP propose_changes で AI から投入された変更提案の 1 行分。
// UI の Diff Panel はこのリストを緑/赤/オレンジで描く。
public enum PendingChangeOp
{
    Add,
    Update,
    Delete,
}

// v03-mcp-auto-apply: 変更の重み分類。
// Auto: 進捗更新やメモ追記など軽量なもの、ユーザー設定で承認 UI をスキップして即適用可能。
// Manual: 承認 UI 必須 (タスク追加・削除、階層変更、Title / EstimateHours / DueDate / Assignee /
//         Notes の書き換え・削除、Predecessors 変更、DocumentLevel 変更)。
// 混在 (Auto と Manual が同 proposal 内に両方ある) 場合は Manual に倒す
// (t-mixed-fallback、PendingChangeSet.IsAllAutoApplicable で判定)。
public enum ChangeSeverity
{
    Auto,
    Manual,
}

// Update のときに 1 フィールドあたりの Before → After を保持する。
public sealed class FieldDiff
{
    public string FieldName { get; init; } = "";
    public object? Before { get; init; }
    public object? After { get; init; }
}

public sealed class PendingChange
{
    public PendingChangeOp Op { get; init; }
    public string TaskId { get; init; } = "";
    public string? ParentId { get; init; }

    // Before / After はスナップショット (Diff Panel の表示用)。
    // Op=Add なら Before=null、Op=Delete なら After=null、Op=Update は両方あり。
    public TaskNode? Before { get; init; }
    public TaskNode? After { get; init; }

    // Op=Update のときのフィールド単位差分。Add/Delete では空。
    public IReadOnlyList<FieldDiff> FieldDiffs { get; init; } = Array.Empty<FieldDiff>();

    // v03-mcp-auto-apply: この 1 変更を auto-apply して良いかの分類。DiffCalculator が判定。
    public ChangeSeverity Severity { get; init; } = ChangeSeverity.Manual;
}
