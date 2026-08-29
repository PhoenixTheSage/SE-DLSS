# Space Engineers DLSS

A Pulsar client plugin that injects **NVIDIA DLSS 4.5 Super Resolution** into Space Engineers 1.

The game is DirectX 11. Super Resolution and DLAA are supported. DLSS Frame Generation / Multi Frame Generation are not — those require DX12.

## Requirements

- Space Engineers with [Pulsar](https://github.com/SpaceGT/Pulsar)
- Windows
- NVIDIA RTX GPU and a current Game Ready driver
- `SeDlssNgx.dll` (built from `Native/SeDlssNgx`)
- `nvngx_dlss.dll` from the [NVIDIA DLSS SDK](https://github.com/NVIDIA/DLSS) (`lib/Windows_x86_64/rel/nvngx_dlss.dll`, SDK 310.5+ for transformer presets K/M/L)

Copy both DLLs into `Assets/` (Pulsar `LoadAssets`) or next to the plugin DLL in Pulsar's `Local` folder.

## Settings

Open the plugin config or **Options → Graphics → Anti-aliasing**:

- **Anti-aliasing** — Off, FXAA, or DLSS (same control as the game's graphics options)
- **Mode** — Quality, Balanced, Performance, Ultra Performance, or DLAA
- **Model** — Latest Recommended, or transformer presets J / K / L / M (NVIDIA App cannot override this unofficial title)
- **Sharpness** — optional; transformer models may ignore it
- **Show Status** — NGX state, GPU support, internal vs output resolution

DLSS is an anti-aliasing choice. Selecting it turns the game's FXAA off. MSAA is not in the current graphics UI and is incompatible with DLSS.

Moving grids can ghost in this version: motion vectors are camera-reprojected from depth, not per-object velocity.

## Building

- .NET Framework 4.8.1 targeting pack and .NET 10 SDK
- Build `ClientPlugin` (deploys to Pulsar `Legacy\Local` or `Interim\Local`). Close the game first if deploy reports the DLL is in use.
- Build `Native/SeDlssNgx/SeDlssNgx.vcxproj` (x64 Release) to produce `Assets/SeDlssNgx.dll`, or run `Native/SeDlssNgx/build.bat`

Debug with Pulsar `Legacy.exe` / `Interim.exe` and `-sources` as described in the client plugin template.

## NVIDIA license

`nvngx_dlss.dll` is NVIDIA redistributable software. Do not vendor NVIDIA SDK headers. Notify NVIDIA before a PluginHub release: https://developer.nvidia.com/sw-notification

## Known interactions

[SmoothFrames](https://github.com/WhiteFang34/SmoothFrames) also patches the render thread. DLSS should still compose at copy time; jitter plus camera interpolation can interact.

## Bug reports

Open an issue on this repository with the **Show Status** text, GPU, driver version, and `SpaceEngineers.log`.
