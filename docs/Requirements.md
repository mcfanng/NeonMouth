# Android Voice Modulator App — Requirements Document (v2)

**Platform:** .NET MAUI (Android target) · **Status:** Ready for implementation handoff · Supersedes v1 (Copilot draft)

## Revision Notes (v1 → v2)

This revision closes gaps identified in v1, incorporates decisions made during requirements review, and adds sections needed before handoff to an implementation agent. Key changes:

- Input scope expanded: Bluetooth mic AND the device's built-in mic are both in scope for v1 (v1 treated the device mic as a future feature).
- Added a Latency Capability Check (§4.2) — the app measures real achievable latency per device pairing instead of assuming a fixed number.
- Added a full Feedback & Howling Protection section (§4.1) — the original single mitigation (block same-device input/output) only prevents electrical loopback, not acoustic feedback. Manual gain limiting and active howling detection are now required.
- Corrected the latency target: <100ms end-to-end is not realistic over standard Bluetooth and is now framed as a best-case, hardware-dependent number, not a hard requirement.
- Added Bluetooth profile detail (§5.4), edge cases (§7), a glossary (§13), and open risks for the implementing agent (§11).
- Latency Capability Check now runs once per input/output pairing and is cached (§4.2.4), rather than every time the built-in mic is used.
- Added wired headphones/earbuds as a supported output destination, clarified that a wired headset's inline mic is out of scope as an input source, and removed the phone's own speaker as a selectable output entirely rather than allowing-then-blocking it per combination (§1.1, §2.1). *(Superseded below — wired mic input was later re-enabled.)*
- Added device auto-selection and live hot-plug detection (§2.5): the app defaults to the best available input/output on launch, detects new connections while running without requiring a relaunch (with a documented limitation for a fully closed app), and blocks Start entirely with a clear prompt when no valid input/output exists.
- Reversed the wired-mic restriction: a wired microphone (inline mic on a wired headset) is now a supported input source, provided the output is not the phone's own built-in speaker (§1.1, §2.1). This also means a single wired headset can now be used as both mic input and headphone output — unlike Bluetooth, that same-device combination is low-risk here and is explicitly supported, not blocked.
- Added a future headset button control spec (§12.1): single/double/triple press for start-stop/next-effect/previous-effect, and volume buttons for output gain, with implementation notes pointing at Android's existing media-session button-press handling rather than custom debounce logic.

## 1. Overview

This document defines the functional, technical, and UX requirements for an Android application built with .NET MAUI. The app captures voice audio from a selected input source, applies real-time voice modulation effects, and routes the processed audio to a selected output destination. The app must continue operating with the screen off.

### 1.1 Supported Audio Paths (v1)

Rather than allowing every input/output combination and then blocking specific pairings, the picker itself only offers safe categories:

- **Input sources:** Bluetooth microphone, the phone's built-in microphone, or a wired microphone (inline mic on a wired headset). A wired mic is allowed as long as the selected output is not the phone's own speaker — which is guaranteed structurally, since the phone's speaker is never offered as an output choice at all (see below).
- **Output destinations:** Bluetooth speaker, or wired headphones/earbuds (headphone jack or USB-C). The phone's own built-in speaker is **never** a selectable output destination, regardless of input source — it is not offered as a choice at all, rather than allowed-then-blocked per combination.

Risk profile of the remaining combinations:

| Input | Output | Status | Feedback risk |
|---|---|---|---|
| Bluetooth microphone (headset/earbud) | Bluetooth speaker (different device) | Supported | Low — mic and speaker are physically separated |
| Phone's built-in microphone | Bluetooth speaker | Supported | Medium–High — depends on speaker volume and distance from phone; requires active protection (§4.1) |
| Wired microphone (inline mic) | Bluetooth speaker | Supported | Medium — similar to the built-in mic case; mic position on the cord/earbud and speaker volume/distance determine risk, requires active protection (§4.1) |
| Bluetooth microphone | Wired headphones/earbuds | Supported | Very low — sealed wired output, negligible acoustic leakage into the Bluetooth mic |
| Phone's built-in microphone | Wired headphones/earbuds | Supported — recommended default | Very low — sealed wired output next to the ear, essentially no leakage back into the built-in mic; also the lowest-latency path overall (§4.2) |
| Wired microphone (inline mic) | Wired headphones/earbuds (same accessory) | Supported | Very low — this is the standard phone-call/gaming-headset configuration: sealed near-ear output with a close-talk mic, minimal acoustic leakage. The Bluetooth same-device restriction does **not** apply here — this combination is the intended primary use case for a wired headset, not an edge case to block. |
| Bluetooth microphone | Same Bluetooth device | Blocked | N/A — disallowed by device-pairing restriction (Bluetooth only, see above) |

Rationale: allowing the built-in mic (and now the wired mic) changes the risk profile substantially, since the mic and speaker can end up close together (e.g., phone on a desk next to a Bluetooth speaker). This is why §4.1 is a required section rather than a single restriction rule. Wired headphone output sidesteps most of that risk regardless of which mic feeds it, and built-in mic + wired headphones remains the safest and fastest path overall — worth surfacing as the recommended/default configuration in the UI (§3.1). Note that the "same device for input and output" restriction is Bluetooth-specific: it exists because some Bluetooth accessories combine an open-air mic and speaker in one enclosure (real feedback risk), whereas a wired headset's sealed, near-ear design makes the same-accessory combination the normal, low-risk case.

## 2. Core Functionality Requirements

### 2.1 Audio Input

- The user selects an input source: a paired Bluetooth microphone, the device's built-in microphone, or a wired microphone (inline mic on a wired headset). A wired mic is valid as long as the output isn't the phone's own speaker — which is structurally guaranteed, since the phone's speaker is never an output choice at all (§1.1).
- The user selects an output destination: a paired Bluetooth speaker, or wired headphones/earbuds. The phone's own built-in speaker is never offered as an output destination.
- The same Bluetooth device may not be selected as both input and output. This restriction does not apply to wired: using one wired headset as both mic input and headphone output is supported and is in fact the primary intended use case (§1.1).
- Regardless of which input the user selects, the app must explicitly set it via a preferred-device API rather than relying on OS default routing. This matters most when wired headphones with an inline mic are connected, since the OS defaults input to that wired mic — if the user instead picks the built-in mic or a Bluetooth mic while wired headphones are connected, the app must force that override explicitly (see §5.3, §11).
- If the built-in mic or a wired mic is selected as input, the app must run the Latency Capability Check (§4.2) the first time that input/output pairing is used, then cache the result (see §4.2.4) and simply display it on subsequent uses. Feedback Protection (§4.1) runs every session regardless, since it depends on live signal conditions, not a cacheable device property. The app should also show a one-time warning when the output is a Bluetooth speaker (built-in mic or wired mic + Bluetooth speaker both carry meaningful feedback risk) explaining the risk with guidance (e.g., "keep the phone away from the speaker, or lower speaker volume"). No warning is needed for a wired mic paired with wired headphones (§1.1).
- Audio capture must continue while the screen is off or the app is backgrounded, once explicitly started via the on/off switch, via a foreground service.
- A future release adds on-device recording of the processed output.

### 2.2 Audio Processing

- Real-time effects applied to the incoming voice stream. Minimum initial set:
  - Witch / distorted high-pitch voice
  - High-pitch cartoon-style voice
  - Low, growly monster voice
  - Robotic / synthetic voice
- Processing order (see §4.1 for full chain): input gain/AGC → voice effect DSP → output limiter → howling detector → output.
- Processing must occur within the latency budget established per-session by the Capability Check (§4.2), not a single fixed number.

### 2.3 Audio Output

- Processed audio is routed to the selected output destination: a Bluetooth speaker via the A2DP profile, or wired headphones/earbuds via the headphone jack or USB-C.
- The app must maintain simultaneous Bluetooth connections for the input device (if Bluetooth) and the output device (if Bluetooth).
- If the selected output disconnects (Bluetooth drop or wired unplug), the app must mute/pause output immediately rather than letting the OS fall back to the phone's built-in speaker (§7).
- Output must continue while the screen is off.

### 2.4 Background Operation

- Audio capture, processing, and output run in a foreground service.
- The app remains active when minimized or the screen is locked, once started by the user.

### 2.5 Device Discovery & Auto-Selection

The app cannot function without at least one valid input and one valid output selected. Rather than always requiring the user to pick both manually, it should default intelligently and recover from a missing device without forcing a relaunch wherever that's technically possible.

- **Auto-selection on launch:** whenever the device-selection screen is shown (including app launch), the app scans currently available candidates and auto-selects a sensible default, which the user can always override (§3.1, §3.2):
  - Output priority: wired headphones/earbuds (if currently connected) → the last-used Bluetooth speaker (if currently connected) → no valid output.
  - Input priority: the last-used Bluetooth mic (if currently connected) → the built-in microphone (available on virtually every device) → no valid input (only if `RECORD_AUDIO` permission is denied or the mic hardware is otherwise unavailable).
- **Live hot-plug detection:** while the app/foreground service is running, the app must listen for device connect/disconnect events live — Bluetooth connection-state broadcasts (A2DP/HFP) and `AudioDeviceCallback` for wired/USB — and react without the user needing to relaunch the app:
  - A newly available device (a Bluetooth speaker finishing its connection, or headphones being plugged in) surfaces as a non-blocking "New device available — switch?" prompt rather than auto-switching mid-session, since an unreviewed switch could itself cause an audio glitch or move to a combination that needs the feedback warning (§2.1).
  - A device disconnecting is handled per §7 (mute output, don't fall back to the phone speaker) and does not require a relaunch to recover from — the user can pick a newly available device from the still-running app.
- **Known limitation:** live detection only works while the app's process/service is actually alive. If the app has been fully closed (not just backgrounded) or the OS killed its process before Start was ever pressed, a device paired or connected in that window won't be picked up until the app is reopened and re-scans. This is a consequence of how Android exposes device state to a non-running process, not a defect to design around (also noted in §11).
- **No valid device available:** if, at launch or at any point Start is attempted, there is no valid output (no wired headphones connected and no Bluetooth speaker connected) and/or no valid input (permission denied or mic hardware unavailable), the app must block Start entirely and show a clear prompt naming what's missing and how to fix it (e.g., "Connect wired headphones or pair a Bluetooth speaker to continue"), with a manual "Rescan" action — never allow Start with an unresolved missing input or output.

## 3. User Interface Requirements

### 3.1 Main Screen

- Input source selector: Bluetooth mic (list of paired devices), "Phone microphone," or "Wired microphone" (when a wired headset with an inline mic is connected). Auto-selected on open per §2.5, with a visible indicator that it was auto-selected.
- Output device selector: list of paired Bluetooth speakers, plus "Wired headphones" when connected. The phone's built-in speaker is never listed. Wired headphones + phone microphone should be visually flagged as the recommended/lowest-latency default when available. Auto-selected on open per §2.5.
- Voice effect selector (dropdown or tile grid).
- Status indicators: input connected, output connected, processing active.
- Estimated latency badge (from §4.2), shown before Start and updated if the device pairing changes.
- Output gain / volume limiter control (manual cap, see §4.1).
- Start/Stop toggle.
- Feedback-protection indicator: shows normal / "auto-reduced" state if the howling detector intervenes.
- No-valid-device state: if there's no valid input or output (§2.5), Start is disabled and a blocking prompt explains what's missing, with a "Rescan" action.

### 3.2 Settings Screen

- Effect intensity sliders.
- Latency/quality tradeoff setting (maps to buffer size, informed by §4.2).
- Bluetooth device selection and re-scan.
- Feedback protection sensitivity (default: on, medium sensitivity).
- Manual output gain ceiling.

### 3.3 Background Mode

- After pressing Start, the user may turn off the screen.
- Persistent notification shows: current effect, service running state, estimated latency, and a Stop button.
- If the howling detector auto-reduces gain while backgrounded, the notification should reflect the reduced/protected state.

## 4. Audio Engine Requirements

### 4.1 Feedback & Howling Protection (required, not optional)

Blocking the same Bluetooth device for input and output (retained from v1) only prevents a direct electrical loopback. It does not prevent acoustic feedback — the classic "Larsen effect" where sound from the output speaker re-enters the microphone, gets re-amplified, and builds into a runaway howl. This can happen with any mic/speaker combination that is acoustically coupled (in the same room), which is now explicitly in scope because the built-in mic is supported. Required mitigations, in order along the signal chain:

- **Device-pairing restriction** (existing, Bluetooth only): disallow selecting the same Bluetooth device for input and output. This does not extend to wired: a single wired headset used as both mic input and headphone output is supported and low-risk (§1.1).
- **Manual gain limiter** (new): a user-adjustable output gain ceiling, plus a hard limiter/compressor on the final output stage so output level cannot exceed a safe maximum regardless of effect processing or user settings.
- **Real-time howling detection** (new): continuously analyze the output signal (or a tap of the mic input) for the frequency-domain signature of feedback — sustained, rapidly growing narrow-band energy at a stable frequency across consecutive analysis windows — distinct from normal broadband voice energy.
- **Automatic response** (new): when howling is detected, immediately duck output gain (e.g., by a fixed dB amount or to a safe floor), show the protection indicator, and ramp gain back up gradually once the condition clears. Do not fully mute without user action, so the app remains usable.
- **Warning + guidance** (new): when the built-in mic or wired mic is paired with a Bluetooth speaker, show a one-time (dismissible, re-showable from Settings) warning explaining the risk and suggesting mitigations (lower volume, increase distance). Not needed for wired mic + wired headphones (§1.1).

Implementation note for the implementing agent: a full acoustic echo canceller (AEC) that references the actual output signal (like Android's `AcousticEchoCanceler` effect, or WebRTC's AEC3) is the more robust long-term solution, since it can cancel the coupled signal rather than just detecting and ducking after the fact. AEC referencing is more involved to wire up correctly with a modulated (pitch-shifted) output signal, since a standard AEC expects the reference and the echo to be spectrally related — which breaks once the voice effect has changed the pitch/formants of what comes out of the speaker. This is flagged as an open risk in §11 rather than assumed solved.

### 4.2 Latency Capability Check & Calibration

Rather than assuming a fixed latency target, the app determines and displays the actual achievable latency for the current device pairing before the user starts processing. This directly answers "can we hit <100ms?" on a per-device, per-session basis instead of guessing.

**4.2.1 Static capability probe (fast, run on device/pairing selection)**

- Query device audio properties: `PROPERTY_OUTPUT_SAMPLE_RATE` and `PROPERTY_OUTPUT_FRAMES_PER_BUFFER` (`AudioManager`), and whether the device declares `FEATURE_AUDIO_LOW_LATENCY` / `FEATURE_AUDIO_PRO` (`PackageManager`).
- Query the active Bluetooth output codec where the platform exposes it (SBC / AAC / aptX / aptX-LL / LDAC / LC3 via LE Audio) — note this is inconsistently exposed across Android versions/OEMs and may require reflection or a best-effort fallback (flagged in §11).
- Query whether the input path is HFP/SCO (typical for Bluetooth mic — narrowband, mono, higher latency) versus the AAudio/Oboe low-latency path (typical for the built-in mic).

**4.2.2 Empirical loopback measurement (run once per pairing, a few seconds)**

- Emit a short calibration chirp/tone through the selected output path.
- Capture via the selected input path and cross-correlate against the known emitted signal to estimate round-trip latency.
- This is reliable when input and output are acoustically coupled (e.g., built-in mic + external speaker in the same room). For a Bluetooth mic that is not acoustically coupled to the output, fall back to the static probe estimate and label it as "estimated" rather than "measured."

**4.2.3 Result**

- Display an estimated/measured latency badge (e.g., "~180ms measured" or "~150–250ms estimated") before Start, and let the user proceed regardless — this is informational, not a hard gate.
- Use the measurement to auto-select buffer size (smaller buffer on low-latency-capable paths; larger, safer buffer otherwise), exposed as the Settings §3.2 latency/quality tradeoff.

**4.2.4 Caching (run once per pairing, not every session)**

- Both the static probe and the empirical measurement are properties of a given input/output device pairing (e.g., "phone built-in mic + [speaker name/MAC]"), not of a single session — they do not need to be re-run every time that pairing is selected.
- Cache the result locally, keyed by the input source and the output device identifier (Bluetooth MAC address or equivalent stable ID).
- On a cache hit, skip both the static probe and the loopback measurement, and simply display the stored latency badge immediately.
- Re-run the check only when: the pairing is used for the first time; the previously paired output device is unavailable/replaced by a different device; the OS or app is updated in a way that could change the low-latency audio path; or the user explicitly requests "Recalibrate" from Settings (§3.2).
- Wired output via the headphone jack is a special case: its latency is set by the phone's own DAC/ADC, not by the headphone brand, so it can be cached once per phone model rather than per accessory — no need to recalibrate every time a different pair of wired headphones is plugged in. USB-C digital headphones/dongles are the exception: since the DAC lives in the accessory, cache those per-device like Bluetooth.
- The Feedback & Howling Protection module (§4.1) is not cached — gain limiting and howling detection must run live every session, since they react to real-time signal conditions (volume, distance, ambient noise) that a one-time measurement can't capture.

Realistic expectations to carry into implementation: standard Bluetooth audio (SBC-based A2DP output, HFP/SCO mic input) typically runs 150–300ms round trip; sub-100ms is realistic mainly with a wired/on-device mic path using AAudio's low-latency mode, or with Bluetooth LE Audio (LC3) / aptX Low Latency where both the phone and accessory support it. Wired headphone output adds only a few milliseconds of analog conversion latency, so the built-in mic + wired headphones path is the one most likely to land under 100ms end-to-end. <100ms should still be documented as a best-case, hardware-dependent outcome shown to the user, not a universal requirement the app must guarantee.

## 5. Technical Requirements

### 5.1 Platform

- Android (minimum API 29 / Android 10 recommended); other MAUI platforms stubbed out for now.
- Built using .NET MAUI, with a custom Android platform-specific audio module (see §5.3 — MAUI does not expose low-latency audio APIs directly).

### 5.2 Permissions

| Permission | Notes |
|---|---|
| `RECORD_AUDIO` | Runtime permission on all supported versions. |
| `BLUETOOTH_CONNECT`, `BLUETOOTH_SCAN` | Runtime permissions required from Android 12 (API 31) onward; replace the legacy `BLUETOOTH`/`BLUETOOTH_ADMIN` permissions used pre-API 31 — handle both paths if minSdk stays at 29. |
| `POST_NOTIFICATIONS` | Runtime permission from Android 13 (API 33) onward; required to show the persistent foreground-service notification. |
| `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_MICROPHONE`, `FOREGROUND_SERVICE_CONNECTED_DEVICE` | From Android 14 (API 34), foreground services must declare a `foregroundServiceType` in the manifest (`microphone` and `connectedDevice` apply here) and request the matching permission. |

### 5.3 Audio Pipeline

- Plain `AudioRecord`/`AudioTrack` via managed MAUI bindings will not reliably hit low-latency targets (typical latency 100–300ms+ through the Java layer). Recommend a native Android module using AAudio directly (performance mode `PERFORMANCE_MODE_LOW_LATENCY`) or the Oboe C++ library, wrapped for MAUI via platform-specific bindings.
- DSP effects (pitch shifting, formant manipulation, distortion, filtering) should use an established algorithm/library (e.g., WSOLA or phase-vocoder based pitch shifting, similar to SoundTouch) rather than a from-scratch implementation, to keep quality and latency predictable.
- Signal chain order: input gain/AGC → effect DSP → output limiter → howling detector/ducking → output (see §4.1).
- The app must explicitly select the input device via a preferred-device API (e.g., `AudioRecord.setPreferredDevice` with the `AudioDeviceInfo` for whichever mic the user selected — built-in, Bluetooth, or wired) rather than relying on the OS default, since connecting wired headphones with an inline mic can otherwise cause Android to auto-route input to that wired mic even when the user wants a different source (see §2.1, §11).
- Detect wired accessory connect/disconnect via `AudioDeviceCallback` (device types `TYPE_WIRED_HEADSET` / `TYPE_WIRED_HEADPHONES` / `TYPE_USB_HEADSET`) or the `ACTION_HEADSET_PLUG` broadcast, so the UI and routing logic react immediately to plug/unplug events.

### 5.4 Bluetooth Profile Detail

- Output uses A2DP (higher quality, stereo-capable, but adds its own encode/transmit/decode latency).
- A Bluetooth microphone input typically requires HFP/SCO, which is narrowband (commonly 8–16kHz, mono) and adds its own latency — this constrains effect quality on that path more than on the built-in mic path.
- Where both ends support it, LE Audio (LC3 codec) or aptX Low Latency reduce this overhead meaningfully; treat as an enhancement path, not a v1 dependency.

### 5.5 Wired Audio Path Detail

- Wired output (headphone jack) is a direct analog path through the phone's own DAC — no codec negotiation, no encode/decode step, and only a few milliseconds of added latency, making it the fastest output option (§4.2).
- USB-C "wired" headphones are actually digital: the DAC lives in the accessory (or an inline dongle), so latency and quality vary by accessory, similar to Bluetooth, and should be cached per-device (§4.2.4) rather than assumed fixed.
- A wired headset's inline microphone is a valid input source (§1.1, §2.1), same as the built-in mic or a Bluetooth mic. It must never be paired with the phone's own speaker as output — which is structurally impossible anyway, since the phone's speaker is never offered as an output choice.

### 5.6 Performance Requirements

- Latency: reported per-session via the Capability Check (§4.2), not a single fixed number; <100ms is a best-case outcome on capable hardware paths.
- CPU/battery usage must remain low enough for sustained background operation; measure battery drain per hour of active processing as part of QA (§10).

## 6. Non-Functional Requirements

### 6.1 Reliability

- Graceful handling of Bluetooth disconnects, with auto-reconnect attempts.
- Graceful handling of a howling/feedback event without a full crash or freeze (§4.1).
- If the selected output device disconnects for any reason (Bluetooth drop or wired unplug), mute/pause output immediately — never allow the OS to silently fall back to the phone's built-in speaker, which is not a supported output destination (§1.1, §7).

### 6.2 Usability

- UI must be intuitive and require minimal steps to start.
- First-run and per-pairing risk warnings (§2.1, §4.1) must be clear but not block repeated use.

### 6.3 Security & Privacy

- No audio data stored; no network transmission of audio.
- Because `RECORD_AUDIO` is a sensitive permission, include an in-app disclosure of what the mic is used for (real-time processing only, nothing stored/sent) and complete the Play Store Data Safety declaration accordingly.

### 6.4 Extensibility

- Architecture must allow adding new voice effects, and new input/output path types (e.g., wired USB audio), without redesigning the core pipeline.

## 7. Edge Cases & Error Handling

| Scenario | Required behavior |
|---|---|
| Selected Bluetooth input or output disconnects mid-session | Mute/pause processing immediately, attempt auto-reconnect, show clear status in the persistent notification and main screen; never fall back to the phone's built-in speaker (see below). |
| Wired headphones are unplugged mid-session | Mute/pause output immediately rather than letting the OS route audio to the phone's built-in speaker; show a reconnect/re-select prompt. |
| Wired headset with an inline mic is connected while the built-in mic or a Bluetooth mic is selected as input (user wants a different source than the wired mic) | Force input routing back to the selected mic via the preferred-device API (§5.3). If the platform/device doesn't allow that override, fall back to using the wired mic as input instead (it's now a supported source, §1.1) and notify the user that input auto-switched, rather than blocking Start. |
| User denies `RECORD_AUDIO` or Bluetooth permissions | Block Start with an explanatory screen and a path to Settings; do not crash. |
| No paired Bluetooth devices found and no wired headphones connected (no valid output) | Block Start entirely; show a prompt naming the missing output and how to fix it, with a manual "Rescan" action (§2.5). |
| No valid input available (e.g., `RECORD_AUDIO` permission denied or mic hardware unavailable) | Block Start entirely; show a prompt naming the missing input and how to fix it (§2.5). |
| A new valid input or output becomes available while the app/service is running | Detect live via Bluetooth connection-state broadcasts / `AudioDeviceCallback`; surface a non-blocking "New device available — switch?" prompt rather than auto-switching mid-session (§2.5). |
| App was fully closed or killed before Start was ever pressed, and a device was paired/connected during that time | No live detection is possible while the process isn't running; the app re-scans on next open/resume — this is expected, not a bug (§2.5, §11). |
| Howling detected | Auto-duck gain, show protection indicator, ramp back up gradually once clear (§4.1). |
| Built-in mic or wired mic + Bluetooth speaker selected | Show the one-time risk warning (§2.1) before allowing Start. |
| Latency calibration fails or is inconclusive | Fall back to the static probe estimate, label it clearly as an estimate, and still allow Start. |

## 8. Deliverables

- Full MAUI project targeting Android.
- Foreground service implementation with the correct Android 14 foreground service types declared.
- Native low-latency audio module (AAudio/Oboe-based) with MAUI bindings.
- DSP engine with the four initial effects.
- Feedback/howling protection module (§4.1).
- Latency capability check module, including caching (§4.2, §4.2.4).
- Wired audio path handling: preferred-device input routing and plug/unplug detection (§5.3, §5.5).
- Device auto-selection and live hot-plug detection module (Bluetooth + wired), including the no-valid-device blocking state (§2.5).
- UI screens, persistent notification, and Bluetooth/wired input/output handling.

## 9. Acceptance Criteria

- User can select an input source (Bluetooth mic, phone mic, or wired mic) and an output destination (Bluetooth speaker or wired headphones), and cannot select the same Bluetooth device for both, or the phone's built-in speaker at all.
- User can select an effect and start/stop processing.
- Voice is modulated in real time and played through the selected output destination.
- App continues working with the screen off.
- No crashes during Bluetooth connect/disconnect/switching, or wired headphone plug/unplug.
- A latency estimate is shown before Start and reflects the selected device pairing; on a repeat pairing, it appears immediately from cache without re-running calibration.
- A simulated howling condition (e.g., in a controlled test rig) triggers auto gain reduction within a defined response time, without a hard mute or crash.
- Manual output gain ceiling is respected even under effect processing.
- Unplugging wired headphones mid-session mutes output rather than routing to the built-in speaker.
- On launch, the app auto-selects the best available input/output pairing when one exists, and the user can override it.
- If no valid input or output exists, Start is disabled and a clear prompt is shown, with a working "Rescan" action.
- Pairing or connecting a new valid device while the app/service is running is detected live, without requiring the app to be relaunched.

## 10. Testing / QA Notes for the Implementing Agent

- Build a device/pairing test matrix: at minimum, [Bluetooth mic + Bluetooth speaker], [phone mic + Bluetooth speaker, near], [phone mic + Bluetooth speaker, far], [phone mic + wired headphones], [Bluetooth mic + wired headphones], [wired mic + wired headphones, same accessory], [wired mic + Bluetooth speaker], across at least two Android OEMs/versions.
- Include a controlled feedback test (e.g., speaker facing mic at close range at moderate volume) to validate the howling detector's response time and false-positive rate against normal loud/sung voice input — run this for both the built-in mic and wired mic paired with a Bluetooth speaker.
- Validate the latency calibration's measured value against an independent reference (e.g., an external audio interface loopback) on at least one device.
- Validate that the latency badge is shown from cache (no recalibration delay) the second time the same pairing is used, and only recalibrates when the pairing actually changes.
- Test wired headset with inline mic connected while the built-in mic or Bluetooth mic is selected: confirm the app forces input back to the selected source rather than silently switching, and falls back gracefully to the wired mic (with notice) if that override isn't possible on a given device.
- Measure battery drain per hour of continuous background processing.

## 11. Open Risks / Questions for the Implementing Agent

- Bluetooth codec introspection (SBC/AAC/aptX/LDAC/LC3) is inconsistently exposed across Android versions and OEM skins; the static probe in §4.2.1 may need a best-effort/graceful-degradation approach rather than a guaranteed answer.
- A full acoustic echo canceller referencing a pitch-shifted output signal is non-trivial (see §4.1 implementation note); v1 relies on detection + ducking rather than cancellation, and this should be revisited if user feedback shows it's insufficient.
- MAUI's managed audio APIs are not sufficient for the latency targets here; budget time for a native Android (Kotlin/Java or C++ via Oboe) module with MAUI interop, rather than treating this as a pure C# task.
- Confirm whether the <100ms figure in early product discussions was a hard requirement from a stakeholder or an aspirational placeholder — this document treats it as the latter and reports real numbers to the user instead.
- Some OEM devices may not expose a way to keep capturing from the built-in mic (or Bluetooth mic) while a wired headset with an inline mic is plugged in (the OS may hard-route input to the wired mic at the hardware level with no override available). Since the wired mic is now a supported source, the fallback in that case is to switch to it and notify the user (§7), rather than blocking Start — but this still needs verifying across a few real OEM devices early, since it changes which input the user actually gets versus what they selected.
- The wired-mic + Bluetooth-speaker risk classification (§1.1, medium) assumes a typical inline mic position (on the cord or in an earbud housing, a few inches to a foot from the mouth). Actual risk varies by accessory — a boom mic close to the mouth behaves differently from a mic clipped near the chest — so the howling detector and gain limiter (§4.1) need to be tuned to cover this range rather than a single assumed mic position, and this should be validated with a few real wired accessories, not just one reference device.
- Live hot-plug detection (§2.5) only works while the app's process or foreground service is alive; there is no way to receive Bluetooth/wired connection events for a fully closed or OS-killed app. This is a platform constraint, not something to engineer around — confirm the product is fine with "re-scan on reopen" as the recovery path in that case, rather than expecting true background detection with zero app process.

## 12. Future Enhancements (Out of Scope for v1)

- Custom user-created effects.
- Recording and playback of processed audio.
- iOS support (limited by Bluetooth/background-audio restrictions).
- Multiple simultaneous output devices.
- Voice call (in-call) voice manipulation.
- Full acoustic echo cancellation referencing the modulated output signal.
- Headset button control (§12.1).

### 12.1 Headset Button Control (Future)

Let the headset's own hardware buttons (wired or Bluetooth) control the app without touching the phone screen:

| Gesture | Action |
|---|---|
| Single press/tap (main button) | Start/stop audio processing (toggle) |
| Double press/tap (main button) | Switch to the next voice modulator effect |
| Triple press/tap (main button) | Switch to the previous voice modulator effect |
| Volume up / down | Increase/decrease output gain, capped by the manual gain ceiling and limiter (§4.1) |

Implementation notes for whenever this is scoped in:

- Android's media button framework (`MediaSessionCompat`) already implements single/double/triple-press timing as a first-class concept: a wired headset's hook button fires `onPlay`/`onPause`, `onSkipToNext`, and `onSkipToPrevious` callbacks for single, double, and triple presses respectively, once the app holds an active media session — custom press-timing/debounce logic likely isn't needed. Map `onPlay`/`onPause` → Start/Stop, `onSkipToNext` → next modulator, `onSkipToPrevious` → previous modulator.
- Bluetooth headset buttons are typically surfaced through the AVRCP profile and land on the same `MediaSessionCompat` callbacks, so wired and Bluetooth remotes should be able to share one code path rather than needing separate handling.
- To have hardware volume buttons control the app's own output gain instead of the phone's system media volume, claim volume handling via a `VolumeProviderCompat` on the media session (`setPlaybackToRemote`) while processing is active, and release control back to system volume when processing stops. Volume changes from the headset must still respect the gain ceiling and limiter (§4.1) — the headset buttons should not be able to bypass the safety cap.
- Next/previous modulator switching should wrap around the effect list (past the last effect goes to the first, and vice versa) and apply live without requiring a restart of processing.
- This requires an active media session with playback state while running, which fits naturally with the existing foreground service (§2.4); no additional Android permission is needed.
- Open question to resolve when this is scoped: should button control also work while processing is stopped (e.g., to cycle through effects before pressing Start), or only once Start has been pressed?

## 13. Glossary

| Term | Meaning |
|---|---|
| A2DP | Advanced Audio Distribution Profile — Bluetooth profile used for streaming higher-quality stereo audio (used here for output to the speaker). |
| HFP / SCO | Hands-Free Profile / Synchronous Connection-Oriented link — Bluetooth profile typically used for mic input on headsets; narrowband and higher latency than A2DP. |
| AVRCP | Audio/Video Remote Control Profile — the Bluetooth profile that carries remote-control commands (play/pause, next, previous, volume) from a headset's buttons to the phone. |
| AAudio / Oboe | Android's low-latency native audio API (AAudio) and Google's C++ wrapper around it (Oboe), used to achieve low-latency audio outside the managed Java/C# layer. |
| aptX-LL / LE Audio (LC3) | Bluetooth codecs designed for lower audio latency than the default SBC codec, when supported by both the phone and the accessory. |
| AGC | Automatic Gain Control — automatically adjusts input level to a target range. |
| Howling / Larsen effect | Runaway audio feedback caused by sound from a speaker re-entering a microphone and being re-amplified in a loop. |
| AEC | Acoustic Echo Cancellation — a technique that removes a known output signal's acoustic echo from the microphone input. |

---

This document is ready to hand off to the implementation agent, with open risks explicitly called out in §11 rather than assumed away.
