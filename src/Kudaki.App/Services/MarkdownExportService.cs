using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kudaki.App.Models;
using Kudaki.App.Properties;

namespace Kudaki.App.Services;

// WbsDocument → GitHub 対応 Markdown への片方向出力。
// 読込はしない (保存形式は YAML)。共有・GitHub 貼付用の派生成果物。
public sealed class MarkdownExportService
{
    // 砕き警告の閾値。VM 側 (TaskNodeViewModel.BreakdownThresholdHours) と同値。
    // MVP の間は両方に定数として置く。設定化するときに1箇所に集約。
    public const double BreakdownThresholdHours = 40.0;

    public const string PrimaryExtension = ".md";
    public static string SaveFilter => Strings.Dialog_Markdown_Filter;

    public async Task ExportAsync(WbsDocument document, string path, CancellationToken ct = default)
    {
        var text = Render(document);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
    }

    // 単体テストしやすいように文字列生成部を分離。
    public string Render(WbsDocument document)
    {
        var sb = new StringBuilder();

        sb.Append("# ").AppendLine(string.IsNullOrWhiteSpace(document.Title) ? Strings.Md_FallbackDocumentTitle : document.Title);
        sb.AppendLine();
        sb.AppendLine(string.Format(
            Strings.Md_UpdatedLine_Format,
            document.ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
        sb.AppendLine();

        if (document.Tasks.Count == 0)
        {
            sb.AppendLine(Strings.Md_NoTasks);
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
        var progress = task.GetRolledUpProgressPercent();
        var check = progress == 100 ? "[x]" : "[ ]";

        // ⚠ は葉タスクで見積 > 閾値 のときだけ
        var warning = task.IsLeaf && (task.EstimateHours ?? 0.0) > BreakdownThresholdHours;
        var warningGlyph = warning ? " ⚠" : "";

        var title = string.IsNullOrWhiteSpace(task.Title) ? Strings.Md_UntitledTask : task.Title;

        sb.Append(indent).Append("- ").Append(check).Append(" **").Append(title).Append("**").Append(warningGlyph);

        var meta = BuildMetaLine(task);
        if (meta.Length > 0)
        {
            sb.Append(" — ").Append(meta);
        }

        if (warning)
        {
            sb.Append(Strings.Md_Warning_Suffix);
        }

        sb.AppendLine();

        // メモは blockquote で1段下げ。YAML `notes: |` の末尾改行が
        // 空 blockquote (`> `) として残らないよう先に TrimEnd。
        if (!string.IsNullOrWhiteSpace(task.Notes))
        {
            var noteIndent = new string(' ', (indentLevel + 1) * 2);
            var trimmedNotes = task.Notes.TrimEnd('\r', '\n', ' ', '\t');
            foreach (var line in trimmedNotes.Split('\n'))
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
                parts.Add(string.Format(Strings.Md_Leaf_Estimate_Format, FormatHours(est)));
            if (task.RemainingHours is double rem)
                parts.Add(string.Format(Strings.Md_Leaf_Remaining_Format, FormatHours(rem)));
            var prog = task.GetRolledUpProgressPercent();
            if (prog.HasValue) parts.Add(string.Format(Strings.Md_Leaf_Progress_Format, prog.Value));
        }
        else
        {
            // 内部ノード: 葉から派生した合計を表示
            var est = task.GetRolledUpEstimateHours();
            var rem = task.GetRolledUpRemainingHours();
            var prog = task.GetRolledUpProgressPercent();

            if (est > 0) parts.Add(string.Format(Strings.Md_Inner_EstimateTotal_Format, FormatHours(est)));
            if (est > 0) parts.Add(string.Format(Strings.Md_Inner_RemainingTotal_Format, FormatHours(rem)));
            if (prog.HasValue) parts.Add(string.Format(Strings.Md_Inner_Progress_Format, prog.Value));
        }

        if (!string.IsNullOrWhiteSpace(task.Assignee)) parts.Add(string.Format(Strings.Md_Meta_Assignee_Format, task.Assignee));
        if (task.DueDate is DateOnly due) parts.Add(string.Format(Strings.Md_Meta_DueDate_Format, due));

        return string.Join(" / ", parts);
    }

    private static string FormatHours(double h) => h % 1 == 0 ? h.ToString("0") : h.ToString("0.#");
}
