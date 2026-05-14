using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace Inklet;

/// <summary>
/// Custom entry point. The XAML markup compiler's auto-generated <c>Main</c> in
/// <c>App.g.i.cs</c> is decorated <c>[STAThread]</c> when the project is built standalone
/// (i.e. via <c>dotnet publish</c> without a wapproj parent). WinUI 3's
/// <see cref="Application.Start(ApplicationInitializationCallback)"/> requires the calling
/// thread to be in the multi-threaded apartment — running an STA-built binary inside an
/// MSIX container therefore crashes immediately with
/// <c>The application called an interface that was marshalled for a different thread</c>
/// (<c>0x8001010E</c>).
///
/// Setting the <c>DISABLE_XAML_GENERATED_MAIN</c> compile constant in the csproj suppresses
/// the auto-generated <c>Main</c> so this custom one wins, and the <c>[MTAThread]</c>
/// attribute below makes the binary work identically when launched standalone or when
/// activated by the OS from inside a packaged MSIX.
/// </summary>
public static class Program
{
    [MTAThread]
    private static void Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
