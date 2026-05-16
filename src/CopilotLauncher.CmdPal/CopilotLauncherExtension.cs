using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CommandPalette.Extensions;

namespace CopilotLauncher.CmdPal;

/// <summary>
/// IExtension implementation. The Guid attribute MUST match the
/// com:Class Id in Package.appxmanifest. PT Command Palette uses
/// CoCreateInstance with that CLSID to spawn us.
/// </summary>
[ComVisible(true)]
[Guid("50BE806C-4555-48BE-9D31-7E427ECA40C0")]
[ComDefaultInterface(typeof(IExtension))]
public sealed partial class CopilotLauncherExtension : IExtension, IDisposable
{
    private readonly ManualResetEvent _disposedEvent;
    private readonly CopilotLauncherCommandsProvider _provider = new();

    public CopilotLauncherExtension(ManualResetEvent disposedEvent)
    {
        _disposedEvent = disposedEvent;
    }

    public object? GetProvider(ProviderType providerType) => providerType switch
    {
        ProviderType.Commands => _provider,
        _ => null,
    };

    public void Dispose() => _disposedEvent.Set();
}
