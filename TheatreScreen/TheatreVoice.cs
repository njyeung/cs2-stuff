using System.Runtime.InteropServices;

namespace TheatreScreen;

/// <summary>Binding to the TheatreVoice Metamod plugin, which posts prebuilt network messages.</summary>
public sealed class TheatreVoice : IDisposable
{
    public const int BufVoice = 2; // NetChannelBufType_t::BUF_VOICE

    private nint _handle;
    private readonly PostMessage _post;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PostMessage(int messageId, [In] byte[] data, int size, ulong recipients, int bufType);

    [DllImport("libdl.so.2", EntryPoint = "dlopen")]
    private static extern nint Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libdl.so.2", EntryPoint = "dlclose")]
    private static extern int Close(nint handle);

    private TheatreVoice(nint handle)
    {
        _post = Marshal.GetDelegateForFunctionPointer<PostMessage>(NativeLibrary.GetExport(handle, "TheatreVoice_Post"));
        _handle = handle;
    }

    public static bool TryAttach(string path, out TheatreVoice? voice, out string error)
    {
        voice = null;
        error = "TheatreVoice needs a Linux game server.";
        if (!OperatingSystem.IsLinux()) return false;
        // RTLD_NOW | RTLD_NOLOAD: attach only to the copy Metamod loaded and initialized.
        var handle = Open(Path.GetFullPath(path), 2 | 4);
        if (handle == 0)
        {
            error = "TheatreVoice is not loaded. Install addons/theatrevoice and its metamod .vdf, restart, and check meta list.";
            return false;
        }
        try
        {
            voice = new TheatreVoice(handle);
            error = "";
            return true;
        }
        catch (EntryPointNotFoundException ex)
        {
            Close(handle);
            error = $"TheatreVoice is out of date: {ex.Message}";
            return false;
        }
    }

    /// <summary>Posts protobuf bytes as network message messageId to the slots set in recipients.</summary>
    /// <exception cref="InvalidOperationException">The plugin rejected the message.</exception>
    public void Post(int messageId, byte[] data, ulong recipients, int bufType = BufVoice)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        var result = _post(messageId, data, data.Length, recipients, bufType);
        if (result != 0)
            throw new InvalidOperationException(result switch
            {
                -1 => "TheatreVoice was not initialized by Metamod.",
                -2 => $"Unknown network message id {messageId}.",
                -3 => "The game could not allocate the message.",
                -4 => "The game rejected the message bytes.",
                _ => $"TheatreVoice error {result}.",
            });
    }

    public void Dispose()
    {
        if (_handle == 0) return;
        Close(_handle); // Release only our extra reference; Metamod retains its own.
        _handle = 0;
    }
}
