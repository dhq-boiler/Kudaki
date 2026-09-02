using System.Collections.Generic;
using System.Linq;
using Kudaki.App.Models;
using Kudaki.App.Properties;

namespace Kudaki.App.Services.Mcp;

// 現在の WbsDocument と AI 提案 WbsDocument を id ベースで比較して
// PendingChange のリストを吐く。
// - Guid ベース Id が current / proposed で共通なら Update 候補
// - proposed だけにある id は Add
// - current だけにある id は Delete
// - Update は TaskNode の各フィールドを比較して FieldDiffs を作る
//
// 簡易実装 (v0.2 MVP): subtree Add/Delete の畳み込みは v0.3 に。
// 今は「変更を id ごとに 1 行に展開する」だけ。
public static class DiffCalculator
{
    // ドキュメントレベル (Task 群じゃない) の変更を代表する疑似 TaskId。
    // ApplyProposedDocument が Document 丸ごと差し替えなので、この 1 件を承認/却下
    // すれば document メタデータの変更もついてくる。
    public const string DocumentPseudoTaskId = "__document__";

    public static IReadOnlyList<PendingChange> Compare(WbsDocument current, WbsDocument proposed)
    {
        var changes = new List<PendingChange>();

        // ドキュメントレベル差分 (Title など)
        var docFieldDiffs = new List<FieldDiff>();
        if (current.Title != proposed.Title)
        {
            docFieldDiffs.Add(new FieldDiff
            {
                FieldName = nameof(WbsDocument.Title),
                Before = current.Title,
                After = proposed.Title,
            });
        }
        if (docFieldDiffs.Count > 0)
        {
            // Overlay の表示用に stub TaskNode を挟む (Before/After.Title を「(ドキュメント全体)」に)
            var stub = new TaskNode { Id = DocumentPseudoTaskId, Title = Strings.Diff_DocumentLevelPseudoTitle };
            changes.Add(new PendingChange
            {
                Op = PendingChangeOp.Update,
                TaskId = DocumentPseudoTaskId,
                ParentId = null,
                Before = stub,
                After = stub,
                FieldDiffs = docFieldDiffs,
            });
        }

        var currentFlat = Flatten(current);
        var proposedFlat = Flatten(proposed);

        var currentIds = new HashSet<string>(currentFlat.Keys);
        var proposedIds = new HashSet<string>(proposedFlat.Keys);

        // Delete: current にあって proposed にない → 常に Manual (承認必須)
        foreach (var id in currentIds.Except(proposedIds))
        {
            var entry = currentFlat[id];
            changes.Add(new PendingChange
            {
                Op = PendingChangeOp.Delete,
                TaskId = id,
                ParentId = entry.ParentId,
                Before = entry.Node,
                Severity = ChangeSeverity.Manual,
            });
        }

        // Add: proposed にあって current にない → 常に Manual (承認必須)
        foreach (var id in proposedIds.Except(currentIds))
        {
            var entry = proposedFlat[id];
            changes.Add(new PendingChange
            {
                Op = PendingChangeOp.Add,
                TaskId = id,
                ParentId = entry.ParentId,
                After = entry.Node,
                Severity = ChangeSeverity.Manual,
            });
        }

        // Update: 両方にあってフィールド差分がある → 分類器で Auto/Manual 判定
        foreach (var id in currentIds.Intersect(proposedIds))
        {
            var cur = currentFlat[id];
            var prop = proposedFlat[id];
            var fieldDiffs = CompareFields(cur, prop);
            if (fieldDiffs.Count == 0) continue;

            changes.Add(new PendingChange
            {
                Op = PendingChangeOp.Update,
                TaskId = id,
                ParentId = prop.ParentId,
                Before = cur.Node,
                After = prop.Node,
                FieldDiffs = fieldDiffs,
                Severity = ClassifyUpdate(fieldDiffs, cur.ParentId, prop.ParentId),
            });
        }

        // ドキュメントレベル変更が既に List 先頭に入っていれば Manual (上の docFieldDiffs 分岐で追加された)
        // に上書きしておく (デフォルト Manual なので実質何もしないが、意図の明示)
        // → 現状 PendingChange の init-only + デフォルト Manual なので追加操作不要

        return changes;
    }

    // v03-mcp-auto-apply t-diff-classifier: Update 1 件を auto/manual に振り分ける。
    // Kudaki の TaskNode field:
    //   - Auto: RemainingHours (残時間更新), Notes 追記のみ (Before が After の prefix)
    //   - Manual: 上記以外の全 field 変更、および ParentId 変更 (階層変更)
    // 混在 (Auto field と Manual field が同 update 内) は Manual に倒す (1 つでも Manual field なら Manual)。
    private static ChangeSeverity ClassifyUpdate(IReadOnlyList<FieldDiff> fieldDiffs, string? beforeParent, string? afterParent)
    {
        // ParentId 変更 = 階層変更 = 常に Manual
        if (beforeParent != afterParent) return ChangeSeverity.Manual;

        foreach (var fd in fieldDiffs)
        {
            if (!IsFieldAutoApplyable(fd)) return ChangeSeverity.Manual;
        }
        return fieldDiffs.Count > 0 ? ChangeSeverity.Auto : ChangeSeverity.Manual;
    }

    private static bool IsFieldAutoApplyable(FieldDiff fd) => fd.FieldName switch
    {
        // ParentId は既に上で分岐済みなので、ここに来ることは無い (念のため Manual)
        "ParentId" => false,
        nameof(TaskNode.RemainingHours) => true,
        nameof(TaskNode.Notes) => IsNotesAppendOnly(fd.Before as string, fd.After as string),
        // Title / EstimateHours / Assignee / DueDate / PredecessorIds は Manual
        _ => false,
    };

    // 「追記のみ」= After が Before で始まる + After.Length > Before.Length。
    // 書き換え / 削除 / 中間挿入 (Before が prefix にならないケース) は Manual に落ちる。
    // Before が null / empty のときは「空 → 何か書いた」も追記扱いで Auto。
    private static bool IsNotesAppendOnly(string? before, string? after)
    {
        var b = before ?? string.Empty;
        var a = after ?? string.Empty;
        if (a.Length <= b.Length) return false;  // 短くなってる = 削除 or 書き換え
        return a.StartsWith(b, System.StringComparison.Ordinal);
    }

    private readonly record struct Entry(TaskNode Node, string? ParentId);

    private static Dictionary<string, Entry> Flatten(WbsDocument doc)
    {
        var map = new Dictionary<string, Entry>();
        foreach (var t in doc.Tasks) Walk(t, parentId: null, map);
        return map;
    }

    private static void Walk(TaskNode node, string? parentId, Dictionary<string, Entry> map)
    {
        map[node.Id] = new Entry(node, parentId);
        foreach (var child in node.Children)
        {
            Walk(child, node.Id, map);
        }
    }

    private static List<FieldDiff> CompareFields(Entry cur, Entry prop)
    {
        var diffs = new List<FieldDiff>();
        var a = cur.Node;
        var b = prop.Node;

        if (cur.ParentId != prop.ParentId)
            diffs.Add(new FieldDiff { FieldName = "ParentId", Before = cur.ParentId, After = prop.ParentId });
        if (a.Title != b.Title)
            diffs.Add(new FieldDiff { FieldName = nameof(TaskNode.Title), Before = a.Title, After = b.Title });
        if (!System.Nullable.Equals(a.EstimateHours, b.EstimateHours))
            diffs.Add(new FieldDiff { FieldName = nameof(TaskNode.EstimateHours), Before = a.EstimateHours, After = b.EstimateHours });
        if (!System.Nullable.Equals(a.RemainingHours, b.RemainingHours))
            diffs.Add(new FieldDiff { FieldName = nameof(TaskNode.RemainingHours), Before = a.RemainingHours, After = b.RemainingHours });
        if (a.Assignee != b.Assignee)
            diffs.Add(new FieldDiff { FieldName = nameof(TaskNode.Assignee), Before = a.Assignee, After = b.Assignee });
        if (!System.Nullable.Equals(a.DueDate, b.DueDate))
            diffs.Add(new FieldDiff { FieldName = nameof(TaskNode.DueDate), Before = a.DueDate, After = b.DueDate });
        if (a.Notes != b.Notes)
            diffs.Add(new FieldDiff { FieldName = nameof(TaskNode.Notes), Before = a.Notes, After = b.Notes });

        var aPreds = a.PredecessorIds ?? new List<string>();
        var bPreds = b.PredecessorIds ?? new List<string>();
        if (!aPreds.SequenceEqual(bPreds))
            diffs.Add(new FieldDiff { FieldName = nameof(TaskNode.PredecessorIds),
                Before = string.Join(",", aPreds), After = string.Join(",", bPreds) });

        return diffs;
    }
}
