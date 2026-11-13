using System.Runtime.InteropServices;

namespace WinMusic.Audio.Wasapi;

/// <summary>Plain description of an audio format, plus helpers to build/parse WAVEFORMATEX(TENSIBLE).</summary>
public sealed record WaveFormatInfo(int SampleRate, int Channels, int BitsPerSample, int ValidBits, bool IsFloat)
{
    public override string ToString() =>
        $"{SampleRate / 1000.0:0.###} kHz, {(IsFloat ? $"{BitsPerSample}-bit float" : $"{ValidBits}-bit")}, {Channels} ch";

    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    internal static WaveFormatInfo Parse(IntPtr p)
    {
        ushort tag = (ushort)Marshal.ReadInt16(p, 0);
        int ch = Marshal.ReadInt16(p, 2);
        int rate = Marshal.ReadInt32(p, 4);
        int bits = Marshal.ReadInt16(p, 14);
        int valid = bits;
        bool isFloat = tag == 3;
        if (tag == 0xFFFE)
        {
            valid = Marshal.ReadInt16(p, 18);
            var sub = Marshal.PtrToStructure<Guid>(p + 24);
            isFloat = sub == SubtypeFloat;
        }
        return new WaveFormatInfo(rate, ch, bits, valid == 0 ? bits : valid, isFloat);
    }

    /// <summary>Allocates a WAVEFORMATEXTENSIBLE (free with Marshal.FreeHGlobal).</summary>
    internal static IntPtr Alloc(int rate, int channels, bool isFloat, int containerBits, int validBits)
    {
        IntPtr p = Marshal.AllocHGlobal(40);
        int block = channels * containerBits / 8;
        Marshal.WriteInt16(p, 0, unchecked((short)0xFFFE));
        Marshal.WriteInt16(p, 2, (short)channels);
        Marshal.WriteInt32(p, 4, rate);
        Marshal.WriteInt32(p, 8, rate * block);
        Marshal.WriteInt16(p, 12, (short)block);
        Marshal.WriteInt16(p, 14, (short)containerBits);
        Marshal.WriteInt16(p, 16, 22);
        Marshal.WriteInt16(p, 18, (short)validBits);
        int mask = channels switch { 1 => 0x4, 2 => 0x3, _ => (1 << Math.Min(channels, 18)) - 1 };
        Marshal.WriteInt32(p, 20, mask);
        Marshal.StructureToPtr(isFloat ? SubtypeFloat : SubtypePcm, p + 24, false);
        return p;
    }

    internal static IntPtr AllocFloat(int rate, int channels) => Alloc(rate, channels, true, 32, 32);
}
