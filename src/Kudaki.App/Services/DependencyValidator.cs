using System.Collections.Generic;
using System.Linq;
using Kudaki.App.Properties;
using Kudaki.App.ViewModels;

namespace Kudaki.App.Services;

// タスク依存 (Predecessor) の妥当性チェックとサニタイズ。
// 意味論: X → Y は「X 配下の全葉が完了 → Y 配下の葉が開始可能」(FS 固定)。
// 制約:
//   - self-loop 禁止 (A → A)
//   - 祖先-子孫関係の依存禁止 (A が B の祖先/子孫だと A の完了は B に包含される)
//   - 循環禁止 (A → ... → A になる依存の連鎖)
public static class DependencyValidator
{
    // 追加候補 candidate を target の Predecessor として張ってよいかを判定。
    public static ValidationResult CanAddPredecessor(
        TaskNodeViewModel target, TaskNodeViewModel candidate)
    {
        if (candidate == target) return new(false, Strings.Dep_Error_Self);
        if (target.Predecessors.Contains(candidate)) return new(false, Strings.Dep_Error_AlreadyRegistered);
        if (IsAncestorOrDescendant(target, candidate))
            return new(false, Strings.Dep_Error_AncestryRelation);
        if (WouldCreateCycle(target, candidate))
            return new(false, Strings.Dep_Error_Cycle);
        return new(true, null);
    }

    // target の Predecessor に candidate を追加したときに循環になるか。
    // 循環条件: candidate は既に target の (transitive な) 後続である
    //   = candidate から Predecessor をたどっていくと target に到達できる。
    public static bool WouldCreateCycle(TaskNodeViewModel target, TaskNodeViewModel candidate)
    {
        var visited = new HashSet<TaskNodeViewModel>();
        var stack = new Stack<TaskNodeViewModel>();
        foreach (var p in candidate.Predecessors) stack.Push(p);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == target) return true;
            if (!visited.Add(cur)) continue;
            foreach (var p in cur.Predecessors) stack.Push(p);
        }
        return false;
    }

    // 祖先-子孫関係か (どちら方向でも)。
    public static bool IsAncestorOrDescendant(TaskNodeViewModel a, TaskNodeViewModel b)
        => IsAncestorOf(a, b) || IsAncestorOf(b, a);

    // a が b の祖先か。
    public static bool IsAncestorOf(TaskNodeViewModel a, TaskNodeViewModel b)
    {
        var p = b.Parent;
        while (p != null)
        {
            if (p == a) return true;
            p = p.Parent;
        }
        return false;
    }

    // Indent / Outdent / Move で祖先-子孫関係になった依存を全木で消し込む。
    // 循環はタスク移動で新規発生しないので (依存エッジ自体は不変)、ここでは除去対象外。
    // 除去した数を返す (呼び出し側が StatusMessage で告知するため)。
    public static int SanitizeAncestryDependencies(TaskNodeViewModel root)
    {
        int removed = 0;
        foreach (var vm in EnumerateTasks(root))
        {
            var bad = vm.Predecessors
                .Where(p => IsAncestorOrDescendant(vm, p))
                .ToList();
            foreach (var b in bad)
            {
                vm.Predecessors.Remove(b);
                removed++;
            }
        }
        return removed;
    }

    // 仮想ルート以下の全 VM を BFS で列挙 (仮想ルート自体は除く)。
    public static IEnumerable<TaskNodeViewModel> EnumerateTasks(TaskNodeViewModel root)
    {
        var queue = new Queue<TaskNodeViewModel>();
        foreach (var c in root.Children) queue.Enqueue(c);
        while (queue.Count > 0)
        {
            var v = queue.Dequeue();
            yield return v;
            foreach (var c in v.Children) queue.Enqueue(c);
        }
    }
}

public sealed record ValidationResult(bool IsValid, string? ErrorMessage);
