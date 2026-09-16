# Graph Report - SE-DLSS  (2026-09-16)

## Corpus Check
- Corpus is ~45,710 words - fits in a single context window. You may not need a graph.

## Summary
- 1217 nodes · 2365 edges · 76 communities (62 shown, 13 thin omitted)
- Extraction: 99% EXTRACTED · 1% INFERRED · 0% AMBIGUOUS · INFERRED: 32 edges (avg confidence: 0.86)
- Token cost: 8,200 input · 9,800 output

## Community Hubs (Navigation)
- Anti Aliasing Choice
- Post Pp Hud Space
- Gpu Support
- App Log Callback
- Buffer
- Alloc Params Delegate
- Button
- Hdr Uav Target
- Game Assemblies To Publicize
- Dlss Runtime
- Ngx Parameter
- Anomaly Hook
- Jitter
- H L S L
- Anomaly Hook 2
- Ngx Host
- Preloader Helpers
- Element
- Config Dialog Example Screenshot
- Settings Screen
- Settings Generator
- Ngx Feature Info
- Velocity Acceptance
- Color
- Anomaly Owns Shared Gaps
- Tone Mapping Patch
- Debug Log
- Render Trace Bind
- I Rtv Bindable
- Control Button Data
- Rich H U D
- Copy To Rt Patch
- Control
- Checkbox
- Method Impl
- Dlss Status
- Slider
- Config
- Camera Update Screen Size
- Hashing
- Program
- Dropdown
- Simple
- Tools
- Assembly Definition
- Client Plugin
- Button 2
- Color 2
- Element 2
- Textbox
- Layout
- Binding
- N V I D
- Device Dispose Patch
- Update Screen Size Patch
- Assembly
- Assets R E A
- Get Screen Device Resolution
- Git Hub Copilot Instructions
- Draw Patch
- Apply Settings Patch
- Borrow Custom Patch
- G Buffer Pass Begin
- After Upscale
- Disp Call Trampoline
- Harmony Postfix
- Ngx Names
- Chromatic Aberration Patch
- Fxaa Enabled Patch
- Microsoft. N E T.
- clean.sh
- Deploy.sh
- Shader Bytecode
- Draw Game Scene Jitter
- verify props.sh

## God Nodes (most connected - your core abstractions)
1. `DlssRuntime` - 68 edges
2. `AnomalyHook` - 51 edges
3. `ClientPlugin.Dlss` - 47 edges
4. `DlssD3d` - 42 edges
5. `BillboardOutputPass` - 42 edges
6. `NgxDriver` - 40 edges
7. `GameAntiAliasing` - 35 edges
8. `NgxHost` - 27 edges
9. `HdrUavTarget` - 26 edges
10. `PersistentLdrTarget` - 25 edges

## Surprising Connections (you probably didn't know these)
- `DLSS SDK` --semantically_similar_to--> `NVIDIA DLSS Super Resolution`  [INFERRED] [semantically similar]
  Assets/NVIDIA-LICENSE.txt → README.md
- `Velocity Convention 15` --semantically_similar_to--> `VelocityRegistry`  [INFERRED] [semantically similar]
  Tests/ANOMALY-ACCEPTANCE.md → README.md
- `HLSL Skill README (cursor)` --semantically_similar_to--> `HLSL Skill README (agents)`  [INFERRED] [semantically similar]
  .cursor/skills/a5c-ai-babysitter-hlsl/README.md → .agents/skills/a5c-ai-babysitter-hlsl/README.md
- `HLSL Skill (cursor)` --semantically_similar_to--> `HLSL Skill (agents)`  [INFERRED] [semantically similar]
  .cursor/skills/a5c-ai-babysitter-hlsl/SKILL.md → .agents/skills/a5c-ai-babysitter-hlsl/SKILL.md
- `GitHub Copilot Instructions` --semantically_similar_to--> `VS Code AGENTS Pointer`  [INFERRED] [semantically similar]
  .github/copilot-instructions.md → .vscode/AGENTS.md

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **HLSL Skill File Mirrors** — _agents_skills_a5c_ai_babysitter_hlsl_readme, _agents_skills_a5c_ai_babysitter_hlsl_skill, _cursor_skills_a5c_ai_babysitter_hlsl_readme, _cursor_skills_a5c_ai_babysitter_hlsl_skill [INFERRED 0.95]
- **Exclusive Upscaler Anti-Aliasing Selection** — readme_anti_aliasing, readme_dlss_super_resolution, readme_frs, readme_smaa, readme_antiaaliasing_handshake [EXTRACTED 1.00]
- **Optional Anomaly DLSS Integration** — readme_anomaly_shader_framework, readme_velocity_registry, readme_reactive_mask, readme_after_upscale, readme_claim_upscale, readme_frame_temporal [EXTRACTED 1.00]
- **Config Demo Widget Set** — docs_configdialogexample_toggle, docs_configdialogexample_integer_slider, docs_configdialogexample_number_slider, docs_configdialogexample_text_field, docs_configdialogexample_dropdown, docs_configdialogexample_color_picker, docs_configdialogexample_color_with_alpha, docs_configdialogexample_keybind, docs_configdialogexample_action_button [EXTRACTED 1.00]
- **Numeric Slider Family** — docs_configdialogexample_integer_slider, docs_configdialogexample_number_slider, docs_configdialogexample_numeric_type_split [INFERRED 0.85]
- **Color Row Family** — docs_configdialogexample_color_picker, docs_configdialogexample_color_with_alpha, docs_configdialogexample_hex_color_field [EXTRACTED 1.00]

## Communities (76 total, 13 thin omitted)

### Community 0 - "Anti Aliasing Choice"
Cohesion: 0.06
Nodes (25): AntiAliasingChoice, DLSS, FRS, FXAA, Off, AntiAliasingHandshake, GameAntiAliasing, MyGraphicsSettings (+17 more)

### Community 1 - "Post Pp Hud Space"
Cohesion: 0.08
Nodes (19): PostPpHudSpace, List, MatrixD, MyBillboard, BillboardAddPatch, BillboardAddRangePatch, BillboardFrameCompletePatch, BillboardLdrPatch (+11 more)

### Community 2 - "Gpu Support"
Cohesion: 0.05
Nodes (27): GpuSupport, AdapterName, CanAttemptDlss, CanOfferDlss, IsNvidia, Probed, StatusLine, UnsupportedReason (+19 more)

### Community 3 - "App Log Callback"
Cohesion: 0.09
Nodes (36): AppLogCallback, ApplyHintPresets(), CallNgxInit(), Device, Exception, IntPtr, MethodImpl, EnsureNativeStrings() (+28 more)

### Community 4 - "Buffer"
Cohesion: 0.13
Nodes (20): Buffer, DlssD3d, Device, DeviceContext, IntPtr, Resource, ShaderResourceView, UnorderedAccessView (+12 more)

### Community 5 - "Alloc Params Delegate"
Cohesion: 0.08
Nodes (30): AllocParamsDelegate, NgxDriver, AllocateParameters, CreateFeature, DestroyParameters, EvaluateFeature, GetCapabilityParameters, HasInitApp (+22 more)

### Community 6 - "Button"
Cohesion: 0.07
Nodes (24): Button, Config, AntiAliasing, Enabled, EnabledCompat, Mode, Model, Sharpness (+16 more)

### Community 7 - "Hdr Uav Target"
Cohesion: 0.06
Nodes (31): HdrUavTarget, Format, Linear, MipLevels, Name, Resource, Size, Size3 (+23 more)

### Community 8 - "Game Assemblies To Publicize"
Cohesion: 0.13
Nodes (15): CodeInstructionNotFound, TranspilerHelpers, CodeInstruction, CodeInstructionPredicate, FieldInfo, IEnumerable, List, MethodBase (+7 more)

### Community 9 - "Dlss Runtime"
Cohesion: 0.07
Nodes (26): DlssRuntime, BindingContext, EvaluateCount, EvaluatedThisFrame, InternalHeight, InternalWidth, IsHdrSwapchainLive, IsLive (+18 more)

### Community 10 - "Ngx Parameter"
Cohesion: 0.11
Nodes (18): NgxParameter, Pointer, NgxResult, VTable, Dictionary, IntPtr, GetFloatFn, GetIntFn (+10 more)

### Community 11 - "Anomaly Hook"
Cohesion: 0.13
Nodes (14): AnomalyHook, CanNotifyUpscale, CatalogFound, ClaimedUpscale, HasDisplayTenant, RegistryFound, SelectedSource, SelectionReason (+6 more)

### Community 12 - "Jitter"
Cohesion: 0.09
Nodes (16): Jitter, HasPrevious, JitteredInvViewProjection, OffsetX, OffsetY, PreviousViewProjection, UnjitteredViewProjection, MatrixD (+8 more)

### Community 13 - "H L S L"
Cohesion: 0.08
Nodes (24): HLSL Skill README (agents), GPU Compute, DirectX Shaders, GLSL Skill, HLSL, Shader Optimization Skill, HLSL Skill (agents), Compute Shaders (+16 more)

### Community 14 - "Anomaly Hook 2"
Cohesion: 0.17
Nodes (4): VRage.Render11.Resources, ClientPlugin.Dlss, VRage.Utils, ClientPlugin.Patches

### Community 15 - "Ngx Host"
Cohesion: 0.14
Nodes (13): NgxHost, CurrentPresetHint, FeatureIsHdr, IsLoaded, IsReady, IsSupported, LastError, SupportKnown (+5 more)

### Community 16 - "Preloader Helpers"
Cohesion: 0.21
Nodes (9): PreloaderHelpers, CodeInstructionPredicate, Instruction, List, Collection, FieldReference, MethodDefinition, MethodReference (+1 more)

### Community 17 - "Element"
Cohesion: 0.20
Nodes (22): Element, Path, _detect_pulsar_dir(), _detect_space_engineers(), _generate_guid(), _get_install_locations(), _get_linux_steam_path(), _get_steam_path() (+14 more)

### Community 18 - "Config Dialog Example Screenshot"
Cohesion: 0.12
Nodes (21): Config Dialog Example Screenshot, Action Button, Dialog Close Button, Color Picker RGB, Color Picker RGBA, Config Demo Dialog, Dropdown Combo Box, Hex Color Text Field (+13 more)

### Community 19 - "Settings Screen"
Cohesion: 0.16
Nodes (9): SettingsScreen, Func, List, MyGuiControlBase, Vector2, StatusScreen, IEnumerable, StringBuilder (+1 more)

### Community 20 - "Settings Generator"
Cohesion: 0.16
Nodes (11): AttributeInfo, SettingsGenerator, ActiveLayout, Dialog, Action, Func, List, MethodInfo (+3 more)

### Community 21 - "Ngx Feature Info"
Cohesion: 0.18
Nodes (12): FeatureCommonInfo, LoggingInfo, NgxFeatureInfo, NativeSize, Pointer, PathListInfo, IEnumerable, IntPtr (+4 more)

### Community 22 - "Velocity Acceptance"
Cohesion: 0.15
Nodes (11): VelocityAcceptance, Buffer, Convention, Height, HistoryValid, IsAvailable, NativeResource, Width (+3 more)

### Community 23 - "Color"
Cohesion: 0.15
Nodes (8): None, SettingsPanelSize, List, MyGuiControlBase, Vector2, ClientPlugin.Settings.Tools, ClientPlugin.Settings.Elements, ClientPlugin.Settings.Layouts

### Community 24 - "Anomaly Owns Shared Gaps"
Cohesion: 0.17
Nodes (15): Anomaly Owns Shared Gaps, IsolatedMix Energy, Anomaly Shader Framework, FrameTemporal.InvalidateHistory, Reactive Mask, VelocityRegistry, Anomaly Consumption Acceptance, Camera Cut History Reset (+7 more)

### Community 26 - "Tone Mapping Patch"
Cohesion: 0.21
Nodes (6): ToneMappingPatch, Exception, HarmonyPostfix, HarmonyPrefix, HarmonyPriority, IBorrowedCustomTexture

### Community 27 - "Debug Log"
Cohesion: 0.23
Nodes (7): DebugLog, FilePath, FrameSite, Dictionary, Conditional, FrameSite, StreamWriter

### Community 28 - "Render Trace Bind"
Cohesion: 0.29
Nodes (5): RenderTraceBind, Exception, MethodInfo, IResource, ISrvBindable

### Community 30 - "Control Button Data"
Cohesion: 0.25
Nodes (10): ControlButtonData, KeybindAttribute, SupportedTypes, Action, Func, List, Type, MyControl (+2 more)

### Community 31 - "Rich H U D"
Cohesion: 0.18
Nodes (13): Rich HUD Config Save Rule, Anti-aliasing Setting, AntiAliasingHandshake, DLAA, NVIDIA DLSS Super Resolution, DLSS Frame Generation, AMD FRS, MSAA (+5 more)

### Community 32 - "Copy To Rt Patch"
Cohesion: 0.18
Nodes (5): CopyToRtPatch, HarmonyPrefix, IRtvBindable, DrawScenePatch, HarmonyPrefix

### Community 33 - "Control"
Cohesion: 0.17
Nodes (10): Control, MyGuiControlBase, Vector2, SeparatorAttribute, SupportedTypes, Action, Func, List (+2 more)

### Community 34 - "Checkbox"
Cohesion: 0.18
Nodes (9): Attribute, CheckboxAttribute, SupportedTypes, Action, Func, List, Type, IgnoresAccessChecksToAttribute (+1 more)

### Community 36 - "Dlss Status"
Cohesion: 0.26
Nodes (4): DlssStatus, CurrentText, StringBuilder, StringBuilder

### Community 37 - "Slider"
Cohesion: 0.18
Nodes (10): SliderAttribute, SupportedTypes, SliderType, Float, Integer, Action, Func, List (+2 more)

### Community 38 - "Config"
Cohesion: 0.24
Nodes (3): ClientPlugin, ClientPlugin.Settings, ClientPlugin.RichHud

### Community 39 - "Camera Update Screen Size"
Cohesion: 0.29
Nodes (6): CameraUpdateScreenSizePatch, CameraUpdateViewportPatch, UpdateScreenSizePatch, HarmonyPrefix, MyCamera, MyViewport

### Community 40 - "Hashing"
Cohesion: 0.27
Nodes (7): Hashing, CodeInstruction, IEnumerable, Instruction, MethodImpl, MethodInfo, ConstructorInfo

### Community 41 - "Program"
Cohesion: 0.20
Nodes (6): DebugLog, DlssRuntime, ProbeConfig, VelocityProbe, VelocitySource, MyLog

### Community 42 - "Dropdown"
Cohesion: 0.28
Nodes (6): DropdownAttribute, SupportedTypes, Action, Func, List, Type

### Community 43 - "Simple"
Cohesion: 0.33
Nodes (7): Simple, SettingsPanelSize, List, MyGuiControlBase, Vector2, MyGuiControlParent, MyGuiControlScrollablePanel

### Community 44 - "Tools"
Cohesion: 0.31
Nodes (4): Tools, Color, Match, Regex

### Community 45 - "Assembly Definition"
Cohesion: 0.25
Nodes (4): AssemblyDefinition, IEnumerable, Preloader, TargetDLLs

### Community 46 - "Client Plugin"
Cohesion: 0.25
Nodes (6): ClientPlugin, net10.0, net48, Krafs.Publicizer (2.3.0), Lib.Harmony (2.4.2), Mono.Cecil (0.11.6)

### Community 47 - "Button 2"
Cohesion: 0.29
Nodes (6): ButtonAttribute, SupportedTypes, Action, Func, List, Type

### Community 48 - "Color 2"
Cohesion: 0.29
Nodes (7): ColorAttribute, SupportedTypes, Action, Color, Func, List, Type

### Community 49 - "Element 2"
Cohesion: 0.29
Nodes (6): IElement, SupportedTypes, Action, Func, List, Type

### Community 50 - "Textbox"
Cohesion: 0.29
Nodes (6): TextboxAttribute, SupportedTypes, Action, Func, List, Type

### Community 51 - "Layout"
Cohesion: 0.29
Nodes (6): Layout, SettingsPanelSize, Func, List, MyGuiControlBase, Vector2

### Community 52 - "Binding"
Cohesion: 0.39
Nodes (3): Binding, IMyInput, MyKeys

### Community 53 - "N V I D"
Cohesion: 0.33
Nodes (7): NVIDIA RTX SDKs License, Commercial Release Notification, SDK Distribution Requirements, DLSS SDK, NGX SDK, NVIDIA GPU Interoperability Requirement, Space Engineers DLSS README

### Community 54 - "Device Dispose Patch"
Cohesion: 0.33
Nodes (4): DeviceDisposePatch, Harmony, MethodInfo, DisposeBase

### Community 55 - "Update Screen Size Patch"
Cohesion: 0.29
Nodes (4): CameraViewportSizePatch, HarmonyPostfix, MethodBase, Vector2

### Community 57 - "Assets R E A"
Cohesion: 0.40
Nodes (6): Assets README, nvngx_dlss.dll Asset, Plugin.LoadAssets, SpaceEngineersDLSS.xml, nvngx_dlss.dll, Pulsar

### Community 58 - "Get Screen Device Resolution"
Cohesion: 0.40
Nodes (3): GetDeviceVSyncModePatch, GetScreenDeviceResolutionPatch, HarmonyPrefix

### Community 59 - "Git Hub Copilot Instructions"
Cohesion: 0.60
Nodes (5): GitHub Copilot Instructions, VS Code AGENTS Pointer, Root Agent Instructions, se-dev Skill, Space Engineers Plugin Developer

### Community 61 - "Apply Settings Patch"
Cohesion: 0.40
Nodes (3): ApplySettingsPatch, HarmonyPrefix, MyRenderDeviceSettings

### Community 62 - "Borrow Custom Patch"
Cohesion: 0.40
Nodes (4): BorrowCustomPatch, HarmonyPrefix, IBorrowedCustomTexture, MyBorrowedRwTextureManager

### Community 63 - "G Buffer Pass Begin"
Cohesion: 0.40
Nodes (3): GBufferPassBeginPatch, HarmonyPrefix, MyGBufferPass

### Community 64 - "After Upscale"
Cohesion: 0.40
Nodes (5): AfterUpscale, ClaimUpscale se-dlss, HasDisplayTenant, hdrColor LBuffer, HdrRender

### Community 65 - "Disp Call Trampoline"
Cohesion: 0.40
Nodes (5): DispCallFunc Trampoline, NGX Fail Closed, NVIDIA NGX, Probe Metadata Fail Closed, VelocityProbe

### Community 68 - "Chromatic Aberration Patch"
Cohesion: 0.50
Nodes (3): ChromaticAberrationPatch, HarmonyPrefix, IUavBindable

## Ambiguous Edges - Review These
- `NVIDIA NGX` → `NGX SDK`  [AMBIGUOUS]
  README.md · relation: conceptually_related_to

## Knowledge Gaps
- **201 isolated node(s):** `net10.0`, `net48`, `Lib.Harmony (2.4.2)`, `Mono.Cecil (0.11.6)`, `Krafs.Publicizer (2.3.0)` (+196 more)
  These have ≤1 connection - possible missing edges or undocumented components. (Counts symbols only; 381 node(s) total have ≤1 connection when file, concept and rationale nodes are included.)
- **13 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **What is the exact relationship between `NVIDIA NGX` and `NGX SDK`?**
  _Edge tagged AMBIGUOUS (relation: conceptually_related_to) - confidence is low._
- **Why does `ClientPlugin.Dlss` connect `Anomaly Hook 2` to `Anti Aliasing Choice`, `Post Pp Hud Space`, `App Log Callback`, `Alloc Params Delegate`, `Button`, `Ngx Parameter`, `Jitter`, `Ngx Feature Info`, `Velocity Acceptance`, `Dlss Status`, `Config`, `Program`, `Dropdown`, `Update Screen Size Patch`, `Get Screen Device Resolution`, `G Buffer Pass Begin`, `Ngx Names`, `Fxaa Enabled Patch`, `Shader Bytecode`?**
  _High betweenness centrality (0.243) - this node is a cross-community bridge._
- **Why does `System.Runtime.CompilerServices` connect `Game Assemblies To Publicize` to `Checkbox`, `App Log Callback`, `Config`, `Anomaly Hook 2`?**
  _High betweenness centrality (0.083) - this node is a cross-community bridge._
- **Why does `DlssRuntime` connect `Dlss Runtime` to `Copy To Rt Patch`, `Harmony Postfix`, `Method Impl`, `Hdr Uav Target`, `Render Trace Bind`, `Anomaly Hook 2`, `Ngx Host`, `Apply Settings Patch`, `Invalidate History`, `Tone Mapping Patch`, `Debug Log`, `Draw Patch`, `I Rtv Bindable`?**
  _High betweenness centrality (0.079) - this node is a cross-community bridge._
- **What connects `net10.0`, `net48`, `Lib.Harmony (2.4.2)` to the rest of the system?**
  _201 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Anti Aliasing Choice` be split into smaller, more focused modules?**
  _Cohesion score 0.0635814889336016 - nodes in this community are weakly interconnected._
- **Should `Post Pp Hud Space` be split into smaller, more focused modules?**
  _Cohesion score 0.0763888888888889 - nodes in this community are weakly interconnected._