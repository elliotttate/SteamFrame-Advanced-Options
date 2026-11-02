# Building from source

## Requirements

- Windows 11 x64
- .NET 9 SDK
- Visual Studio 2022 or later, or Build Tools, with **Desktop development with C++**
  (including CMake), **WinUI application development build tools**, and Windows SDK
  **10.0.22621**. The scripts find Visual Studio and its bundled CMake and Ninja automatically.
- Python 3 on PATH, only for the receiver-diagnostics tests. They're skipped without it.

The WinUI app must be built with Visual Studio's MSBuild. `dotnet build` can't build it because
the .NET CLI lacks the Windows App SDK packaging tasks; the test projects build fine with it.

## Build

```powershell
.\Build.ps1
```

This builds the native encoder probe and runs its tests, builds the app, and runs the test
projects. Output goes to `SteamLinkVRSettings\bin\x64\Release\net9.0-windows10.0.22621.0\`.

Close any copy of the app running from that folder first, or build elsewhere:

```powershell
.\Build.ps1 -OutputDirectory "artifacts\BuildCheck"
```

Options: `-Configuration Debug`, `-SkipNativeTests`.

`Launch-Latest.ps1` opens the newest complete local app output under the app's
`bin` directory or `artifacts`. It checks required files and orders builds by the
app assembly timestamp. Use `-ResolveOnly` to inspect the selected build without
opening it. A desktop shortcut to this script follows future local builds.

When using the optional diagnostic CLI, build it against the selected app:

```powershell
.\Native\HevcTune\Build-ForApp.ps1 -AppDirectory "artifacts\BuildCheck"
```

The script verifies that both use the same native helper. Do not restore normal
play with historical experiment tools: their older helper can make the app's
update notice return after every research restart.

The matching diagnostic tool accepts `--sender` to read and verify the current
sender's synchronous/asynchronous mode. Start monitoring in the app first.
This option never installs the helper or changes sender settings.

Opening the solution in Visual Studio also works before the native helper has been built. You'll
see a warning, and encoder monitoring stays unavailable until `Native\build.ps1` has run.

## Package

```powershell
.\Package.ps1
```

Produces a self-contained portable folder and ZIP under `artifacts\`:
`SteamFrame-Advanced-Options-win-x64\` and `SteamFrame-Advanced-Options-win-x64.zip`.
The package includes the .NET and Windows App SDK runtimes, the README, user guides
and third-party licenses. The codec-enabled release also requires a staged
`artifacts\CodecLab` companion. Use `-SkipBuild` to package an existing build.

## Codec-enabled package staging

Build the independent native codec adapter with:

```powershell
.\Native\AV1Lab\build.ps1 -OutputDirectory artifacts\CodecLab\Backend
```

The bundled Frame decoder is an exact, previously accepted kernel build. Its
corresponding source, source provenance, GPL license and direct kernel build
instructions ship as `CodecLab\Assets\decoder-source.zip`. See the
[decoder build recipe](../Frame/AV1/decoder-build.md) for the matching header
package. Rebuilt modules can have a different fingerprint; they need hardware
acceptance/rollback testing before updating the root helper's pinned hash.

With the accepted module under `artifacts\CodecLab\Assets` (or beside its source)
and its corresponding sources under `artifacts\AV1Lab\iris-7.0-frame-candidate`,
stage the package using CPython 3.13:

```powershell
python Frame\AV1\stage_codec_package.py
.\Package.ps1 -SkipBuild
```

Use `--decoder-module PATH` if the accepted module is elsewhere.
The optional `--backend PATH` argument selects final tested native build products;
the default is `artifacts\CodecLab\Backend`. The staging script copies only source-controlled companion helpers and the
accepted decoder assets, includes corresponding source, and checks the isolated
PC runtime's imports. It does not load a decoder, authenticate to the Frame or
launch SteamVR. A clean checkout therefore needs the tested module/source assets
in addition to the native build products to produce the codec-enabled release;
it never substitutes a stale or unverified module. The portable release already
contains those assets, so end users do not need this build toolchain.

## Tests

The projects under `Tests\` are xUnit projects in the solution and appear in Test Explorer.

```powershell
dotnet test Tests\CoreTests
dotnet test Tests\BenchmarkTests
dotnet test Tests\StreamingHealthTests
dotnet test Tests\StreamingFrameTraceTests
dotnet test Tests\FrameReceiverDiagnosticsTests
```

| Project | Covers |
| --- | --- |
| CoreTests | Settings file safety, the settings editor view model, Steam paths, preferences, rate math, traffic selection |
| BenchmarkTests | Auto tune search, telemetry validity, startup retries, cancellation, settings ownership and display recovery |
| StreamingHealthTests | PC log parsing, rotation, truncation and privacy of captured events |
| StreamingFrameTraceTests | Recovery-trace timing without inventing drops |
| FrameReceiverDiagnosticsTests | The headset-side collector script, run locally against fixture logs |
| PatcherTests | Exact patch and restore hashes, refusal paths, backups and adaptive discovery |
| CompositorTimingTests | PC presentation counter handling (console project, run by `Build.ps1`) |

**PatcherTests** needs a verified original `driver_vrlink.dll`, which is never committed:

```powershell
$env:SLVR_PATCHER_FIXTURE = "path\to\verified-original-driver_vrlink.dll"
$env:SLVR_PATCHER_CURRENT_FIXTURE = "path\to\later-original.dll"   # optional
$env:SLVR_PATCHER_STABLE_FIXTURE = "path\to\stable-original.dll"   # optional, public build 25330290
dotnet test Tests\PatcherTests
```

Native tests run as part of `Native\build.ps1`. The managed probe harness checks the built helper:

```powershell
dotnet run --project Native\tests\managed -- Native\build\SteamFrameEncoderProbe.dll
```

None of these tests modify your Steam configuration, and none load anything into a running SteamVR.

## Source map

| Path | Contents |
| --- | --- |
| `SteamLinkVRSettings\Views` | Frame dashboard and controls page, settings editor page |
| `SteamLinkVRSettings\ViewModels` | Settings editor view model |
| `SteamLinkVRSettings\Frame` | SteamVR paths and live settings, connections, patcher, encoder probe controller, diagnostics |
| `SteamLinkVRSettings\Services` | Atomic `steamvr.vrsettings` reading and writing |
| `Native` | C++ NVENC encoder probe, quality-map logic and tests; see [its README](../Native/README.md) |
