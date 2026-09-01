Pulsar copies these files from named <Asset> entries in SpaceEngineersDLSS.xml
(Placement=Bin) and calls Plugin.LoadAssets with their resolved paths.

- nvngx_dlss.dll
  NVIDIA DLSS Super Resolution redistributable (NVIDIA/DLSS SDK, lib/Windows_x86_64/rel/).
  Shipped here so Pulsar deploys it with the plugin. Do not vendor NVIDIA SDK headers.
  After replacing this file, update the NvngxDlss Sha256 in SpaceEngineersDLSS.xml.
