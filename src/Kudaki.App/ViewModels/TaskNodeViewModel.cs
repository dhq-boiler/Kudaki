using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kudaki.App.Models;
using Kudaki.App.Services;
using R3;

namespace Kudaki.App.ViewModels;

public sealed partial class TaskNodeViewModel : ObservableObject
{
    // 砕き警告の閾値。設定化はポストMVP。
    public const double BreakdownThresholdHours = 40.0;

    private readonly TaskNode _model;

    // UI 状態は R3 の BindableReactiveProperty で保持。
    // XAML は {Binding IsExpanded.Value, Mode=TwoWay} で参照。
    public BindableReactiveProperty<bool> IsExpanded { get; } = new(true);
    public BindableReactiveProperty<bool> IsSelected { get; } = new(false);

    public TaskNodeViewModel(TaskNode model, TaskNodeViewModel? parent)
    {
        _model = model;
        Parent = parent;
        Children = new ObservableCollection<TaskNodeViewModel>(
            model.Children.Select(c => new TaskNodeViewModel(c, this)));
        Children.CollectionChanged += OnChildrenCollectionChanged;
        Predecessors = new ObservableCollection<TaskNodeViewModel>();
        Predecessors.CollectionChanged += OnPredecessorsCollectionChanged;
    }

    // 保存時に MainViewModel から呼ぶ。VM の変更は常に Model に反映されているので
    // このまま WbsDocument に載せてよい。
    internal TaskNode Model => _model;

    public TaskNodeViewModel? Parent { get; internal set; }

    // 階層レベル (1-indexed)。top-level = 1、その子 = 2、...
    // 仮想ルート (Parent==null または Parent.Parent==null) の下がレベル 1。
    // Indent/Outdent で Parent が変わったときは OnChildrenCollectionChanged
    // 経由で NotifyDepthChanged が呼ばれ、子孫まで再評価される。
    public int Depth => (Parent is null || Parent.Parent is null) ? 1 : Parent.Depth + 1;

    public string Id => _model.Id;

    public string Title
    {
        get => _model.Title;
        set
        {
            if (_model.Title == value) return;
            _model.Title = value;
            OnPropertyChanged();
        }
    }

    public double? EstimateHours
    {
        get => _model.EstimateHours;
        set
        {
            if (Nullable.Equals(_model.EstimateHours, value)) return;
            _model.EstimateHours = value;
            OnPropertyChanged();
            NotifyRollupChanged();
        }
    }

    // 「毎日更新する残時間」が主入力。実績と進捗はここから派生。
    public double? RemainingHours
    {
        get => _model.RemainingHours;
        set
        {
            // 負値を弾く (0 は「完了」の意味なので許可)。
            var clamped = value.HasValue && value.Value < 0 ? 0.0 : value;
            if (Nullable.Equals(_model.RemainingHours, clamped)) return;
            _model.RemainingHours = clamped;
            OnPropertyChanged();
            NotifyRollupChanged();
        }
    }

    public string? Assignee
    {
        get => _model.Assignee;
        set
        {
            if (_model.Assignee == value) return;
            _model.Assignee = value;
            OnPropertyChanged();
        }
    }

    public DateOnly? DueDate
    {
        get => _model.DueDate;
        set
        {
            if (Nullable.Equals(_model.DueDate, value)) return;
            _model.DueDate = value;
            OnPropertyChanged();
        }
    }

    public string? Notes
    {
        get => _model.Notes;
        set
        {
            if (_model.Notes == value) return;
            _model.Notes = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<TaskNodeViewModel> Children { get; }

    // 先行タスク (Finish-to-Start)。任意タスク間 (level-local 制約なし)、
    // ただし self / 祖先-子孫 / 循環は DependencyValidator で弾く。
    // ID の永続化は _model.PredecessorIds に対する片方向同期で維持 (OnPredecessorsCollectionChanged)。
    public ObservableCollection<TaskNodeViewModel> Predecessors { get; }

    // Predecessor カウント表示用 (Tree row バッジで使う想定)。
    public int PredecessorCount => Predecessors.Count;

    public bool IsLeaf => Children.Count == 0;

    public double RolledUpEstimateHours => _model.GetRolledUpEstimateHours();
    public double RolledUpRemainingHours => _model.GetRolledUpRemainingHours();
    public double RolledUpActualHours => _model.GetRolledUpActualHours();
    public int? RolledUpProgressPercent => _model.GetRolledUpProgressPercent();

    // 砕き警告: 葉 かつ 見積 > 閾値 のとき true。
    // 名前 (Kudaki=砕き) を体現する差別化機能。
    public bool NeedsBreakdown =>
        IsLeaf && (EstimateHours ?? 0.0) > BreakdownThresholdHours;

    private void OnChildrenCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Model.Children を VM の並びに追随させる。
        // WbsDocument.Tasks は仮想ルートの Model.Children を共有しているので
        // これでドキュメント側も自動で最新になる。
        _model.Children.Clear();
        foreach (var vm in Children)
        {
            vm.Parent = this;
            vm.NotifyDepthChanged();
            _model.Children.Add(vm._model);
        }
        OnPropertyChanged(nameof(IsLeaf));
        NotifyRollupChanged();
    }

    // ObservableCollection<TaskNodeViewModel> Predecessors → List<string> _model.PredecessorIds
    // への片方向同期。ロード時の初期投入もこれで反映される。
    private void OnPredecessorsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _model.PredecessorIds.Clear();
        foreach (var p in Predecessors) _model.PredecessorIds.Add(p.Id);
        OnPropertyChanged(nameof(PredecessorCount));
    }

    // 仮想ルートまで登る。selfが仮想ルートなら self を返す (Parent==null で停止)。
    public TaskNodeViewModel RootVm
    {
        get
        {
            var current = this;
            while (current.Parent != null) current = current.Parent;
            return current;
        }
    }

    // Indent/Outdent/Move で祖先-子孫関係になった依存を消し込む。
    // 除去された数を返す (MainViewModel が StatusMessage に流す)。
    internal int SanitizeDependenciesAfterMove()
        => DependencyValidator.SanitizeAncestryDependencies(RootVm);

    // 自分と全子孫の Depth を再通知 (Indent/Outdent 後の親付け替えで使う)。
    private void NotifyDepthChanged()
    {
        OnPropertyChanged(nameof(Depth));
        foreach (var child in Children)
        {
            child.NotifyDepthChanged();
        }
    }

    // 自分と全先祖の rolled-up 系プロパティを再評価させる。
    private void NotifyRollupChanged()
    {
        OnPropertyChanged(nameof(RolledUpEstimateHours));
        OnPropertyChanged(nameof(RolledUpRemainingHours));
        OnPropertyChanged(nameof(RolledUpActualHours));
        OnPropertyChanged(nameof(RolledUpProgressPercent));
        OnPropertyChanged(nameof(NeedsBreakdown));
        Parent?.NotifyRollupChanged();
    }

    // ---- Tree operations ----

    [RelayCommand]
    private void AddSiblingAfter()
    {
        if (Parent is null) return;
        var index = Parent.Children.IndexOf(this);
        var vm = new TaskNodeViewModel(new TaskNode(), Parent);
        Parent.Children.Insert(index + 1, vm);
        vm.IsSelected.Value = true;
    }

    [RelayCommand]
    private void AddChild()
    {
        var vm = new TaskNodeViewModel(new TaskNode(), this);
        Children.Add(vm);
        IsExpanded.Value = true;
        vm.IsSelected.Value = true;
    }

    [RelayCommand]
    private void Delete()
    {
        if (Parent is null) return;
        Parent.Children.Remove(this);
    }

    // 前の兄弟の子にする。先頭要素は何もしない。
    [RelayCommand]
    private void Indent()
    {
        if (Parent is null) return;
        var index = Parent.Children.IndexOf(this);
        if (index <= 0) return;
        var newParent = Parent.Children[index - 1];

        Parent.Children.Remove(this);
        newParent.Children.Add(this);
        newParent.IsExpanded.Value = true;
        IsSelected.Value = true;
        SanitizeDependenciesAfterMove();
    }

    // 親の兄弟にする (親の直後に挿入)。親がルートなら何もしない。
    [RelayCommand]
    private void Outdent()
    {
        var oldParent = Parent;
        if (oldParent is null || oldParent.Parent is null) return;
        var newParent = oldParent.Parent;
        var parentIndex = newParent.Children.IndexOf(oldParent);

        oldParent.Children.Remove(this);
        newParent.Children.Insert(parentIndex + 1, this);
        IsSelected.Value = true;
        SanitizeDependenciesAfterMove();
    }

    [RelayCommand]
    private void MoveUp()
    {
        if (Parent is null) return;
        var index = Parent.Children.IndexOf(this);
        if (index <= 0) return;
        Parent.Children.Move(index, index - 1);
    }

    [RelayCommand]
    private void MoveDown()
    {
        if (Parent is null) return;
        var index = Parent.Children.IndexOf(this);
        if (index < 0 || index >= Parent.Children.Count - 1) return;
        Parent.Children.Move(index, index + 1);
    }
}
