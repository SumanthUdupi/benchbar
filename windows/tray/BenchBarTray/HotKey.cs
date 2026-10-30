using System.Windows.Interop;

namespace BenchBar.Tray;

/// <summary>
/// The global Ctrl+Alt+U key: <c>RegisterHotKey</c> on a message-only <see cref="HwndSource"/>, so no visible window exists
/// and no keyboard hook (and no permission prompt) is involved. <see cref="TryRegister"/> is false when another app
/// already owns the key; nothing is retried by itself.
/// </summary>
internal sealed class HotKey(Action pressed) : IDisposable
{
    private const int Id = 0x4242;

    private HwndSource? _source;

    public bool IsRegistered { get; private set; }

    public bool TryRegister()
    {
        if (IsRegistered) return true;
        _source ??= CreateSource();
        IsRegistered = NativeMethods.RegisterHotKey(
            _source.Handle, Id, NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModNoRepeat, NativeMethods.VkU);
        return IsRegistered;
    }

    public void Unregister()
    {
        if (!IsRegistered || _source is null) return;
        NativeMethods.UnregisterHotKey(_source.Handle, Id);
        IsRegistered = false;
    }

    public void Dispose()
    {
        Unregister();
        _source?.Dispose();
        _source = null;
    }

    private HwndSource CreateSource()
    {
        var source = new HwndSource(new HwndSourceParameters("BenchBarHotKey")
        {
            ParentWindow = new IntPtr(NativeMethods.HwndMessage),
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        });
        source.AddHook(OnMessage);
        return source;
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != NativeMethods.WmHotKey || wParam.ToInt32() != Id) return IntPtr.Zero;
        handled = true;
        pressed();
        return IntPtr.Zero;
    }
}
