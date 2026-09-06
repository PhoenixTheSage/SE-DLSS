# Anomaly consumption acceptance

Contract checked September 6, 2026 against the adjacent Anomaly checkout:
`wiki/Velocity-contract.md`, `Docs/DLSSMotionVectorIntegration.md`,
`ClientPlugin/Velocity/IVelocityBuffer.cs`, `VelocityConvention.cs`, and `Config.cs`.
These include local, uncommitted producer updates. GitHub fetches were unavailable;
this is validation against the local update, not confirmation of a published release.

The required convention is exactly 15: unjittered, internal pixel units, Y down,
current-to-previous. `previousPixel = currentPixel + motion`. NGX scale is (+1,+1),
MVLowRes is enabled and MVJittered disabled. Legacy 7 and unknown flags fall back.
The catalog cannot certify conventions and is no longer a velocity fallback.
VelocityProbe must be readable and Off. Missing probe metadata fails closed.
Anomaly does not expose producer frame IDs or frame-latched probe state; freshness
across a probe toggle cannot be proved from this API. Keep probes Off for captures.

## Automated contract checks

Run `dotnet run --project Tests/AnomalyAcceptance.csproj`.
The harness links the production reflection consumer and reset policy. It checks
conventions, all four current probes, unknown probe metadata, resize rejection,
resource replacement, invalid history, unavailable/null buffers, cuts and source resets.
It does not exercise D3D, NGX, or certify rendered image quality.

## In-game acceptance (pending)

Use the same scene, DLSS mode, jitter settings and exposure for every comparison.
Disable unrelated rendering plugins for the baseline. Record Anomaly revision,
DLSS revision, GPU/driver, internal/output dimensions, and the Show Status binding
snapshot. Use a GPU capture for consecutive-frame textures; debug colors are not
numeric evidence. Test Anomaly GBuffer, Anomaly CameraOnly and local camera fallback.

1. **Reprojection:** capture stationary geometry, fixed-camera grids moving right
   and down, camera translation/rotation over static voxels, and characters.
   For a tracked point moving +3 internal pixels horizontally, require MV=(-3,0).
   Reproject with currentPixel+MV and compare to the previous unjittered position,
   excluding disocclusions and depth-dilation boundaries. Proposed acceptance:
   stationary median error <=0.1 pixel and tracked-point error <=0.5 pixel.
   Check finite RG values and no unexplained one-frame spikes over 120 frames.
   Camera-only paths cannot reproduce independent object motion; use that as a
   control rather than expecting object reprojection to pass on those paths.
2. **Ghosting:** capture at least 120 identical frames of moving grid edges,
   characters and emissive/translucent content against a contrasting background.
   Compare consecutive output frames and trails with GBuffer versus camera-only.
   Require no persistent duplicate edge after motion stops and no regression on
   static voxels; annotate disocclusion/reactive-mask limitations separately.
   Save crops and frame indices; visual responsiveness alone is not a pass.
3. **Camera cuts:** teleport and rotate abruptly. Require camera-cut or invalid-
   history reset on the first evaluated post-cut frame, no old-scene history trail,
   and steady frames returning to reset=none. Check AfterUpscale only on success.
4. **Resolution:** change Quality/Performance/DLAA and window resolution repeatedly.
   Require rejection of old-size buffers, a resolution/configuration/history reset,
   then the new texture identity and matching internal dimensions at NGX. Confirm
   no stretch-history artifact, crash, or disposal of the producer texture.
5. **Source/probe changes:** switch GBuffer/CameraOnly/local fallback. Require a
   source reset even when switching between Anomaly modes. Enable each probe and
   verify explicit camera fallback; turn probes Off and allow producer history to
   settle before numerical captures. Legacy flags 7 must never reach NGX.

Binding evidence reports a consumer render-frame counter, evaluate attempt,
selected source, producer texture descriptor, actual NGX pointer and parameter
readback, dimensions, scale, create flags, reset causes and NGX result. It is a
snapshot of the last submission, not a promise the current registry still matches.
Show Status is available in Release; Debug builds also sample evidence in the log.
