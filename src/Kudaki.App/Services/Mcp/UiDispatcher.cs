using System;
using System.Threading.Tasks;

namespace Kudaki.App.Services.Mcp;

internal static class UiDispatcher
{
    internal static Task RunOnUiAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}
