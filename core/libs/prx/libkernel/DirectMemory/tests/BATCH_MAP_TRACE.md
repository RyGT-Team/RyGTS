Set `RYGT_BATCH_MAP_TRACE=1` before process start and create `logs/batch-map-<pid>.capture`
to enable capture at subsequent batch entries. Up to 256 batches per process are reported to
stderr; removing the marker prevents admission of later batches. No report is made for an
invalid batch rejected before iteration. Each line has the PID, TID, ordinal, QPC start/end
and frequency, requested entry count, batch flags, attempted and processed counts, return
error and exception flag. The six operation counters cover operations 0 through 4 and other.
Bytes and size buckets include attempted entries whose operation reads a length; total bytes
saturate at UINT64_MAX. A failed entry is attempted but not processed. Direct ticks and max
are wall time around DoMapDirect, including failures, mapping-transition lock waits, nested
renderer notifications and scheduler delays. They do not isolate OS mapping or renderer time.
No virtual-address adjacency or physical contiguity is measured. The marker check and timing
are diagnostic overhead; compare only appropriately controlled runs.
