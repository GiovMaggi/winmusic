using System.Runtime.InteropServices;

namespace WinMusic.Audio.Interop;

// Minimal, hand-written Windows Core Audio (WASAPI) interop. Only what WinMusic needs.
// Method order in every interface must match the Windows SDK headers exactly (vtable layout).

internal enum EDataFlow { Render = 0, Capture = 1, All = 2 }
internal enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

internal static class Hr
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const int AUDCLNT_E_UNSUPPORTED_FORMAT = unchecked((int)0x88890008);
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);
    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new AudioException(what, hr);
    }
}

public sealed class AudioException : Exception
{
    public int HResult32 { get; }
    public AudioException(string what, int hr) : base(Describe(what, hr)) { HResult32 = hr; HResult = hr; }

    private static string Describe(string what, int hr) => hr == Hr.E_ACCESSDENIED && what.Contains("capture")
        ? "Windows is blocking microphone access. Open Settings → Privacy & security → Microphone and turn on “Microphone access” and “Let desktop apps access your microphone”."
        : $"{what} failed (0x{hr:X8})";
    public bool DeviceInvalidated => HResult32 == Hr.AUDCLNT_E_DEVICE_INVALIDATED;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid fmtid;
    public int pid;
    public PropertyKey(Guid g, int p) { fmtid = g; pid = p; }
    public static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    public static readonly PropertyKey DeviceDesc = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    public static readonly PropertyKey AudioEngineDeviceFormat = new(new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort vt;
    public ushort r1, r2, r3;
    public IntPtr p1;
    public IntPtr p2;

    public const ushort VT_LPWSTR = 31;
    public const ushort VT_BLOB = 65;

    public string? GetString() => vt == VT_LPWSTR ? Marshal.PtrToStringUni(p1) : null;

    [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PropVariant pvar);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IPropertyStore properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint bufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint numFramesRequested, out IntPtr data);
    [PreserveSig] int ReleaseBuffer(uint numFramesWritten, uint flags);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint numFramesRead);
    [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
    [PreserveSig] int GetMasterVolume(out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    // IAudioSessionManager
    [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr sessionControl);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr audioVolume);
    // IAudioSessionManager2
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    [PreserveSig] int RegisterSessionNotification(IntPtr notification);
    [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
    [PreserveSig] int RegisterDuckNotification(IntPtr sessionId, IntPtr duckNotification);
    [PreserveSig] int UnregisterDuckNotification(IntPtr duckNotification);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
}

[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    // IAudioSessionControl
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName(out IntPtr name);
    [PreserveSig] int SetDisplayName(IntPtr value, IntPtr eventContext);
    [PreserveSig] int GetIconPath(out IntPtr path);
    [PreserveSig] int SetIconPath(IntPtr value, IntPtr eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingParam);
    [PreserveSig] int SetGroupingParam(IntPtr groupingParam, IntPtr eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
    // IAudioSessionControl2
    [PreserveSig] int GetSessionIdentifier(out IntPtr id);
    [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

// ---- Process loopback activation (Windows 10 2004+) ----

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

/// <summary>Marker: tells COM our completion handler may be called from any thread.</summary>
[ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject { }

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public int ActivationType;        // 1 = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
    public uint TargetProcessId;
    public int ProcessLoopbackMode;   // 0 = include target process tree, 1 = exclude
}

// ---- IPolicyConfig (undocumented but stable since Windows 7; used by many audio tools) ----

[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class PolicyConfigClient { }

[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int useDefault, out IntPtr format);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int useDefault, out long defaultPeriod, out long minPeriod);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
    [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
    [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
}

internal static class Native
{
    public const uint CLSCTX_ALL = 0x17;
    public const uint DEVICE_STATE_ACTIVE = 0x1;

    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint AUDCLNT_STREAMFLAGS_NOPERSIST = 0x00080000;
    public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;

    public const uint AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY = 0x1;
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    public const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";

    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    public static readonly Guid IID_ISimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");
    public static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateEventW(IntPtr attrs, bool manualReset, bool initialState, IntPtr name);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);
    [DllImport("avrt.dll", SetLastError = true)]
    public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr p);
}
