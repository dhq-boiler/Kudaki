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
}
