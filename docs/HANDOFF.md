# Handoff — Voice Modulator (MAUI Android)

Last updated: 2026-09-18 · Branch: `feature/phase1-core-audio` (not merged to `main`)

## Start here
1. Read `docs/Requirements.md` (full spec, v2). Section numbers (§) below refer to it.
2. Read this file, then the code under `VoiceModulator/Audio` and `VoiceModulator/Platforms/Android/Audio`.
3. **Immediate task: fix the open bug below, then finish Phase 1 testing with the user.** Do not start Phase 2 until the user has signed off on Phase 1.

## Working agreements with the user
- Personal project, **limited budget**: for research, spawn cheaper agents (e.g. `model: "haiku"`) instead of doing lookups yourself. Keep outputs concise.
- Work on feature branches, never commit directly to `main`.
- **Stop for the user's review after each phase.**
- The user tests on a real device and reports back; they may be away for days between sessions.

## Agreed decisions (don't re-ask)
| Topic | Decision |
|---|---|
| Native audio | AAudio C API via P/Invoke from C# (`LibraryImport`, function-pointer callbacks). **No Oboe / NDK / C++.** |
| DSP | Written in C#; no SoundTouch. Quality "decent, not studio" is accepted. |
| Min Android | Real minimum is API 31. Installable minSdk stays 21 so older devices open `UnsupportedPage` ("Android 12 or newer required"). All Android audio classes carry `[SupportedOSPlatform("android31.0")]`. |
| Platforms | Android + iOS (iOS is a stub: `UnsupportedAudioSession`). Windows/MacCatalyst removed. |
| Storage | MAUI `Preferences` (keys in `Services/SettingsKeys.cs`). |
| UI | Plain XAML + MVVM. Only extra library: CommunityToolkit.Mvvm 8.4.0. **Use field-based `[ObservableProperty]` (`_camelCase` fields)** — the partial-property syntax failed to compile in this setup. |
| Bluetooth reconnect | Don't force reconnect; just detect when the OS reconnects. |
| Privacy/Play Store | Keep the in-app mic disclosure (may publish later). |
| Latency | Loopback measurement (§4.2.2) **deferred**; estimates only for now. Revisit later. |

## Phase plan
1. **Phase 1 (current, built, awaiting sign-off):** phone mic → wired headphones, 4 effects (+ "Natural" bypass), gain ceiling + limiter, foreground service with notification/Stop, Start/Stop UI.
2. Phase 2: device selection UI, auto-selection, live hot-plug (wired + Bluetooth A2DP output), "new device — switch?" prompt, no-valid-device blocking (§2.5). Will need `FOREGROUND_SERVICE_CONNECTED_DEVICE` + Bluetooth permissions.
3. Phase 3: Bluetooth mic input (HFP/SCO), howling detection + ducking (`OutputLimiter.DuckGain` is the hook), feedback risk warnings (§4.1).
4. Phase 4: static latency capability probe + per-pairing cache (§4.2.1, §4.2.4), Recalibrate. Loopback measurement later.

Also noted but not done: input AGC (currently fixed `InputGain = 1.5`), a Settings screen (§3.2), and the app ID is still the template's `com.companyname.voicemodulator`.

## Architecture (what exists)
```
VoiceModulator/
  Audio/IAudioSession.cs            platform contract: status, device availability, levels, start/stop/update
  Audio/Dsp/                        platform-independent, allocation-free on the audio thread
    VoiceProcessor.cs               chain: input gain → effect → NaN guard → OutputLimiter; peak meters
    VoiceEffects.cs                 enum VoiceEffectKind {Natural, Witch, Cartoon, Monster, Robot} + effects
    PitchShifter.cs                 two-tap crossfaded delay-line pitch shifter (~window/2 latency)
    Biquad.cs, OutputLimiter.cs     RBJ filters; user gain × duck gain → peak limiter → hard clip 0.95
  Platforms/Android/Audio/
    AAudioNative.cs                 P/Invokes into libaaudio.so
    DuplexAudioEngine.cs            output stream data callback reads input non-blocking (Oboe FullDuplex pattern),
                                    drains stale input for the first 10 callbacks, mono → N output channels
    AndroidAudioSession.cs          device lookup (built-in mic, wired/USB output only), AudioDeviceCallback,
                                    mutes + stops on output removal / AAudio disconnect error
    VoiceForegroundService.cs       FGS type microphone; notification with Stop; owns engine lifetime
  ViewModels/MainViewModel.cs       settings persistence, permissions, Start/Stop, 100 ms level-meter poll
  MainPage.xaml                     device info, effect picker, intensity & gain sliders, Start, status, meters, disclosure overlay
  UnsupportedPage.cs, App.xaml.cs   < API 31 gate
```
Key invariants:
- The phone's own speaker is **never** an output (§1.1). Output is only opened on an explicit wired/USB device ID; on removal the engine is muted immediately.
- Nothing on the audio callback path may allocate or block (GC pauses → glitches). Parameters cross threads as plain fields / `Volatile`.
- Streams must not be closed from AAudio callback threads — `OnStreamError` hops to `Task.Run`.

## Open bug (top priority)
**After starting with Witch, switching to other effects produces no audible output** (user report, 2026-09-12). Witch worked. Unknown whether switches were made while running or stopped. Device logcat showed no AAudio errors; streams opened/closed normally.

Done so far (commit `fb02c99`): NaN/Inf guard in `VoiceProcessor` (resets effect + limiter), limiter envelope guard, and **Mic level / Output level meters** on the main screen. Hypothesis: NaN poisoning filter/limiter state — unconfirmed.

Asked the user to re-test and report:
- mic moves, output doesn't → bug inside effect/limiter path
- neither moves → input stall
- both move, silent → playback path
- now works → NaN was the cause

Other suspects if not fixed: `Picker` `SelectedItem` binding with enum values under XAML SourceGen (check `SelectedEffect` actually changes — add a debug log in `OnSelectedEffectChanged` / `ApplySettings`); effect `Reset()` on switch; `PitchShifter` ratio/phase edge cases. Consider unit-testing the DSP offline (feed a sine through each effect, assert finite, non-zero output) — the DSP folder has no Android dependencies.

## Remaining Phase 1 checks (user)
Sliders live, screen locked/backgrounded keeps working, notification Stop button, and unplug mid-session does **not** reroute to the phone speaker.

## Build / deploy
- .NET SDK 10.0.401, MAUI workloads installed. Build: `dotnet build VoiceModulator -f net10.0-android`; deploy+run: `dotnet build VoiceModulator -t:Run -f net10.0-android`.
- `adb` isn't on PATH: `"/c/Program Files (x86)/Android/android-sdk/platform-tools/adb.exe"`.
- Test device: **Google Pixel 7a, Android API 37**, USB debugging. User has wired earbuds; Bluetooth devices unconfirmed.
- Screenshots: `adb exec-out screencap -p > file.png` and read the image.

## Commits on the branch
- `44cdefa` Phase 1: AAudio duplex engine, voice effects, limiter, foreground service and main UI
- `fb02c99` Add NaN guard in processing chain and mic/output level meters
