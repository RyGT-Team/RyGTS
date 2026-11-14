# Optional bounded FG timing

Patch 0096 enables diagnostics only with `SHADGT_FG_GPU_TIMING=1` at startup. It reserves sixteen non-reused timestamp slots per measured GPU stage: preparation, interpolation, input snapshots and HUD-reference conversion. Results are polled without WAIT. Another bounded buffer records 128 acquire/present host calls, then logs their timestamps and durations. Queue timestamp support is required; teardown follows idle/context shutdown.

Host-call timestamps do not measure scanout spacing. Use a separate presentation trace for that; no external capture executable or service is bundled here. GPU stage samples and a later presentation capture are not automatically the same time window. A focused scene with compilation finished is required for useful comparisons; discard incomplete/zero samples and compare uninstrumented pacing separately.

Validation: renderer build, 2,893 imports, image self-test and live bounded results. Diagnostics remain off by default and introduce no per-frame waits for query results.
