# Experimental fixed 540p scenes and composition

Apply renderer patches0088 and0089 after0087, then rebuild ryty_gpu. These are opt-in experiments, not general resolution-preset or upscaler support. Neither changes launcher defaults. Use only the verified GT Sport executable and Native renderer; the helper refuses unsupported allocation signatures.

With Python3 installed, from the source checkout:

```powershell
python renderer/tools/run-experimental-540p.py "<portable-game-folder>" --verify-only
python renderer/tools/run-experimental-540p.py "<portable-game-folder>"
# Optional lower-resolution post-processing, with normal-resolution HUD draws:
python renderer/tools/run-experimental-540p.py "<portable-game-folder>" --low-composition
```

Close the game before changing the configuration. The helper sets startup-only SHADGT_PATCH edits for three verified scene allocation sites; it does not modify the executable on disk. It enables the previously validated fragment descriptor and bounded compute-prefix improvements. Rejected material/full-range compute/expanded-prefix experiments stay off. Exit and start normally to leave the test configuration.

Patch0088 uses SHADGT_EXPERIMENTAL_SCENE_VIEWPORT=1 to match the game's scoped scene viewport to960x540 while preserving1920x1080 output. A six-byte signature-checked hook preserves the original SysV argument list and adjusts only the verified target-size combination. In both prepared and recorder bindings, final composition resolves its locally copied sampled descriptor against a matching already-rendered960x540 target. Guest descriptors and registry identity are not rewritten. Preparing workers defer if the preceding target is not yet registered.

GPU dumps independently confirmed960x540 color, depth, motion and history, with1920x1080 output in the tested race. The user also checked3D showroom, Brand Central UI and return-to-track rendering. Existing AMD material defects are outside this change. Only this fixed resolution pair and the tested executable are validated; arbitrary presets, other game versions, DLSS/FSR integration and other GPUs are not claimed.

Patch0089 adds SHADGT_LOW_RES_COMPOSITION=1. For the known full-screen composition shader b33ec4df only, it shades the upper-left960x540 region, copies to a separate transfer scratch image, and scales to1080p before subsequent HUD draws. Strict guards require one full-output viewport/scissor, one full-RGBA color attachment, no depth attachment or blending, single sample/mip/layer, matching formats and hardware linear-blit support. Unsupported states retain the original path. The renderer owns the scratch for its normal GPU-drained lifetime; it is never replaced while older lists may reference it. Transfer barriers order copy, scale and later attachment work. Normal dynamic-state updates restore following draws' viewport/scissor.

This reduces post-processing resolution: grain, gradients or sharpness can differ. It is a quality/performance choice, not pixel-identical output. On local AMD integrated graphics,15 samples measured composition shading1.106ms plus scaling0.536ms, compared with3.986ms for the original composition control. Sequential foreground pacing measured17.5 versus16.6FPS. These are local observations, not a repeated crossover benchmark or universal speedup. The user accepted quality and verified track ->3D menu/showroom ->track, lighting and HUD placement. No device-loss/crash errors were found in that cycle.

The candidate passed2893 import checks and the native image self-test. Runtime validation is limited to the tested workload; the standalone self-test does not exercise every experimental branch. The public patch excludes GPU timing instrumentation, texture-pointer tracing and failed shader-specialization changes.
