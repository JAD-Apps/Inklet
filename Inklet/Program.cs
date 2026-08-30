using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace Inklet;

/// <summary>
/// Custom entry point. The XAML markup compiler's auto-generated <c>Main</c> in
/// <c>App.g.i.cs</c> is suppressed by the <c>DISABLE_XAML_GENERATED_MAIN</c> compile
/// constant in the csproj so this one wins, which is what lets the binary set up its
/// own synchronisation context and perf marks before <see cref="App"/> is constructed.
///
/// The apartment must be STA, as it is in WinUI 3's own generated entry point.
/// This was <c>[MTAThread]</c> for a while, on the belief that <c>Application.Start</c>
/// required MTA and that an STA binary died inside an MSIX container with
/// <c>0x8001010E</c>. That is not the case: STA launches correctly both packaged and
/// unpackaged, and MTA carried a hidden cost. UI Automation providers are COM objects
/// that expect to be created on an STA UI thread; on an MTA one, the first time any
/// client walked into the XAML content island, provider bring-up faulted inside
/// Microsoft.UI.Xaml.dll with an access violation and killed the process. Since that
/// walk is exactly what Narrator, Voice Access and store accessibility scans do, the
/// app could be crashed by any assistive technology simply inspecting it.
///
/// If a future change appears to need MTA here, note that it will silently reintroduce
/// that crash: verify with a UI Automation descendant walk, not just by launching.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Diagnostics.Perf.Mark("AppMain");
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
