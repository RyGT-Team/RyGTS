# Experimental low-FPS pacing workaround

Patch 0097 is off by default. Set `SHADGT_FG_LOW_FPS_PACING=1` in the launcher's environment before starting the game to test it.

The bundled Vulkan SDK clears its ten-sample pacing history whenever an interpolation interval exceeds 100 ms. Repeated slow frames can prevent that history from filling, reducing the intended spacing between generated and native frames to zero. This is a timing-history reset, not a hard generation cutoff at 24 FPS.

The diagnostic workaround raises that dedicated timeout to 500 ms in process memory before any FFx contexts or threads are created. It verifies DLL size, complete file SHA-256, and loaded constant-load/comparison signatures. Supported DLL SHA-256: `A1624CC4238FEF046F30C4D80CE3F47BE63FC5F5373F49E3EE9EDB9960F54C78`. Unknown or altered DLLs are skipped. Four data bytes change; no on-disk vendor DLL is changed, and read-only protection is restored. Explicit scene/pause reset paths remain.

Validation: isolated actual-DLL tests cover opt-out, missing file, same-size wrong hash, exact application, protection restoration and repeat refusal; renderer build, 2,893 imports and image self-test pass. The installed run confirms application, but a controlled performance benefit is NOT yet established. Keep experimental until focused display-spacing and rendering comparisons pass. A vendor source correction is preferable for a permanent implementation.

The same SDK setter applies safety margin and variance, then incorrectly reports an error. It ignores the spin/sleep fields. Thus a runtime error from this particular setter does not prove the settings were rejected. The ineffective spin/sleep selector experiment is excluded.

Official source: https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/54fbaafdc34716811751bea5032700e78f5a0f33/sdk/src/backends/vk/FrameInterpolationSwapchain/FrameInterpolationSwapchainVK.cpp
