# Native fragment buffer descriptors

Apply `shadGT-patches/0085-native-fragment-buffer-descriptors.patch` after the existing renderer patches through 0084, then rebuild `ryty_gpu`.

The Native renderer can bind constants through existing buffer descriptors instead of repeatedly translating guest addresses through the GPU page table. Both recorder-side and prepared draws use the same selection. Existing mapping checks, alignment offsets, resource ownership and synchronization remain in effect. Original shader binaries remain compatible.

These changes remain opt-in. Set both environment variables before starting the game:

```powershell
$env:SHADGT_COMPOSITION_BUFFER_DESCRIPTORS = '1'
$env:SHADGT_POSTPROCESS_BUFFER_DESCRIPTORS = '1'
```

The first selects fragment program `b33ec4df`; the second selects read-only buffers in fragment programs `184b619c` and `7cd92fb8`. Other shaders and compute stages retain existing behavior. Unset the variables or set them to `0` to restore the original paths. No launcher option is added by this patch.

Local GT Sport validation on AMD integrated graphics, with experimental 960x540 scenes and 1920x1080 output:

| Measured GPU scope | Before | After |
| --- | ---: | ---: |
| Composition | 9.40 ms | 4.67 ms |
| Fragment 184b619c | 3.06 ms | 1.53 ms |
| Fragment 7cd92fb8 | 2.20 ms | 0.68 ms |

Each comparison used 15 bounded GPU samples. Sequential uninstrumented foreground samples measured 14.0 FPS initially, 14.75 FPS with composition enabled, and 15.8 FPS with all three passes enabled. These are local measurements, not a repeated crossover benchmark or a prediction for other GPUs. Timestamp instrumentation can affect scheduling. No shader compilation occurred during the recorded pacing checks.

The user verified paused-track rendering and track -> 3D menu/showroom -> track cycles after each candidate. Import validation (2,893 bindings) and the native Vulkan image self-test passed. The image self-test alone does not validate AMD runtime behavior.

The unverified material-shader experiment, GPU profiler, experimental resolution changes and shader-cleanup experiment are excluded from this patch.
