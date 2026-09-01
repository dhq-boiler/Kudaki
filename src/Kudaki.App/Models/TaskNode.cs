using System;
using System.Collections.Generic;
using System.Linq;
using YamlDotNet.Serialization;

namespace Kudaki.App.Models;

public sealed class TaskNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = string.Empty;

    public double? EstimateHours { get; set; }

    public double? ActualHours { get; set; }

    public int? ProgressPercent { get; set; }

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

    public double GetRolledUpActualHours()
    {
        return IsLeaf ? ActualHours ?? 0.0
                      : Children.Sum(c => c.GetRolledUpActualHours());
    }

    // 見積工数で加重平均。全ての子の見積が 0 なら単純平均に落とす。
    // 進捗未入力の子は寄与しない (null で除外)。全員未入力なら null を返す。
    public int? GetRolledUpProgressPercent()
    {
        if (IsLeaf) return ProgressPercent;

        double totalWeight = 0.0;
        double weightedSum = 0.0;
        int reported = 0;
        double simpleSum = 0.0;

        foreach (var child in Children)
        {
            var p = child.GetRolledUpProgressPercent();
            if (p is null) continue;

            var w = child.GetRolledUpEstimateHours();
            totalWeight += w;
            weightedSum += w * p.Value;
            reported++;
            simpleSum += p.Value;
        }

        if (reported == 0) return null;
        if (totalWeight > 0) return (int)Math.Round(weightedSum / totalWeight);
        return (int)Math.Round(simpleSum / reported);
    }
}
