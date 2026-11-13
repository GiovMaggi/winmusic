using System.Runtime.InteropServices;
using WinMusic.Audio.Interop;

namespace WinMusic.Audio.Wasapi;

/// <summary>
/// Activates a WASAPI client that records only the audio of one process tree
/// (Windows 10 version 2004 / build 19041 or newer). This is how we grab Spotify
/// without touching the user's speakers or Windows routing at all.
/// </summary>
internal static class ProcessLoopback
{
    [ComVisible(true)]
    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);
        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            Done.Set();
            return 0;
        }
    }

    public static bool IsSupported => Environment.OSVersion.Version.Build >= 19041;

    public static IAudioClient Activate(uint pid, bool includeTree)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Capturing a single app needs Windows 10 version 2004 or newer.");

        var p = new AudioClientActivationParams { ActivationType = 1, TargetProcessId = pid, ProcessLoopbackMode = includeTree ? 0 : 1 };
        int size = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr pParams = Marshal.AllocHGlobal(size);
        IntPtr pVariant = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.StructureToPtr(p, pParams, false);
            for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(pVariant, i, 0);
            Marshal.WriteInt16(pVariant, 0, (short)PropVariant.VT_BLOB);
            Marshal.WriteInt32(pVariant, 8, size);         // BLOB.cbSize
            Marshal.WriteIntPtr(pVariant, 16, pParams);    // BLOB.pBlobData

            var handler = new CompletionHandler();
            Hr.Check(Native.ActivateAudioInterfaceAsync(Native.VirtualAudioDeviceProcessLoopback, Native.IID_IAudioClient, pVariant, handler, out var op),
                "ActivateAudioInterfaceAsync");
            if (!handler.Done.Wait(8000)) throw new TimeoutException("Process loopback activation timed out");
            Hr.Check(op.GetActivateResult(out int result, out object iface), "GetActivateResult");
            Hr.Check(result, "Process loopback activation");
            Marshal.ReleaseComObject(op);
            return (IAudioClient)iface;
        }
        finally
        {
            Marshal.FreeHGlobal(pVariant);
            Marshal.FreeHGlobal(pParams);
        }
    }
}
