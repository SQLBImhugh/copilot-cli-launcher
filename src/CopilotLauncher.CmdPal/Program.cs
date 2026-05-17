using System;
using System.Threading;
using Microsoft.CommandPalette.Extensions;
using Shmuelie.WinRTServer;
using Shmuelie.WinRTServer.CsWinRT;

namespace CopilotLauncher.CmdPal;

/// <summary>
/// Entry point for the out-of-process COM server. When PT Command Palette
/// activates the extension (via the AppExtension framework), Windows
/// launches this exe with the -RegisterProcessAsComServer argument. We
/// register the <see cref="CopilotLauncherExtension"/> class and block
/// until the extension is disposed.
///
/// Pattern mirrors PowerToys' own SamplePagesExtension/Program.cs.
/// </summary>
public static class Program
{
    [MTAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "-RegisterProcessAsComServer")
        {
            // ComServer is IAsyncDisposable not IDisposable in Shmuelie 2.x;
            // do not use `using`. Stop() releases everything we registered.
            var server = new ComServer();
            using var disposedEvent = new ManualResetEvent(false);

            // Single-instance: the same CopilotLauncherExtension instance is
            // returned every time the host asks for IExtension. This lets us
            // reuse the underlying SessionDiscoveryService / ShortcutsService /
            // LaunchService across all GetItems() calls and keep the cache warm.
            var extensionInstance = new CopilotLauncherExtension(disposedEvent);
            server.RegisterClass<CopilotLauncherExtension, IExtension>(() => extensionInstance);
            server.Start();

            disposedEvent.WaitOne();
            server.Stop();
            server.UnsafeDispose();
        }
        else
        {
            Console.WriteLine("Not being launched as a Command Palette extension. Exiting.");
        }
    }
}
