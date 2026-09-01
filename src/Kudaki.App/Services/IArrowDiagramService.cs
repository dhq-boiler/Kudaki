using Kudaki.App.ViewModels;

namespace Kudaki.App.Services;

// VM がアローダイアグラム Window を直接 new しないための境界。
// View 側で WpfArrowDiagramService が Window を開く。
public interface IArrowDiagramService
{
    void Show(TaskNodeViewModel parent);
}
