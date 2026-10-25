using System;
using System.Windows.Interop;

namespace ClipNest.App.Platform;

/// <summary>
/// a hidden window that receives clipboard, hotkey and tray messages.
/// it is a normal hidden window, not a message only window,
/// because only normal windows hear that the taskbar was restarted.
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    private const int WsExToolWindow = 0x00000080;
    private readonly HwndSource _source;

    public MessageWindow()
    {
        var parameters = new HwndSourceParameters("ClipNestMessageWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = WsExToolWindow,
        };
        _source = new HwndSource(parameters);
    }

    public IntPtr Handle => _source.Handle;

    public void AddHook(HwndSourceHook hook) => _source.AddHook(hook);

    public void RemoveHook(HwndSourceHook hook) => _source.RemoveHook(hook);

    public void Dispose() => _source.Dispose();
}
