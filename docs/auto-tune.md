# Wireless auto tune

Auto tune explores concrete HEVC presets for the user's hardware and wireless link. It returns a list of choices, rather than requiring a gain over Valve defaults or a particular bitrate. Typical choices emphasize balanced quality, higher detail or higher refresh. Original settings are restored after testing. Loading and applying a recommendation is an explicit user action.

## Run and choose

Connect the Frame to PC streaming with HEVC on an NVIDIA GPU, pair SSH under Connection, and close the VR game. SteamVR Home can remain open. The moving test room runs with the headset off your head and temporarily keeps it awake. Leave it in the normal play area with its cameras unobstructed. Avoid other GPU-heavy work during tests.

In this fork, resolution/refresh-rate comparison requires the **500 Mbps / HEVC 6.2** patch under **Patches & Recovery**, followed by a SteamVR restart. This patch changes the HEVC level as well as the bitrate cap. Without it, disable both resolution and refresh-rate comparisons to test only the current display mode. The tuner checks this before changing any settings.

The default search compares 1280, 1664 and 2048-pixel stream widths, also including the starting width if different, at both 90 and 120 Hz. These widths describe the packed encoder stream, not per-eye panel resolution. Single pass, quarter-resolution multipass and full-resolution multipass are compared. Disabling a width or Hz comparison retains the actual starting value. Full multipass can be omitted to shorten the search.

Each recommendation shows its explicit stream width, refresh rate, bitrate target, encoder pass, quality offsets, measured video Mbps, delivered frame rate, estimated affected frames and confidence. Each is saved separately in Stream settings and has a Load button in the results. Choosing a preset does not immediately apply it.

If testing is interrupted, completed evidence still produces choices marked as screened or confirmed. Longer confirmations supersede screens for the same width/Hz, while useful screens for other modes remain eligible with a lower-confidence label. A poor confirmed mode cannot hide those alternatives. If there is no valid measurement, the app offers an **unmeasured starting option** with explicit 1280-pixel width, 90 Hz, 100 Mbps, single pass and modest quality offsets. Disabled mode comparisons retain the starting mode when it was measured. This option is not claimed to be stable or measured. A recovery failure disables loading until the original settings are recovered.

## Faster search

Default screening windows are 10 seconds after a short warmup. The mode/pass matrix is screened once; only three promising, distinct modes receive further work. With region comparisons enabled, mode screens use neutral **0 / 0** offsets. Each shortlisted mode then receives an independent gaze/periphery search and at most two adaptive bitrate probes. Serious problems stop upward bitrate escalation and can trigger a lower-load trial. The exact headroom-adjusted settings are confirmed in a fresh encoder session for 35 seconds by default. If confirmation has serious problems or invalid measurements, one other previously measured pass/quality combination for that mode can receive a fresh confirmation. This retry is bounded; every observation remains in the report. Allow roughly 15–25 minutes with all comparisons enabled; mode restarts or a custom starting resolution can extend this.

### Independent gaze and periphery comparisons

For each shortlisted mode, auto tune measures:

- A fresh **gaze 0, periphery 0** baseline.
- Gaze **+6, −6, −12, −20**, holding periphery at **0**.
- Periphery **+8, −4, −8, −12**, holding gaze at **0**.
- Any additional enabled starting offsets on their respective axes, and the starting pair itself.
- Up to three additional combinations drawn from the best independently measured values below the serious-issue threshold. Each combination must be measured; two individually good offsets do not prove that their combined load works.

Duplicate pairs are skipped. At most 13 quality trials are reserved per mode, or 15 when both starting offsets add values to the axes. This is a bounded search, not every integer pair across every mode. All measured pairs remain in the report, including poor ones. Positive offsets reduce requested regional detail; negative offsets increase it. **0 / 0 preserves Valve's existing foveation**, rather than disabling it or enlarging the gaze region. Turning off the region comparison keeps the exact starting map throughout the search. The stored `AllowQualityReduction` option name is retained for report compatibility, but now controls this wider comparison.

Screening duration is adjustable from 8–60 seconds, confirmation from 20–300 seconds, and bitrate headroom from 5–25%. The default ceiling is 450 Mbps, also limited by the installed driver. It is a search bound, never a required result. The test does not install a bitrate patch or change firmware. Sender mode, codec, bit depth, rate-control policy and other advanced overrides remain at their starting values.

Short screening can miss rare hitches. Longer confirmation improves confidence, and a synthetic scene cannot guarantee performance in every game. The goal is a useful list of tradeoffs within a bounded test, not an exhaustive global optimum.

## Follow progress

The progress panel shows the current mode test (for example, **3 / 18**), an estimated overall percentage, completed tests, elapsed time and the measured seconds in the current window. Preparation, mode comparison, refinement and restoration have separate stage indicators. Reconnect and warmup time do not count as measured seconds. The adaptive search may skip optional trials, so the total is labeled **up to** and the percentage is an estimate, not a time prediction. A completed run reaches 100% after settings recovery; an interrupted run retains its partial percentage and interruption reason.

## Ranking and serious issues

**Smoothness is not end-to-end latency.** These tests score delivery hitches and
cadence; they do not capture PC-submit-to-headset-decode delay or motion-to-photon.
The [separate latency comparison](hevc-high-quality.md#latency-comparison--october-15-2026)
found larger streams could maintain steady FPS while arriving substantially later.
A high score is not a lowest-latency claim. The optional Lower-latency HEVC preset
keeps HQ's geometry at a lower bitrate with single-pass encoding; its visual
equivalence remains unverified. Ranking weights are not fitted to one machine's
latency measurements.

Valid performance observations remain in the report, including poor ones. Ranking first prefers settings below the configured serious-issue threshold, then balances smoothness, stream width, refresh rate and requested regional detail. **Neither actual Mbps nor the requested bitrate earns quality points.** The regional preference weights gaze 60% and periphery 40%; neutral scores 50, with stronger negative offsets increasing the preference. Balanced weighting is 55% smoothness, 25% resolution, 5% Hz and 15% regional detail. Detail and refresh alternatives change these preferences. These weights describe the search's intent, not measured optical or perceptual image quality.

A higher bitrate probe must show either materially better smoothness or at least a one-point reduction in mean whole-frame QP without a significant smoothness penalty, at the **same width, Hz, pass and regional offsets**. Missing QP does not count as evidence of better compression. QP is not compared between different maps to declare one visually superior: region offsets redistribute quality and whole-frame metadata cannot reveal each region's absolute quality. The runner obtains QP counter deltas from full status reads immediately before and after timed sampling; passive samples do not supply QP. Reports label it as bracketed whole-frame compression metadata, with its frame count and duration. Counter resets, changed configuration, insufficient coverage or encoder errors suppress this optional diagnostic. The exact headroom-adjusted choice still needs its final measured confirmation.

Among modes below the serious-issue threshold, the detail alternative prioritizes stream width and the refresh alternative prioritizes Hz before their weighted scores. This keeps the list useful when the balanced score favors a smaller stream. A larger or faster mode with serious problems still ranks behind modes below the threshold.

The serious-issue thresholds are configurable: by default, over 5% estimated affected frames or at least 500 ms of frame-equivalent hitch load in one sampling window. Sustained delivery below 90% of refresh for at least three seconds, a no-delivery window of at least one second, or mean encoder/receiver cadence below 85% of refresh also flag serious problems. So does a trial during which SteamVR itself lowered the stream below the candidate's requested rate (a bad-link step-down in the driver log): the link did not carry that rate, whatever the presentation counters say. Each trial also records the sender's held frames (driver throttle events); ten or more per minute are noted as link saturation without changing the verdict. Choices below the threshold are preferred. If every measured choice has problems, the least problematic measured choices remain available with prominent warnings; no claim of stability is made.

The affected-frame estimate takes the largest overlapping PC, receiver or cadence observation in each roughly one-second window. It does not add duplicate pipeline losses and is not a unique optical dropped-frame count. Burst windows, sustained delivery dips and freezes cost more than isolated hitches. Raw counters remain in the report. The legacy `Passed` and `MajorIssues` fields are diagnostic warnings, not qualification gates; the configured serious-issue limits govern exploration and ordering.

Missing/stale counters, changed stream identities or wireless routes, inactive requested settings and encoder errors cannot support a measured recommendation. Such evidence is retained as an invalid observation rather than fabricated into a successful trial. Normal reprojection and packet-repair log entries are not converted into optical frame losses.

Low encoded output by itself does not end a trial early. The tuner measures the requested window before deciding whether it contained enough picture data; a brief low-output interval may recover. A completed window with insufficient workload still cannot support a measured recommendation. Invalid counters or changed settings continue to stop that window immediately.

## Display changes and recovery

For a new Hz, the host preference is written first, the Frame's `vrlink.client` per-app display preference is changed through paired SSH, and both the receiver and PC active rates are checked. That per-app preference takes precedence over the Frame's global SteamVR setting. SteamVR is restarted when needed. Width changes are verified from accepted encoder settings. Each mode gets fresh scene/receiver observers and a new warmup; comparison counters never span a deliberate mode switch.

Bitrate and quality changes use the running stream. Width and Hz changes also avoid a SteamVR restart when the encoder accepts the width and the PC reports the requested active rate. The tuner waits briefly for these live updates before falling back to a restart. A fresh test scene is still created for each display mode. The progress panel identifies why a restart is needed, including encoder-pass changes and final confirmations.

After a mode switch, headset counter startup allows 20 seconds for the wireless route and streaming scene to settle, with a 30-second host-side bound. Three consistent startup observations are required. If a transient missing route or receiver persists, the app resumes the same captured PC once and retries. Ambiguous or wired routes remain explicit errors. Once measurement starts, changed identities, missing telemetry or route changes still invalidate that window; reconnection never joins unrelated counters into a seemingly clean result.

Before changing a candidate and before opening its measurement window, the tuner verifies advancing encoded frames and bytes. If output has stopped, it can recover the owned runtime up to twice during the whole run. A reconnect stops the headset's stale streaming client, rechecks the captured PC, and starts that same host again. Fresh output is required afterward. Completed observations remain in the report; persistent failures stop with a bounded recovery error. A cleanup failure is appended to the original cause instead of hiding it.

App-requested multipass transitions use a fresh encoder session. Live testing found a GPU fault after switching to full multipass inside an existing session despite an accepted API return. The helper now retains the accepted pass until initialization, and the search groups modes by pass to limit restarts. Original multipass restoration follows the same rule.

Before writing settings, the app journals original overrides and absent keys. A separate display journal preserves original active Hz and the PC/Frame refresh preferences. Cancellation, normal completion and recovery restore those values, preserving newer external edits. A failed restore keeps its journal for retry. After an interruption, reconnect the Frame and use **Restore starting settings**. If a dedicated connection is stuck, reconnecting the dongle can help even in the same USB 3 port or hub.

Reports are local under `%LOCALAPPDATA%/SteamLinkVRSettings/Benchmarks`. Version 4 reports include independent region stages and optional bracketed QP metadata, alongside each candidate's mode and saved recommendation profiles. Version 3 reports used an earlier, narrower quality search and bitrate-based quality preference; old reports retain their original observations and semantics. No credentials or reports are uploaded.

## Streaming experience audit — October 15, 2026

The updated Windows app completed the full default comparison on the connected Frame over its dedicated wireless dongle: **64 valid trials in 25:05**, all 18 mode/pass screens, four longer confirmations (including one alternative), three saved choices, 100% completion and no pending recovery. The report is `benchmark-20261015-183828-7aa1367a.json`. Two final choices retained the **Screened only; limited confidence** label; the third was confirmed. Performance warnings remain attached to the observations. Completion does not certify every candidate or every game as hitch-free.

An additional live fault-injection check deliberately stopped the headset stream between two measurements. Auto tune reconnected the same PC, required a fresh stream identity, and completed the next measurement at approximately 90 encoded/received FPS. Both windows were valid and settings restored without intervention (`benchmark-20261015-183612-a5c7f4f8.json`). The first full audit attempt exposed this dropped-stream recovery defect; that interrupted attempt is not counted as a successful run.

Readback after the completed run verified the original 225 Mbps target, 1664-pixel stream width, 90 Hz display, CBR quarter-resolution multipass and enabled gaze −20 / periphery −12 offsets. Settings, display and power recovery journals were absent, the test scene had exited, and streaming was active. Loading a result opened the settings editor for review without applying it. The review-only change was discarded afterward.

The audit also checked the compact-window layout, recommendation confidence labels, pending quality edits across polling/navigation, cancellation and restoration. Automated verification passed 329 managed tests, seven native tests and 30 compositor assertions. Two patcher fixture tests were skipped because their external driver fixtures were not supplied.

A follow-up stock-restoration check reproduced an exited SteamVR process being treated as live, blocking driver restoration before settings were cleared. The corrected flow disables automatic reapply after saving the previous tuning and checks whether enumerated processes have actually exited. Live verification restored the exact original driver hash, cleared tuning overrides and resumed Valve-controlled VBR/single-pass encoding with `Pending=false` and `NeedsNewSession=false`. The follow-up suite passed 334 managed tests (including the current-driver patch/restore fixture), seven native tests and 30 compositor assertions; the legacy patcher fixture remained unavailable.
