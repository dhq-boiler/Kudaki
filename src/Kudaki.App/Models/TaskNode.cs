using System;
using System.Collections.Generic;
using System.Linq;
using YamlDotNet.Serialization;

namespace Kudaki.App.Models;

public sealed class TaskNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = string.Empty;

    // 初期見積 (改訂可能)。子を持つ内部ノードでは無視され、rolled-up で子の合計を使う。
    public double? EstimateHours { get; set; }

    // 毎日更新する「残時間」。実績と進捗はここから派生させる (V2 の主入力)。
    //   null      → 未着手 (進捗0%、消化0h)
    //   >= Est    → 未着手扱い (進捗0%)
    //   0 <= Rem < Est → 作業中 (進捗 = (Est-Rem)/Est)
    //   0         → 完了 (100%)
    public double? RemainingHours { get; set; }

    public string? Assignee { get; set; }

    public DateOnly? DueDate { get; set; }

    public string? Notes { get; set; }

    public List<TaskNode> Children { get; set; } = new();

    [YamlIgnore]
    public bool IsLeaf => Children.Count == 0;

    // 葉なら自分の EstimateHours、内部ノードなら子の合計。
    // 内部ノードに EstimateHours が入力されていても子があれば無視する
    // (Excel の合計行が式ズレで壊れる問題を構造で回避)。
    public double GetRolledUpEstimateHours()
    {
        return IsLeaf ? EstimateHours ?? 0.0
                      : Children.Sum(c => c.GetRolledUpEstimateHours());
    }

    // 葉: 残時間。未入力なら見積相当が丸ごと残ってる (未着手) 扱い。
    // 内部ノード: 子の合計。
    public double GetRolledUpRemainingHours()
    {
        if (IsLeaf)
        {
            if (RemainingHours.HasValue) return RemainingHours.Value;
            // 未着手 = まだ何もしていない = 見積全部が残っている
            return EstimateHours ?? 0.0;
        }
        return Children.Sum(c => c.GetRolledUpRemainingHours());
    }

    // 消化 (spent) = max(0, 見積 - 残)。派生値、手動入力なし。
    public double GetRolledUpActualHours()
    {
        var est = GetRolledUpEstimateHours();
        var rem = GetRolledUpRemainingHours();
        var spent = est - rem;
        return spent > 0 ? spent : 0.0;
    }

    // 進捗% = (見積 - 残) / 見積 を 0..100 に clamp。
    // 見積が 0 なら null (計算不能)。全体の見積が 0 の内部ノードも同じ。
    public int? GetRolledUpProgressPercent()
    {
        var est = GetRolledUpEstimateHours();
        if (est <= 0) return null;

        var rem = GetRolledUpRemainingHours();
        var progress = (est - rem) / est * 100.0;
        if (progress < 0) return 0;
        if (progress > 100) return 100;
        return (int)Math.Round(progress);
    }
}
