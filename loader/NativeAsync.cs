// FEAT-GTS-267: "Native renderer async (experimental)" on the Graphics page: the native renderer
// started with the renderer switches of shadGT's PERF-GTS-368 to 403 (work moved off the recorder
// thread or made cheaper on it, all off by default in the renderer), with the values they were
// tested with together in races at Autopolis and Tokyo.
using System.Diagnostics;

namespace RyGT
{
    static class NativeAsync
    {
        /// Name and value pairs. A renderer build that does not know a switch ignores it.
        public static readonly string[,] Switches =
        {
            { "RYGT_DIRECT_NESTED_RESUME", "1" },   // PERF-GTS-383: nested command buffers resumed directly
            { "RYGT_DYNAMIC_BUFFER_OFFSETS", "1" }, // PERF-GTS-374: buffer offsets given at the bind
            { "RYGT_LONG_LIVED_SETS", "1" },        // PERF-GTS-375: descriptor sets kept across lists
            { "RYGT_AHEAD_UPLOAD_MB", "128" },      // PERF-GTS-377: draw constants copied by the workers
            { "RYGT_NATIVE_CHUNK_MB", "64" },       // upload chunks of 64 MB
            { "RYGT_NATIVE_UPLOAD_VRAM", "1" },     // PERF-GTS-457: upload memory in video memory
            { "RYGT_DRIVER_CACHE_MAX_MB", "4096" }, // the driver's shader cache limit
            { "RYGT_UPLOAD_ONCE_A_FRAME", "1" },    // PERF-GTS-384: a large texture uploads once a frame
            { "RYGT_EVICT_SCAN_SHARDS", "8" },      // PERF-GTS-385: idle-texture scan spread over frames
            { "RYGT_MIRROR_DROP_BACKOFF", "1" },    // PERF-GTS-386: rewritten vertex memory read in place longer
            { "RYGT_MIRROR_ROUND_MB", "4" },        // PERF-GTS-388: vertex mirrors widened to 4 MB
            { "RYGT_PREPARE_MISSING_PICTURES", "1" }, // PERF-GTS-403: a prepared draw kept when a texture is new
        };

        /// Sets the switches the environment does not already set (a script's own value is kept; the
        /// Advanced page's list is applied after this and wins). Returns how many were set.
        public static int Apply(ProcessStartInfo start)
        {
            int set = 0;
            for (int i = 0; i < Switches.GetLength(0); ++i)
            {
                if (!string.IsNullOrEmpty(start.EnvironmentVariables[Switches[i, 0]])) continue;
                start.EnvironmentVariables[Switches[i, 0]] = Switches[i, 1];
                ++set;
            }
            return set;
        }
    }
}
