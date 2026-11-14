# Automatic AMD compatibility

Patch 0098 includes the Native renderer compressed-image fallback that was previously present only in the local investigation build. The older texture-cache fallback alone did not cover Native rendering, so the CI package could still report missing BC6H/BC5 images.

No environment variable or launcher option is required. The active Vulkan device is queried with the actual format and image usage. If a block-compressed image rejects storage usage, the renderer retries with sampling and transfers, preserving the original compressed format and HDR content. Drivers accepting the original request retain that path. This capability check covers AMD without guessing support from a GPU generation; other vendors retain their accepted path too.

Both immediate and prepared descriptor paths refuse writable storage bindings for images created without storage usage. Unsupported required storage is not silently emulated.

Existing published AMD family/driver profiles remain automatic. Do not enable all diagnostic switches: KHR material comparisons and separate-W experiments have not established a general road/car fix. This change addresses the confirmed image allocation rejection, not every remaining material defect. Polaris through RDNA generations still need hardware validation individually.

The local sampled-only candidate previously restored the skybox on Radeon 610M. The CI package must be rebuilt with this patch before its installed binaries contain the fix.

Patch 0099 enables the validated composition and two post-processing fragment descriptor paths, plus the single-shader zero-copy 4 KiB compute prefix, automatically for Vulkan vendor ID 0x1002 (AMD). Other vendors retain the previous default. Existing SHADGT_COMPOSITION_BUFFER_DESCRIPTORS, SHADGT_POSTPROCESS_BUFFER_DESCRIPTORS and SHADGT_COMPUTE_PREFIX_DESCRIPTOR overrides remain available: 0 disables, 1 enables. These are runtime binding choices; no shader interface or shader-store revision changes. Unsupported imports still use their existing fallback checks. Broad compute gathers and the unsuccessful material experiment remain off.

## RX 6000 series and older (AMD-304/306/307/308/314)

AMD's older shader compiler (proprietary driver without LLPC in `driverInfo`: RX 6000, RX 5000, RX 400/500) gets its own fixes; RX 7000 and RX 9000 (LLPC) are unchanged.

- AMD-306: pixel shaders keep whole quads in branches (`VK_KHR_shader_quad_control` with maximal reconvergence), so mip levels in branches match the PS4. `SHADGT_QUAD_CONTROL=0/1` overrides.
- AMD-307: per-vertex reads find the driver's vertex order per triangle. `SHADGT_VERTEX_ORDER=0/1` overrides.
- AMD-308: image float min/max atomics are built from integer atomics. `SHADGT_FP32_IMAGE_ATOMICS` overrides.
- AMD-314: the KHR single-sample barycentric default is off there; `SHADGT_AMD_SINGLE_SAMPLE_BARYCENTRIC=1` turns it on.
- AMD-315: GCN P1/P2 interpolation is done by hand on the explicit-vertex path, so a `V_INTERP_MOV` no longer turns other components' interpolated reads into vertex 0. `SHADGT_MANUAL_INTERP=0/1` overrides.
- AMD-307's check uses locations 30/31 only when the vertex stage's exports leave them free (`SPI_VS_OUT_CONFIG`), and selects among the driver's raw vertices.
- These cards use shader store 8, so older stores are replaced once.

All AMD cards: AMD-316 logs one line naming the Radeon generation, the driver, the compiler (older or LLPC) and which of these fixes are on. All GPUs: FIX-217 stops a tessellation control-point stride of 0 from failing shader translation.

AMD-317 (game detection): the renderer names the Vulkan application "Gran Turismo Sport" (the engine name stays "shadPS4 Vulkan"); Set up gives GTSport.exe version information (product and description "Gran Turismo Sport"); and the first Play on a PC with AMD Software adds GTSport.exe to its game list once (`user/amd-software-entry.txt` records it; `RYGT_AMD_SOFTWARE=0` skips it). The game entry applies in window, borderless and fullscreen modes alike. Folders set up before this keep their GTSport.exe unchanged (changing it would trigger a rebuild); Set up adds the version information the next time it builds the exe.

FIX-218 (all GPUs): Alt+Tab no longer ends the game with "Device lost during submit". Recreating the swapchain after a failed acquire or a window size change now holds the submit lock, as the recreate after a failed present already did, and a lost surface or lost exclusive fullscreen recreates the swapchain instead of stopping. A lost surface is replaced with a new one, whose formats, present modes and capabilities are queried again.

`tools/amd/Add-To-AMD-Software.ps1` adds GTSport.exe to AMD Software: Adrenalin Edition's game list (overlay, metrics, per-game settings). It closes the Adrenalin window, keeps a backup of the list as `gmdb.blb.rygt-backup` and reopens Adrenalin.
