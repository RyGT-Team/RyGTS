# Bounded compute constant-prefix descriptor

Apply shadGT-patches/0087-native-bounded-compute-prefix-descriptor.patch after the existing patches through0086 and rebuild ryty_gpu. Existing prefix-mode shader support comes from0066; cached shaders remain compatible.

Opt-in before game startup:

```powershell
$env:SHADGT_COMPUTE_PREFIX_DESCRIPTOR = '1'
```

Unset it or set it to0 to restore the original compute path. The fragment descriptor switches documented in FRAGMENT-BUFFER-DESCRIPTORS.md remain independent.

For compute program d0af5a54 only, read-only ReadConstPointer buffers of at least4KiB can bind their first4KiB through an already imported buffer descriptor. Descriptor offsets honor the device's storage-buffer alignment. The existing shader prefix check sends reads beyond that prefix back through the original page-table path, preserving the full guest-buffer bounds. If no suitable imported view exists, the original path remains in use. Written buffers and synchronization are unchanged.

This introduces no new allocation, snapshot, gather or copy. It does not enable the rejected full-range compute-descriptor experiment or material experiment, neither of which is included in this patch. No launcher option is added.

Local GT Sport AMD integrated-GPU testing at960x540 scene /1920x1080 output, with the three validated fragment improvements enabled:15 bounded samples measured compute mean3.156 ->1.956ms, median3.144 ->1.955ms. Sequential uninstrumented foreground samples measured16.2 ->17.0FPS; these are local observations, not a repeated crossover benchmark or a universal speedup. Preparing loaded normally, track ->3D menu/showroom ->track rendering was user-verified, and logs reported zero gathered ranges and no crash/device-loss errors during the validation cycle.

The candidate passed2893 import checks and the native Vulkan image self-test; the standalone self-test is not proof of AMD runtime behavior. Keep this opt-in until broader hardware coverage exists.
