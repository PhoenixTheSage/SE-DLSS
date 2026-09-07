# Space Engineers DLSS

Pulsar client plugin that adds **NVIDIA DLSS Super Resolution** and **DLAA** to Space Engineers 1 (DX11). Frame Generation is not supported.

Architecture supports [Rich HUD Framework](https://github.com/DarkHelmet/RichHudFramework)

## Requirements

- Space Engineers with [Pulsar](https://github.com/SpaceGT/Pulsar) 2.4.0 or later, Windows, NVIDIA RTX, current Game Ready driver
- Pulsar downloads NVIDIA's `nvngx_dlss.dll` (SDK 310.7.0) from this repo's GitHub release and places it next to the plugin DLL. For a local build, put the same file in `Assets/` (gitignored) or next to the plugin DLL in Pulsar's `Local` folder.

## Settings

Plugin config or **Options → Graphics → Anti-aliasing**:

- **Anti-aliasing** — Off, FXAA, or DLSS (shared with the game's graphics options; DLSS disables FXAA)
- **Mode** — Quality, Balanced, Performance, Ultra Performance, or DLAA
- **Model** — Latest (transformer K), J / K / L / M, or CNN F. NVIDIA App cannot override this unofficial title.
- **Sharpness** — optional; transformer models may ignore it
- **Show Status** — NGX, GPU, internal vs output resolution, Anomaly velocity / reactive / AfterUpscale

MSAA is not in the current graphics UI and is incompatible with DLSS.

Motion vectors are camera-reprojected from depth unless [Anomaly Shader Framework](https://github.com/PhoenixTheSage/Anomaly) is also loaded. Anomaly is optional and discovered at runtime ([shader developer wiki](https://github.com/PhoenixTheSage/Anomaly/wiki)):

- **Velocity** — `VelocityRegistry.Active` (object motion). Camera-from-depth remains the fallback.
- **Reactive mask** — catalog `reactiveMask`, bound as DLSS bias-current-color when a pack marks pixels that must not use history.
- **AfterUpscale** — `ClaimUpscale("se-dlss")` at init; `NotifyUpscaleComplete(rc, dest)` after evaluate. When `HasDisplayTenant`, dest is pre-tonemap HDR.
- **History** — `FrameTemporal.InvalidateHistory()` on camera cuts this plugin owns.

No compile-time Anomaly reference. NVIDIA RTX is required for DLSS; Anomaly itself does not need it.

## Driver / NGX

This plugin does not use NVIDIA's public NGX SDK shim. It loads the driver's private `_nvngx.dll` from System32, the `NGXCore` registry path, or DriverStore `nv*` folders, and calls private exports through an OleAut32 `DispCallFunc` trampoline so the driver sees a native return address.

The code fails closed on NGX error codes and defaults to off. A native access violation during init cannot be caught from .NET Framework and will take the game down.

## Building

- .NET Framework 4.8.1 targeting pack and .NET 10 SDK
- Build `ClientPlugin` (deploys to Pulsar `Legacy\Local` or `Interim\Local`; close the game if the DLL is in use)

Debug with Pulsar `Legacy.exe` / `Interim.exe` and `-sources`.

## NVIDIA license

`nvngx_dlss.dll` is NVIDIA redistributable software. The NVIDIA RTX SDKs license is in [`Assets/NVIDIA-LICENSE.txt`](Assets/NVIDIA-LICENSE.txt). Do not vendor NVIDIA SDK headers. https://developer.nvidia.com/sw-notification

The binary is served from the [`nvngx-dlss-310.7.0`](https://github.com/PhoenixTheSage/SE-DLSS/releases/tag/nvngx-dlss-310.7.0) release (SHA-256 `be6e434a94ca32499515eb62ca0e6c274526055d568d0426e4c652dcdfb6ee6e`), not from git.

## Known interactions

These plugins patch the same render-thread surfaces. Prefer not enabling them together until a handshake exists.

### HdrRender

Overlap: `MyToneMapping.Run`, `MyCopyToRT.Run`, and emissive billboards. HdrRender replaces Keen's SDR tone-map with an HDR path and owns the scRGB swapchain.

When Anomaly is loaded this plugin `ClaimUpscale("se-dlss")` at init. If `HasDisplayTenant` (HdrRender-class BT.2390 registered AfterUpscale with `TemporalPolicy.Display`), DLSS skips Keen SDR and does not evaluate HdrRender's scRGB `MyToneMapping.Run` result. It evaluates catalog `hdrColor` (LBuffer) into an output-sized HDR dest and calls `NotifyUpscaleComplete(rc, dest)` so AfterUpscale reads `upscaledColor`. `CopyToRT` and LDR billboards yield when that Display tenant is present or the backbuffer is already `R16G16B16A16`.

Without a Display tenant, keep using one or the other. A HdrRender fork still has to `Register("hdr.tonemap", "AfterUpscale", …, Display)` and yield its tonemap prefix when `HasUpscaleConsumer`.

### SMAA

Overlap: anti-aliasing ownership. SMAA adds its own AA option; this plugin already shares the game's AA dropdown (Off / FXAA / DLSS).

- **Safe now:** pick DLSS or SMAA as the AA, not both.
- **Later — SMAA after DLSS:** run SMAA at output resolution on the DLSS LDR target (AfterUpscale). SMAA would need a public evaluate entry or an Anomaly-style owned pass.
- **Later — detect and yield:** if SMAA is loaded and selected, keep `WantsDlss` false.

### SmoothFrames

Overlap: render-thread camera interpolation plus this plugin's jitter on `DrawGameScene`. Interpolated camera vs jittered projection fights temporal history.

- **Safe now:** disable SmoothFrames camera interpolation while DLSS is on.
- **Later — one temporal owner:** if SmoothFrames is interpolating, skip jitter; or if DLSS is live, skip SmoothFrames interpolation.
- **Later — detect and yield:** skip `DrawGameSceneJitterPatch` when SmoothFrames types are present.

[Anomaly Shader Framework](https://github.com/PhoenixTheSage/Anomaly) is optional and complementary. It is discovered at runtime by type name (`VelocityRegistry`, `BufferCatalog`, `OwnedPassRegistry`, `FrameTemporal`). Packs that Harmony-patch `MyShader` or leave extra RT/SRV bound will fight Anomaly and can break Rich HUD.

## Bug reports

Open an issue with **Show Status** text, GPU, driver version, and `SpaceEngineers.log`.

Anomaly consumer verification and in-game acceptance procedure: [Tests/ANOMALY-ACCEPTANCE.md](Tests/ANOMALY-ACCEPTANCE.md). Numerical reprojection and ghosting acceptance remain pending in-game captures.
