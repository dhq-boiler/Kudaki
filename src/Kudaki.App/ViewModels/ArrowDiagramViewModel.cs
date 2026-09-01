using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using R3;

namespace Kudaki.App.ViewModels;

// 親タスク parent の子供たちを AON 記法でアローダイアグラム表示するための VM。
// - Kahn's algorithm で topological sort、level ごとに列を割り当てて左→右にレイアウト
// - Nodes: parent の子 (子供の子孫までは追わない)
// - Edges: parent の子同士の Predecessor エッジのみ描画
// - 「子の内部の Predecessor が parent 外に伸びるもの」は外部依存としてボックスに印を付ける
//   (どの子が外部依存に触れているかを HasExternalInbound / Outbound で伝える)
//
// 意味論的には「X → Y は X 配下の全葉が終わって Y 配下の葉が開始可能」だが、
// この表示レベルでは X と Y (parent の子) 単位で見る (level-local view)。
public sealed partial class ArrowDiagramViewModel : ObservableObject
{
    public TaskNodeViewModel Parent { get; }
    public BindableReactiveProperty<string> Title { get; }
    public IReadOnlyList<ArrowDiagramNode> Nodes { get; }
    public IReadOnlyList<ArrowDiagramEdge> Edges { get; }
    public double CanvasWidth { get; }
    public double CanvasHeight { get; }

    // レイアウト定数
    private const double NodeWidth = 180;
    private const double NodeHeight = 64;
    private const double HGap = 60;   // 列間
    private const double VGap = 20;   // 同列内の間隔
    private const double PaddingX = 24;
    private const double PaddingY = 24;

    public ArrowDiagramViewModel(TaskNodeViewModel parent)
    {
        Parent = parent;
        Title = new BindableReactiveProperty<string>($"アローダイアグラム — {parent.Title}");

        var children = parent.Children.ToList();
        var childIdSet = new HashSet<string>(children.Select(c => c.Id));

        // 内部エッジ: parent の子同士。Predecessors リストから内部同士だけ拾う。
        var internalEdges = new List<(TaskNodeViewModel From, TaskNodeViewModel To)>();
        foreach (var to in children)
        {
            foreach (var from in to.Predecessors)
            {
                if (childIdSet.Contains(from.Id))
                {
                    internalEdges.Add((from, to));
                }
            }
        }

        // topological sort + level 割り当て (Kahn)。各ノードの level = 前段最大 level + 1。
        var levelOf = children.ToDictionary(c => c, _ => 0);
        var indeg = children.ToDictionary(c => c, c =>
            internalEdges.Count(e => e.To == c));
        var queue = new Queue<TaskNodeViewModel>(children.Where(c => indeg[c] == 0));
        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            foreach (var (from, to) in internalEdges.Where(e => e.From == u))
            {
                if (levelOf[to] < levelOf[u] + 1) levelOf[to] = levelOf[u] + 1;
                indeg[to]--;
                if (indeg[to] == 0) queue.Enqueue(to);
            }
        }

        // 列ごとに縦に積む。順序は元の Children の順を保つ (安定)。
        var byColumn = children
            .GroupBy(c => levelOf[c])
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.ToList());

        var nodes = new List<ArrowDiagramNode>();
        foreach (var (level, group) in byColumn)
        {
            for (int i = 0; i < group.Count; i++)
            {
                var vm = group[i];
                var x = PaddingX + level * (NodeWidth + HGap);
                var y = PaddingY + i * (NodeHeight + VGap);
                // 外部依存の存在チェック
                var hasExternalInbound = vm.Predecessors.Any(p => !childIdSet.Contains(p.Id));
                nodes.Add(new ArrowDiagramNode(vm, x, y, NodeWidth, NodeHeight, hasExternalInbound));
            }
        }

        Nodes = nodes;

        // Edges: 各内部エッジをノードの中心座標から中心座標へ (View 側で矢頭を描画)。
        var nodeMap = nodes.ToDictionary(n => n.Task, n => n);
        Edges = internalEdges
            .Where(e => nodeMap.ContainsKey(e.From) && nodeMap.ContainsKey(e.To))
            .Select(e =>
            {
                var f = nodeMap[e.From];
                var t = nodeMap[e.To];
                return new ArrowDiagramEdge(
                    FromX: f.X + f.Width,        // 出発は from の右端中央
                    FromY: f.Y + f.Height / 2,
                    ToX: t.X,                    // 到着は to の左端中央
                    ToY: t.Y + t.Height / 2);
            })
            .ToList();

        // Canvas 全体サイズ
        CanvasWidth = nodes.Count == 0
            ? 400
            : nodes.Max(n => n.X + n.Width) + PaddingX;
        CanvasHeight = nodes.Count == 0
            ? 200
            : nodes.Max(n => n.Y + n.Height) + PaddingY;
    }
}

public sealed record ArrowDiagramNode(
    TaskNodeViewModel Task,
    double X, double Y,
    double Width, double Height,
    bool HasExternalInbound);

public sealed record ArrowDiagramEdge(
    double FromX, double FromY,
    double ToX, double ToY);
