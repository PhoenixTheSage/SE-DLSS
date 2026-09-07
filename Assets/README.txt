Pulsar 2.4.0+ downloads nvngx_dlss.dll from the GitHub release named in
SpaceEngineersDLSS.xml (Url + Sha256, Placement=Bin) and calls
Plugin.LoadAssets with the resolved file path.

- nvngx_dlss.dll
  NVIDIA DLSS Super Resolution redistributable (NVIDIA/DLSS SDK 310.7.0,
  lib/Windows_x86_64/rel/). Not committed to git. For a local build, drop
  the release asset here or next to the plugin DLL. After replacing the
  file, update the NvngxDlss Sha256 (and release tag) in SpaceEngineersDLSS.xml.

- NVIDIA-LICENSE.txt
  NVIDIA RTX SDKs license shipped next to the redistributable.
