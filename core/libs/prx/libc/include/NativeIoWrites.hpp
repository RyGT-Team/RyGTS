#pragma once
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

inline bool MarkNativeGuestWrite(void* buffer, std::size_t bytes) {
#ifdef _WIN32
    static const bool enabled = [] { const char* v = std::getenv("RYGT_NATIVE_SNAPSHOT_LEASE"); return v && v[0] == '1' && v[1] == '\0'; }();
    if (!enabled || !buffer || !bytes) return true;
    if (reinterpret_cast<std::uint64_t>(buffer) >= (std::uint64_t{1} << 40)) return true;
    using RangeFunction = void (*)(std::uint64_t, std::uint64_t);
    HMODULE libc = GetModuleHandleA("libc.prx");
    if (!libc) return false;
    auto mark = reinterpret_cast<RangeFunction>(reinterpret_cast<void*>(GetProcAddress(libc, "RytySnapshotMarkUnsafe")));
    // NID patching renames non-preserved runtime exports in production PRXs.
    if (!mark) mark = reinterpret_cast<RangeFunction>(reinterpret_cast<void*>(GetProcAddress(libc, "hZxVJ2Jdlec")));
    if (!mark) return false;
    mark(reinterpret_cast<std::uint64_t>(buffer), bytes);
#else
    (void)buffer;
    (void)bytes;
#endif
    return true;
}

inline bool PrepareLibcGuestWrite(void* buffer, std::size_t bytes) {
    if (!MarkNativeGuestWrite(buffer, bytes)) return false;
#ifdef _WIN32
    static const bool enabled = [] { const char* v = std::getenv("RYGT_NATIVE_SNAPSHOT_LEASE"); return v && v[0] == '1' && v[1] == '\0'; }();
    if (!enabled || !buffer || !bytes || reinterpret_cast<std::uint64_t>(buffer) >= (std::uint64_t{1} << 40)) return true;
    using RangeFunction = void (*)(std::uint64_t, std::uint64_t);
    if (HMODULE renderer = GetModuleHandleA("libSceVideoOut.prx")) {
        auto invalidate = reinterpret_cast<RangeFunction>(reinterpret_cast<void*>(GetProcAddress(renderer, "RytyGpuInvalidateMemory")));
        if (invalidate) invalidate(reinterpret_cast<std::uint64_t>(buffer), bytes);
    }
#else
    (void)buffer;
    (void)bytes;
#endif
    return true;
}
