# WinMusic

Play music through your microphone on Windows at the highest quality possible, so your friends hear it the way you hear it on Spotify.

Tick the apps you want in your mic (Spotify, a browser, a game…). Everything else stays on your speakers only.

```
Spotify ─┐  per-app capture (no rerouting)
         ├─► WinMusic: 32-bit float mix ─► transparent limiter ─► CABLE Input ══► "CABLE Output" mic ─► Discord, games…
Your mic ┘
```

## Install

1. Download **WinMusic.exe** from [Releases](../../releases). It's one file and needs no .NET install.
2. Open it and click **Install VB-CABLE**. In the VB-CABLE window click **Install Driver**, then **restart your PC**.
   VB-CABLE is a free virtual audio cable (donationware by VB-Audio, <https://vb-audio.com/Cable/>). It's what Windows shows as a microphone.
   The official, unmodified VB-CABLE package is **built into WinMusic.exe**, so this works offline and keeps working even if the VB-Audio website goes away (see `third_party/vbcable`).
3. Open WinMusic again and click **Optimize Windows for music**.
4. Tick **Spotify** (or any app), then click **Start**.
5. In Discord (or anywhere else) pick **CABLE Output (VB-Audio Virtual Cable)** as your microphone.

Needs Windows 10 version 2004 or newer, or Windows 11.

## Getting the best sound on the other end

The voice app on the other end compresses your audio. Its "voice cleanup" filters damage music badly, so in Discord, under **Settings → Voice & Video**:

- Noise Suppression: **None**
- Echo Cancellation: **Off**
- Automatic Gain Control: **Off**
- Input Sensitivity: turn off auto and drag the slider fully left, or use Push-to-Talk
- Voice channels with a higher bitrate (boosted servers, up to 384 kbps) sound much better

In Spotify, set Audio quality to **Lossless** or **Very high**.

**Volume (verified on real Windows in CI):**
- Your speaker or headphone volume does **not** change what friends hear. Use it to set your own listening level.
- Spotify's own volume slider and its slider in the Windows Volume Mixer **do** change what friends hear. Keep them at 100% and use WinMusic's Music volume slider instead.

## Why it sounds better than the usual setups

| | WinMusic |
|---|---|
| Capturing the app | Windows *process loopback* takes the app's own audio directly. Its output device is never changed and no system mixer sits in between. |
| Mixing | 32-bit float throughout, at the cable's native 48 kHz |
| Clock drift between devices | The buffer level is measured from WASAPI hardware timestamps, then a 160-tap Kaiser-windowed sinc resampler is adjusted by a few ppm. No dropped or repeated samples, so no clicks. |
| Resampler quality | THD+N of about −140 dB, aliasing rejected by more than 138 dB, flat to 20 kHz within 0.05 dB (see unit tests) |
| Volume at 0 dB | Exactly ×1.0, so the audio passes through bit for bit |
| Clipping | A look-ahead limiter at −1 dBFS that only acts when music plus voice would clip. Otherwise it's bit-transparent. |
| Verified on real Windows | CI installs VB-CABLE on a Windows machine, plays test tones through the whole chain and measures what comes out of the virtual mic. See the Actions run summary or `selftest-report.md` in each release. |

## Building

```
dotnet test tests/WinMusic.Core.Tests
dotnet publish src/WinMusic -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Project layout:

- `src/WinMusic.Core`: DSP (resampler, drift-compensated buffer, limiter, analysis). Cross-platform and unit-tested.
- `src/WinMusic.Audio`: hand-written WASAPI interop, per-app capture, the engine, and Windows setup helpers.
- `src/WinMusic`: the WPF app.
- `src/WinMusic.SelfTest`: the real-device test that CI runs.

## License

MIT. VB-CABLE is not part of this project and has its own license.
