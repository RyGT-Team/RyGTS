# Resource reuse and shader compilation

The Native renderer reuses completed upload buffers, immutable shader-data uploads,
descriptor storage and independent graphics/compute bindings. Completed-resource
cleanup runs on a bounded worker without blocking the recorder queue handoff.

Pipelines remain cached while the driver-reported device-local memory budget has at
least 1 GiB of headroom. Under pressure, up to eight unused pipelines per second
are retired after the existing lifetime checks. This is a headroom target, not a
hard limit on textures, driver allocations or whole-process memory.

Compiler and draw-preparation fixes are unconditional production behavior. The old
SHADGT_CPU_COMPILE_MASK and SHADGT_DRAW_PREP_MASK environment variables no longer turn
them off. Shader precompilation remains controlled by its launcher checkbox and is
available with both RAM profiles. The 16 GB profile enables persistent pipeline
caching and keeps the smaller image-recycle budget; existing disabled cache settings
from that profile are repaired on Play without overwriting the precompile checkbox.

Validation covers resource lifetime/reuse, shader-data equality, descriptor keys,
pipeline bindings, range coverage and budget boundaries, plus package imports and
NVIDIA GPU image tests. Recurring gameplay stalls remain under investigation; these
changes do not establish that every frame meets 16.6 ms.
