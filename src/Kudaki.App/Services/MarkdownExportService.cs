using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kudaki.App.Models;

namespace Kudaki.App.Services;

// WbsDocument → GitHub 対応 Markdown への片方向出力。
// 読込はしない (保存形式は YAML)。共有・GitHub 貼付用の派生成果物。
public sealed class MarkdownExportService
{
    // 砕き警告の閾値。VM 側 (TaskNodeViewModel.BreakdownThresholdHours) と同値。
    // MVP の間は両方に定数として置く。設定化するときに1箇所に集約。
    public const double BreakdownThresholdHours = 40.0;

    public const string PrimaryExtension = ".md";
    public const string SaveFilter = "Markdown ファイル (*.md)|*.md|すべてのファイル (*.*)|*.*";

    public async Task ExportAsync(WbsDocument document, string path, CancellationToken ct = default)
    {
        var text = Render(document);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
    }

    // 単体テストしやすいように文字列生成部を分離。
    public string Render(WbsDocument document)
    {
        var sb = new StringBuilder();

        sb.Append("# ").AppendLine(string.IsNullOrWhiteSpace(document.Title) ? "Kudaki WBS" : document.Title);
        sb.AppendLine();
        sb.Append("*更新: ")
          .Append(document.ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
          .AppendLine("*");
        sb.AppendLine();

        if (document.Tasks.Count == 0)
        {
            sb.AppendLine("*（タスクなし）*");
            return sb.ToString();
        }

        foreach (var task in document.Tasks)
        {
            AppendTask(sb, task, indentLevel: 0);
        }

        return sb.ToString();
    }

    private static void AppendTask(StringBuilder sb, TaskNode task, int indentLevel)
    {
        var indent = new string(' ', indentLevel * 2);

        // 進捗 100% なら [x]、それ以外は [ ]
        var progress = task.IsLeaf ? task.ProgressPercent : task.GetRolledUpProgressPercent();
        var check = progress == 100 ? "[x]" : "[ ]";

        // ⚠ は葉タスクで見積 > 閾値 のときだけ
        var warning = task.IsLeaf && (task.EstimateHours ?? 0.0) > BreakdownThresholdHours;
        var warningGlyph = warning ? " ⚠" : "";

        var title = string.IsNullOrWhiteSpace(task.Title) ? "(無題)" : task.Title;

        sb.Append(indent).Append("- ").Append(check).Append(" **").Append(title).Append("**").Append(warningGlyph);

        var meta = BuildMetaLine(task);
        if (meta.Length > 0)
        {
            sb.Append(" — ").Append(meta);
        }

        if (warning)
        {
            sb.Append(" *(まだ砕けます)*");
        }

        sb.AppendLine();

        // メモは blockquote で1段下げ
        if (!string.IsNullOrWhiteSpace(task.Notes))
        {
            var noteIndent = new string(' ', (indentLevel + 1) * 2);
            foreach (var line in task.Notes.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                sb.Append(noteIndent).Append("> ").AppendLine(trimmed);
            }
        }

        foreach (var child in task.Children)
        {
            AppendTask(sb, child, indentLevel + 1);
        }
    }

    private static string BuildMetaLine(TaskNode task)
    {
        var parts = new System.Collections.Generic.List<string>();

        if (task.IsLeaf)
        {
            if (task.EstimateHours is double est)
                parts.Add($"見積 {FormatHours(est)}h");
            if (task.ActualHours is double act)
                parts.Add($"実績 {FormatHours(act)}h");
            if (task.ProgressPercent is int prog)
                parts.Add($"進捗 {prog}%");
        }
        else
        {
            // 内部ノード: rolled-up 値を表示 ("合計" と明示して混同を防ぐ)
            var est = task.GetRolledUpEstimateHours();
            var act = task.GetRolledUpActualHours();
            var prog = task.GetRolledUpProgressPercent();

            if (est > 0) parts.Add($"見積合計 {FormatHours(est)}h");
            if (act > 0) parts.Add($"実績合計 {FormatHours(act)}h");
            if (prog.HasValue) parts.Add($"進捗 {prog.Value}%");
        }

        if (!string.IsNullOrWhiteSpace(task.Assignee)) parts.Add($"担当 {task.Assignee}");
        if (task.DueDate is DateOnly due) parts.Add($"期限 {due:yyyy-MM-dd}");

        return string.Join(" / ", parts);
    }

    private static string FormatHours(double h) => h % 1 == 0 ? h.ToString("0") : h.ToString("0.#");
}
