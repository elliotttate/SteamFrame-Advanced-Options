# SteamFrame Advanced Options

This fork adds auto tune sender support for **SteamVR 2.18.4, build 25998080**,
alongside the upstream 25839986 layout. See [fork compatibility and verification](docs/fork-compatibility.md).
Unknown future drivers still require verification; the version number alone never enables private memory reads.
**Hardware validation is pending:** the build and automated tests pass, but a full auto tune run on the new driver has not yet been confirmed.

A Windows app for tuning and diagnosing **Steam Frame** streaming from a PC.
It sets the stream's bitrate and foveation, measures what the encoder actually
produces, records streaming faults on both the PC and the headset, and can raise
the SteamVR driver's bitrate ceiling with a fully reversible patch.

The experimental codec selector can also start **AV1 or H.264** on a supported
Steam Frame, applying the PC and headset changes as one supervised session.
The dashboard verifies the codec from fresh encoder output.

It also includes a full editor for every Steam Link VR (`driver_vrlink`) setting,
including the ones SteamVR doesn't show in its own UI.

![Live dashboard measuring a Steam Frame stream: encoded bitrate, requested target, video path and a 60-second graph](docs/images/live-dashboard.png)

> **Platform:** Windows 11 x64 · SteamVR with the Steam Link VR driver · NVIDIA GPU
> for encoder measurements and foveation bias (AMD AMF can be enabled, but isn't measured)

## Features

| Area | What it does |
| --- | --- |
| **Stream controls** | Set the video bitrate target, automatic bandwidth, AMD AMF and USB link joining. Changes apply live to running SteamVR, or are saved for its next start. |
| **SteamVR filtering** | Toggle Advanced Supersample Filtering with accepted live readback or a saved setting for the next SteamVR session. |
| **Foveation bias** | Shift quality between where you look and the periphery on top of Valve's own gaze-tracked map. |
| **Advanced controls** | Tune CQ, maximum QP, peak bitrate, VBV, chroma, AQ and presets, plus resolution, buffering and sending settings, with accepted/pending/rejected readbacks and restoration. |
| **Live dashboard** | Graphs actual encoded video Mbps and FPS, the requested target, and adapter traffic over the last 60 seconds. |
| **Wireless link** | Reads SteamVR's own link estimate, capacity histogram, held frames and bitrate step-downs from the driver log, plus the dongle's radio band, channel, PHY rate and signal from Windows, with no pairing. An advisor turns them into one recommended target. |
| **Capacity sweep** | Raises the manual target step by step while you play, scores each step on held frames, stalls, loss and recovery, re-checks marginal steps and confirms the winner with a second pass, then reports the highest clean rate, saves it as a preset and restores your target. **Apply N Mbps now** puts the measured ceiling on the stream in one click. Auto tune remains the full preset comparison. |
| **Auto tune** | Explores stream widths, 90/120 Hz, single/quarter/full multipass, independent gaze/periphery offsets (including 0 / 0), measured combinations and adaptive bitrate in an unattended headset scene. Ranks detail and smoothness instead of Mbps alone, then returns explicit presets with measured tradeoffs. Short screens, configurable serious-issue limits and exact headroom confirmations keep the search bounded. See [Auto tune](docs/auto-tune.md). |
| **Codec selector** | Switch between HEVC, AV1 and H.264, choose H.264 25–500 Mbps or AV1 25–200 Mbps at 90/120 Hz, and restore stock HEVC. AV1/H.264 are exact-build experiments and restart the stream. |
| **Streaming issue capture** | Timestamps recovery requests, bad-link events, encoder stalls, PC presentation drops and headset receiver faults, and saves them as JSONL. |
| **Driver patcher** | Raises the driver's 232/350 Mbps ceilings to 500 Mbps and selects HEVC level 6.2, with verified backups and one-click restore. |
| **Guided first run** | Checks the actual driver and walks through installing the bitrate override. Explains an existing patch instead of implying that opening the app installed it. Reopen under Patches & Recovery. |
| **Verified refresh rate** | HEVC Hz changes set the Frame display through paired SSH and verify the active display rate on the headset and PC. Reconnects SteamVR if needed. |
| **Connection tools** | Shows the dongle's negotiated USB 2/USB 3 speed and headset link status. Reconnect Frame restarts SteamVR once and verifies fresh HEVC output; separate recovery handles the Frame USB daemon. |
| **Additional settings** | Search less common `driver_vrlink` options. Settings already in Stream settings or Advanced are excluded to avoid duplicate controls. |
| **Complete presets** | Includes Balanced wireless for a stock driver, Tuned HEVC (400 Mbps / 1664 width), Smooth HEVC (300 Mbps, single pass), High quality HEVC (400 Mbps, quarter-resolution multipass), Lower-latency HEVC (200 Mbps, single pass, HQ's 2048 width) and Full-resolution HEVC — Experimental. All use 90 Hz and 10-bit. Edit any control or save a personal preset; built-ins remain available. |
| **Back to stock** | Save the current tuning, end experimental codecs, restore a verified original driver and remove app tuning overrides. Load previous tuning to return to it. Pairing and unrelated settings stay intact. |

## Download and run

1. Download the Windows x64 ZIP from the [latest release](../../releases/latest).
   New builds use `SteamFrame-Advanced-Options-win-x64.zip`; older releases use
   `SteamLinkVRSettings-SteamFrame-win-x64.zip`.
2. Extract the **whole** folder anywhere. The ZIP bundles the .NET and Windows App SDK
   runtimes, so nothing else needs to be installed.
3. Run `SteamLinkVRSettings.exe`.

The app runs as your normal user account. If an action is denied access, such as
patching a driver in a protected folder, the status bar offers **Restart as
administrator**. Opening the app never launches SteamVR, modifies a driver, or
starts encoder monitoring on its own.

After updating while SteamVR is running, an older encoder helper may still be
loaded. The app shows **Restart SteamVR and resume**; selecting it (or starting
monitoring) restarts SteamVR once and resumes with the updated helper. This
briefly disconnects the headset and may close the running game. Saved encoder
tuning is restored when **Reapply** is enabled. Background checks never trigger
this restart, and unapplied edits stay in the editor.

If streaming disconnects after a game, use **Overview → Reconnect Frame** with
the headset awake. If it still does not return, unplug and replug the dedicated
dongle (the same USB port or hub is fine), wait for the link, and select this PC
in the Frame's streaming menu if needed. See the [reconnection guide](docs/live-dashboard.md#reconnecting-after-a-game-closes).

## Quick start

1. Keep Wi-Fi on the Frame enabled and start a VR stream.
2. Open **Stream settings**, choose a codec and bitrate target, and click **Apply stream settings** or **Apply and reconnect**.
3. On **Overview**, click **Start monitoring** to see the real encoded bitrate. **Diagnostics** contains detailed timing and fault reports.
   The **Wireless link** card shows SteamVR's link estimate, held frames and capacity histogram
   within 30 seconds of streaming, the dongle's radio signal, and a recommended target.
   **Find my link's limit** measures the highest clean bitrate while you play (a headset on the
   desk is kept awake, restarted for you and shown a moving test scene) and, by default, applies
   it when it finishes;
   **Apply N Mbps now** puts a saved result back on the stream later.
4. Optionally enable **Custom foveation bias**. **Quality focus** (gaze −4, periphery +8)
   is a good starting point.
5. To request more than 232 Mbps on a stock driver, close SteamVR and use
   **Patches & Recovery → Install 500 Mbps override** first.

### More bitrate than Valve's default, in three clicks

Valve's automatic mode starts every stream at 200 Mbps and feels its way from there. To find
what your link really carries and use it:

1. Put the Frame on the desk with SteamVR running and click **Find my link's limit** on
   Overview. The app wakes the headset, restarts the stream if it has idled, shows a moving test
   scene so the encoder works at the full rate, raises the target in 50 Mbps steps, re-checks
   any marginal or failed step, confirms the winner with a second pass, and applies the highest
   clean target when it finishes (about 5 to 8 minutes).
2. Play. The Wireless link card shows held frames, headset loss and SteamVR's own link estimate
   at that rate; **Apply N Mbps now** brings a saved result back any time, and **Check this
   rate (30 s)** tells you whether today's link still carries it before a long session.
3. Sweep again on another day. Once the week's results disagree by a step or more, the card
   says so and offers **Automatic from N Mbps**: automatic bitrate with SteamVR starting at the
   highest ceiling you measured instead of 200, backing off only when the link dips.

A sweep is a measurement of that link at that hour, not a promise; the card keeps the last
20 results so you can see how steady your setup is.

Use **Save preset…** in Stream settings to keep one complete tuning setup. Selecting
a preset fills the editor without modifying the stream; Apply activates it. Presets
also include saved Additional streaming settings; tracking, OSC, NAT, power and volume
preferences are left as they are. Save those edits before capturing the preset.
Passwords, developer pairing paths and temporary awake-test leases are not saved in
presets. **Back to stock** saves the currently applied tuning before restoring Valve
defaults; **Load previous tuning** fills the editor with that saved setup.

Choose **Tuned HEVC (built-in)** for our current playable setup: a 400 Mbps target,
1664-pixel width, quarter-resolution multipass, synchronous sending and center
−20 / outside-gaze −12 quality offsets. A short current-scene check measured about
261 Mbps at 89.7 encoded fps; sustained 350–400 Mbps and better results in every
game are not established. The lower width trades spatial detail for stronger
compression quality. Every setting remains editable.

Choose **Lower-latency HEVC (built-in)** to retain HQ's 2048-pixel width, 10-bit
and gaze offsets with a 200 Mbps target, single pass and synchronous sending.
Repeated tests measured 25.1 ms versus HQ's 29.6 ms from PC submit to headset
decode completion. This is not motion-to-photon; equal image quality is
unverified. See the [latency comparison](docs/hevc-high-quality.md#latency-comparison--october-15-2026)
for controls, limitations and the larger cost of stream geometry. Auto tune's
smoothness score measures delivery hitches, not end-to-end latency.

Choose **Smooth HEVC (built-in)** for the 300 Mbps single-pass baseline, or
**High quality HEVC (built-in)** to load the tested high-quality setup.
Its 400 Mbps target is a request, not guaranteed throughput: one Galactic Racer
observation averaged about 275 Mbps of coded video. Review individual options in
Stream settings and Advanced before applying. See the
[high-quality HEVC guide](docs/hevc-high-quality.md) for every control and the
measurement limits. **Full-resolution HEVC — Experimental** changes only the
analysis-pass mode from High quality HEVC; improved quality or bitrate has not
been verified, and the extra work can worsen frame timing.

For AV1 or H.264, follow the [codec setup guide](docs/codec-modes.md) first.
The release includes the companion runtime and AV1 decoder, so supported devices
can be patched through the app without building the source or installing Python
on the PC. Developer Mode, trusted SteamOS Devkit SSH pairing and the Frame user
password are required. The companion generates the client patch from the user's
own installed file. It refuses unknown headset builds.

## Documentation

| Guide | Covers |
| --- | --- |
| [Stream controls and foveation](docs/stream-controls.md) | Bitrate, stream width, VBR/CBR, encoding passes, additional links, gaze offsets and temporary test power settings |
| [High-quality HEVC preset](docs/hevc-high-quality.md) | Built-in profile, individual tuning controls, USB speed and observed performance |
| [Advanced controls](docs/advanced-controls.md) | CQ, max QP, peak/VBV, chroma/AQ/presets, driver width/pooling/sharpening/sending experiments, readbacks and restoration |
| [Codec modes](docs/codec-modes.md) | AV1/H.264 setup, supported builds, hardware requirements, switching, verification and rollback |
| [Live dashboard and diagnostics](docs/live-dashboard.md) | Every readout, capture files, recovery traces, PC presentation counters |
| [Streaming system map](docs/research/README.md) | Traced host, receiver, USB/radio and firmware paths, timing meanings, override boundaries and unresolved branches |
| [Driver patcher](docs/driver-patcher.md) | What the patch changes, compatibility checks, backups and restore |
| [Connection setup](docs/connection-setup.md) | Wireless SSH pairing, optional ADB, USB recovery |
| [All streaming settings](docs/settings-editor.md) | The full settings editor, resets, backups, experimental options |
| [Building from source](docs/building.md) | Toolchain, build and package scripts, tests |
| [Encoder probe internals](Native/README.md) · [Compatibility evidence](Native/VERIFICATION.md) | How the NVENC helper works and how it was verified |

## Safety and privacy

- **Reversible by design.** Config saves are atomic and keep both the previous revision and
  your original file. Driver patches keep a hash-verified original and a receipt.
- **Measurement only.** The encoder monitor counts compressed frame sizes and never reads,
  stores or copies video. Log capture stores synthesized events, not raw log lines.
- **No surprises.** Monitoring and patching only happen when you click them. SSH uses strict
  host checking with your own trusted host file and never auto-trusts a device.
- **Nothing personal is shipped.** SSH keys, passwords, personal settings, logs and
  Valve binaries are never packaged. The codec companion uses the dedicated dongle's
  fixed device address. It includes patch recipes and the experimental open-source
  decoder module; the original Valve client and firmware come from the headset.

## Known limitations

- The bitrate target is a request, not a minimum. Simple scenes can encode below it,
  and no filler is added.
- 500 Mbps is a software ceiling. One measured USB 2 connection carried about
  318–325 Mbps of UDP payload; real headroom depends on the link and scene.
- Encoder measurement and foveation bias support NVENC only.
- Experimental NVENC rate-control and pass controls use guarded public API 11.1
  initialize/reconfigure calls. The dashboard separates the requested policy from
  the last accepted encoder configuration and reports whole-frame QP separately
  from gaze-map deltas. CBR without padding is a target, not a useful minimum.
- Quarter-resolution multipass may use more of the target for useful detail but
  can add work or stutter. Valve's default encoding policy remains the app default;
  reconnect-only presets are available in Advanced controls with Valve's stream
  policy preserved. No preset or AQ experiment is enabled by default.
- A future SteamVR driver with a rewritten encoder can disable the patcher or foveation
  until the app is updated. Both refuse layouts they can't verify rather than guessing.
- Experimental AV1/H.264 sessions require Frame kernel `6.18.0-ge66bc2ca6f8c`
  and the specific stock VRLink client documented in the codec guide. They do not
  add general codec negotiation or support unknown future Frame builds.
- AV1 has been tested on an RTX 5090 and the Frame's Qualcomm hardware decoder.
  A 100 Mbps request at 90 Hz is the initial viewing setting; 200 Mbps failed the
  earlier fresh-frame rate test. H.264 currently uses a smaller packed image; 225 Mbps
  was its highest clean short trial, and requests above 232 Mbps need the bitrate patch.
  SteamVR can lower a codec session's bitrate after link problems.
  Available controls are experiments, not a guarantee of higher quality or full FPS.

## Project layout

```
SteamLinkVRSettings/   WinUI 3 app (views, view models, services)
  Frame/               Steam Frame runtime, connection, patcher, probe and diagnostics services
Native/                NVENC encoder probe (C++), its tests and vendored MinHook/NVIDIA headers
Frame/AV1/             Supervised codec companion, Frame decoder port and test/build tools
Tests/                 xUnit test projects
docs/                  User and developer guides
```

Third-party licenses for MinHook/HDE and the NVIDIA Video Codec SDK header live under
`Native/vendor` and are copied into the release package. The codec package also
includes CPython's license and the GPL-2.0-only decoder's corresponding source.
