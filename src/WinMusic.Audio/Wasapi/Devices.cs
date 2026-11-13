using System.Runtime.InteropServices;
using WinMusic.Audio.Interop;

namespace WinMusic.Audio.Wasapi;

public enum DeviceFlow { Playback, Recording }

public sealed record AudioDevice(string Id, string Name, DeviceFlow Flow)
{
    public bool IsCableInput => Flow == DeviceFlow.Playback && Name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase);
    public bool IsCableOutput => Flow == DeviceFlow.Recording && Name.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase);
    public bool IsVirtualCable => Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) || IsCableInput || IsCableOutput;
    public override string ToString() => Name;
}

/// <summary>Device enumeration helpers. All COM work happens on MTA threads.</summary>
public static class Devices
{
    internal static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    /// <summary>Runs COM work on an MTA thread (WPF's UI thread is STA).</summary>
    public static T Mta<T>(Func<T> f)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA) return f();
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception e) { error = e; } });
        t.SetApartmentState(ApartmentState.MTA);
        t.IsBackground = true;
        t.Start();
        t.Join();
        if (error != null) throw error;
        return result;
    }

    public static void Mta(Action a) => Mta(() => { a(); return 0; });

    public static List<AudioDevice> List(DeviceFlow flow) => Mta(() =>
    {
        var list = new List<AudioDevice>();
        var en = CreateEnumerator();
        Hr.Check(en.EnumAudioEndpoints(flow == DeviceFlow.Playback ? EDataFlow.Render : EDataFlow.Capture, Native.DEVICE_STATE_ACTIVE, out var col), "EnumAudioEndpoints");
        col.GetCount(out uint n);
        for (uint i = 0; i < n; i++)
        {
            if (col.Item(i, out var dev) != 0) continue;
            dev.GetId(out string id);
            list.Add(new AudioDevice(id, GetName(dev), flow));
            Marshal.ReleaseComObject(dev);
        }
        Marshal.ReleaseComObject(col);
        Marshal.ReleaseComObject(en);
        return list;
    });

    public static string? DefaultId(DeviceFlow flow, bool communications = false) => Mta<string?>(() =>
    {
        var en = CreateEnumerator();
        int hr = en.GetDefaultAudioEndpoint(flow == DeviceFlow.Playback ? EDataFlow.Render : EDataFlow.Capture,
            communications ? ERole.Communications : ERole.Console, out var dev);
        if (hr != 0) return null;
        dev.GetId(out string id);
        Marshal.ReleaseComObject(dev);
        return id;
    });

    public static AudioDevice? FindCableInput() => List(DeviceFlow.Playback).FirstOrDefault(d => d.IsCableInput);
    public static AudioDevice? FindCableOutput() => List(DeviceFlow.Recording).FirstOrDefault(d => d.IsCableOutput);

    internal static string GetName(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(0, out var store) != 0) return "Unknown device";
        var key = PropertyKey.FriendlyName;
        string name = "Unknown device";
        if (store.GetValue(ref key, out var pv) == 0)
        {
            name = pv.GetString() ?? name;
            PropVariant.PropVariantClear(ref pv);
        }
        Marshal.ReleaseComObject(store);
        return name;
    }

    internal static IMMDevice GetDevice(string id)
    {
        var en = CreateEnumerator();
        Hr.Check(en.GetDevice(id, out var dev), "GetDevice");
        Marshal.ReleaseComObject(en);
        return dev;
    }

    internal static IAudioClient ActivateClient(IMMDevice dev)
    {
        var iid = Native.IID_IAudioClient;
        Hr.Check(dev.Activate(ref iid, Native.CLSCTX_ALL, IntPtr.Zero, out object o), "IMMDevice.Activate");
        return (IAudioClient)o;
    }

    /// <summary>Windows volume slider of a device (0..1) and its mute state.</summary>
    public static (float volume, bool muted) GetEndpointVolume(string id) => Mta(() =>
    {
        var dev = GetDevice(id);
        var iid = Native.IID_IAudioEndpointVolume;
        Hr.Check(dev.Activate(ref iid, Native.CLSCTX_ALL, IntPtr.Zero, out object o), "Activate(IAudioEndpointVolume)");
        var ev = (IAudioEndpointVolume)o;
        ev.GetMasterVolumeLevelScalar(out float v);
        ev.GetMute(out bool m);
        Marshal.ReleaseComObject(ev);
        Marshal.ReleaseComObject(dev);
        return (v, m);
    });

    public static void SetEndpointVolume(string id, float volume, bool mute = false) => Mta(() =>
    {
        var dev = GetDevice(id);
        var iid = Native.IID_IAudioEndpointVolume;
        Hr.Check(dev.Activate(ref iid, Native.CLSCTX_ALL, IntPtr.Zero, out object o), "Activate(IAudioEndpointVolume)");
        var ev = (IAudioEndpointVolume)o;
        var ctx = Guid.Empty;
        Hr.Check(ev.SetMasterVolumeLevelScalar(volume, ref ctx), "SetMasterVolumeLevelScalar");
        Hr.Check(ev.SetMute(mute, ref ctx), "SetMute");
        Marshal.ReleaseComObject(ev);
        Marshal.ReleaseComObject(dev);
    });

    /// <summary>The format the Windows audio engine mixes at for this device (shared mode).</summary>
    public static WaveFormatInfo GetMixFormat(string id) => Mta(() =>
    {
        var dev = GetDevice(id);
        var client = ActivateClient(dev);
        Hr.Check(client.GetMixFormat(out IntPtr p), "GetMixFormat");
        var f = WaveFormatInfo.Parse(p);
        Marshal.FreeCoTaskMem(p);
        Marshal.ReleaseComObject(client);
        Marshal.ReleaseComObject(dev);
        return f;
    });
}
