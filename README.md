# Space Engineers DLSS

Pulsar client plugin that adds **NVIDIA DLSS 4.5 Super Resolution** and **DLAA** to Space Engineers 1 (DX11). Frame Generation is not supported.

Architecture supports [Rich HUD Framework](https://github.com/DarkHelmet/RichHudFramework)

## Requirements

- Space Engineers with [Pulsar](https://github.com/SpaceGT/Pulsar), Windows, NVIDIA RTX, current Game Ready driver
- PluginHub and `Assets/` already include `SeDlssNgx.dll` and NVIDIA's `nvngx_dlss.dll` (SDK 310.5+). For a local build, put both next to the plugin DLL in Pulsar's `Local` folder.

## Settings

Plugin config or **Options → Graphics → Anti-aliasing**:

- **Anti-aliasing** — Off, FXAA, or DLSS (shared with the game's graphics options; DLSS disables FXAA)
- **Mode** — Quality, Balanced, Performance, Ultra Performance, or DLAA
- **Model** — Latest (transformer K), J / K / L / M, or CNN F. NVIDIA App cannot override this unofficial title.
- **Sharpness** — optional; transformer models may ignore it
- **Show Status** — NGX, GPU, internal vs output resolution

MSAA is not in the current graphics UI and is incompatible with DLSS.

Moving grids can ghost: motion vectors are camera-reprojected from depth, not per-object velocity.

## Building

- .NET Framework 4.8.1 targeting pack and .NET 10 SDK
- Build `ClientPlugin` (deploys to Pulsar `Legacy\Local` or `Interim\Local`; close the game if the DLL is in use)
- Build `Native/SeDlssNgx/SeDlssNgx.vcxproj` (x64 Release) or run `Native/SeDlssNgx/build.bat` for `Assets/SeDlssNgx.dll`

Debug with Pulsar `Legacy.exe` / `Interim.exe` and `-sources`.

## NVIDIA license

`nvngx_dlss.dll` is NVIDIA redistributable software and ships in `Assets/`. Do not vendor NVIDIA SDK headers. https://developer.nvidia.com/sw-notification

## Known interactions

[SmoothFrames](https://github.com/WhiteFang34/SmoothFrames) also patches the render thread. Jitter plus camera interpolation can interact.

## Bug reports

Open an issue with **Show Status** text, GPU, driver version, and `SpaceEngineers.log`.
