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
inline bool TrackingEnabled() noexcept {
    static const bool enabled = [] { const char* v = std::getenv("RYGT_NATIVE_SNAPSHOT_LEASE"); return v && v[0] == '1' && v[1] == '\0'; }();
    return enabled;
}
inline constexpr std::uint64_t AddressLimit = std::uint64_t{1} << 40;
inline std::array<std::atomic<std::uint64_t>, (AddressLimit >> 16) / 64> unsafe{};
inline bool ValidRange(std::uint64_t address, std::uint64_t size) noexcept {
    return size && address < AddressLimit && size <= AddressLimit - address;
}
inline void MarkUnsafe(std::uint64_t address, std::uint64_t size) noexcept {
    if (!TrackingEnabled()) return;
    if (!ValidRange(address, size)) { state.store(Invalid, std::memory_order_release); return; }
    const auto first = address >> 16, last = (address + size - 1) >> 16;
    for (auto page = first; page <= last; ++page)
        unsafe[page / 64].fetch_or(std::uint64_t{1} << (page % 64), std::memory_order_seq_cst);
}
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
