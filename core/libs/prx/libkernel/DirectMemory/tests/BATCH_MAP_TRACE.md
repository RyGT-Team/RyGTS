Set `RYGT_BATCH_MAP_TRACE=1` before process start and create `logs/batch-map-<pid>.capture`
to enable capture at subsequent batch entries. Up to 256 nonempty batches per process are reported to
stderr; removing the marker prevents admission of later batches. No report is made for an
empty batch or an invalid batch rejected before iteration. Each line has the PID, TID, ordinal, QPC start/end
and frequency, requested entry count, batch flags, attempted and processed counts, return
error and exception flag. The six operation counters cover operations 0 through 4 and other.
Bytes and size buckets include attempted entries whose operation reads a length; total bytes
saturate at UINT64_MAX. A failed entry is attempted but not processed. Direct ticks and max
are wall time around DoMapDirect, including failures, mapping-transition lock waits, nested
renderer notifications and scheduler delays. They do not isolate OS mapping or renderer time.
No virtual-address adjacency or physical contiguity is measured. The marker check and timing
are diagnostic overhead; compare only appropriately controlled runs.

Each captured batch also emits four `batch-map-stage` lines keyed by PID and ordinal. Stage 0
measures transition-lock acquisition; 1 the pre-map renderer unmap notification when present;
2 fixed-overlap cleanup and mapping/registry work; 3 the post-map renderer map notification.
Each contains attempts, completed returns, total ticks, and maximum ticks, using the batch QPC
frequency. Interrupted attempts contribute elapsed time but not completed count. Stage 2 can
include nested unmap notifications, so renderer spans overlap it and must not be added to it.
Nested direct calls are suppressed to prevent double counting. These are diagnostic wall times.
Completed means the instrumented scope returned, not that mapping necessarily succeeded.
The batch and stage log writes occur after the recorded batch end and may delay later work.
