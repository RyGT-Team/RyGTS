#pragma once
#include <atomic>
#include <cstdint>
#include <limits>
#include <array>
#include <exception>
#include <cstdlib>

// Process-local mutation gate. The renderer treats absence or saturation as unsupported.
namespace SnapshotBacking {
// Upper 48 bits are epoch, lower 16 bits active count. All-ones is permanently unusable.
inline std::atomic<std::uint64_t> state{std::uint64_t{1} << 16};
inline constexpr std::uint64_t Invalid = std::numeric_limits<std::uint64_t>::max();
#ifndef NDEBUG
inline bool TraceEnabled() noexcept {
    static const bool enabled = [] { const char* v = std::getenv("RYGT_SNAPSHOT_POISON_TRACE"); return v && v[0] == '1' && v[1] == '\0'; }();
    return enabled;
}
#endif
inline bool TrackingEnabled() noexcept {
    static const bool enabled = [] {
#ifndef NDEBUG
        (void)TraceEnabled();
#endif
        const char* v = std::getenv("RYGT_NATIVE_SNAPSHOT_LEASE"); return v && v[0] == '1' && v[1] == '\0'; }();
    return enabled;
}
inline constexpr std::uint64_t AddressLimit = std::uint64_t{1} << 40;
inline std::array<std::atomic<std::uint64_t>, (AddressLimit >> 16) / 64> unsafe{};
#ifndef NDEBUG
inline std::array<std::atomic<std::uint8_t>, (AddressLimit >> 16)> first_cause{};
#endif
inline bool ValidRange(std::uint64_t address, std::uint64_t size) noexcept {
    return size && address < AddressLimit && size <= AddressLimit - address;
}
inline void MarkUnsafe(std::uint64_t address, std::uint64_t size, std::uint32_t cause = 32) noexcept {
    if (!TrackingEnabled()) return;
    if (!ValidRange(address, size)) { state.store(Invalid, std::memory_order_release); return; }
    const auto first = address >> 16, last = (address + size - 1) >> 16;
    for (auto page = first; page <= last; ++page) {
        const auto mask = std::uint64_t{1} << (page % 64);
        const auto previous = unsafe[page / 64].fetch_or(mask, std::memory_order_seq_cst);
#ifndef NDEBUG
        if (!(previous & mask) && TraceEnabled()) first_cause[page].store(static_cast<std::uint8_t>(cause), std::memory_order_release);
#else
        (void)previous; (void)cause;
#endif
    }
}
#ifndef NDEBUG
inline std::uint32_t UnsafeCauses(std::uint64_t address, std::uint64_t size) noexcept {
    if (!TrackingEnabled() || !TraceEnabled() || !ValidRange(address, size)) return 0;
    std::uint32_t causes = 0;
    for (auto page = address >> 16, last = (address + size - 1) >> 16; page <= last; ++page)
        causes |= first_cause[page].load(std::memory_order_acquire);
    return causes;
}
#endif
inline bool RangeSafe(std::uint64_t address, std::uint64_t size) noexcept {
    if (!TrackingEnabled() || !ValidRange(address, size) || state.load(std::memory_order_acquire) == Invalid) return false;
    const auto first = address >> 16, last = (address + size - 1) >> 16;
    for (auto page = first; page <= last; ++page)
        if (unsafe[page / 64].load(std::memory_order_seq_cst) & (std::uint64_t{1} << (page % 64))) return false;
    return true;
}
inline void Change(bool entering) noexcept {
    auto old = state.load(std::memory_order_acquire);
    for (;;) {
        if (old == Invalid) return;
        const auto count = old & 0xffffu;
        const auto epoch = old >> 16;
        const bool overflow = epoch >= ((std::uint64_t{1} << 48) - 1) ||
                              (entering && count == 0xffffu) || (!entering && count == 0);
        const auto next = overflow ? Invalid : ((epoch + 1) << 16) | (entering ? count + 1 : count - 1);
        if (state.compare_exchange_weak(old, next, std::memory_order_acq_rel, std::memory_order_acquire)) return;
    }
}
struct Mutation {
    int exceptions;
    bool enabled;
    Mutation() noexcept : exceptions(0), enabled(TrackingEnabled()) { if (enabled) { exceptions = std::uncaught_exceptions(); Change(true); } }
    ~Mutation() {
        if (!enabled) return;
        if (std::uncaught_exceptions() > exceptions) state.store(Invalid, std::memory_order_release);
        else Change(false);
    }
    Mutation(const Mutation&) = delete;
    Mutation& operator=(const Mutation&) = delete;
};
}
