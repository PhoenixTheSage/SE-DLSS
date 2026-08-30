Pulsar copies this folder and calls Plugin.LoadAssets with its path.

- SeDlssNgx.dll
  Native wrapper. Build Native\SeDlssNgx\SeDlssNgx.vcxproj (x64 Release) or run Native\SeDlssNgx\build.bat.

- nvngx_dlss.dll
  NVIDIA DLSS Super Resolution redistributable (NVIDIA/DLSS SDK, lib/Windows_x86_64/rel/).
  Shipped here so Pulsar deploys it with the plugin. Do not vendor NVIDIA SDK headers.
