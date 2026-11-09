#include "prx/libc/include/GuestAllocations.hpp"
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <limits>
#include <random>
#include <new>
#include <stdexcept>
#include <string>
#include <vector>

static std::atomic<int> allocationCountdown{-1};
void* operator new(std::size_t size) {
    int current = allocationCountdown.load();
    if (current >= 0 && allocationCountdown.fetch_sub(1) == 0) throw std::bad_alloc();
    if (void* p = std::malloc(size ? size : 1)) return p;
    throw std::bad_alloc();
}
void operator delete(void* p) noexcept { std::free(p); }
void operator delete(void* p, std::size_t) noexcept { std::free(p); }

namespace {
using GuestAllocations::Range;
using Address = std::uintptr_t;
std::uint64_t checks = 0;
GuestAllocations::Lease* pinnedLease = nullptr;
unsigned pinWaits = 0;
bool ReleaseTestLease() { ++pinWaits; pinnedLease->clear(); return true; }

void Require(bool value, const char* message) {
    if (!value) throw std::runtime_error(message);
}

// Keep the original full scans as an independent behavioral reference, including
// their zero-length, overflow, and exception-message behavior.
bool OriginalOverlaps(const std::vector<Range>& ranges, Address address, std::size_t bytes) {
    for (const auto& range : ranges) {
        if (range.address >= address + bytes) break;
        if (range.address + range.bytes > address) return true;
    }
    return false;
}

void OriginalAvailable(const std::vector<Range>& ranges, Address address, std::size_t bytes) {
    Require(address != 0 && bytes != 0 && bytes <= std::numeric_limits<std::uint64_t>::max() - address,
            "invalid fixed guest mapping");
    for (const auto& range : ranges) {
        if (range.address >= address + bytes) break;
        if (range.address + range.bytes > address) {
            char message[160];
            std::snprintf(message, sizeof(message), "fixed mapping 0x%llx+0x%llx overlaps a registered guest allocation 0x%llx+0x%llx",
                          static_cast<unsigned long long>(address), static_cast<unsigned long long>(bytes),
                          static_cast<unsigned long long>(range.address), static_cast<unsigned long long>(range.bytes));
            throw std::runtime_error(message);
        }
    }
}

bool OriginalCovers(const std::vector<Range>& ranges, Address address, std::size_t bytes) {
    Require(address != 0 && bytes != 0 && bytes <= std::numeric_limits<std::uint64_t>::max() - address,
            "invalid guest allocation range");
    auto cursor = address;
    for (const auto& range : ranges) {
        const auto finish = range.address + range.bytes;
        if (finish <= cursor) continue;
        if (range.address > cursor || !range.releasable) return false;
        cursor = finish;
        if (cursor >= address + bytes) return true;
    }
    return false;
}

struct Result {
    bool value;
    std::string error;
    bool operator==(const Result&) const = default;
};

template<class Function>
Result Evaluate(Function&& function) {
    try { return {function(), {}}; }
    catch (const std::runtime_error& error) { return {false, error.what()}; }
}

void Compare(const std::vector<Range>& ranges, const GuestAllocations::Mutation& mutation,
             Address address, std::size_t bytes) {
    const auto pointer = reinterpret_cast<const void*>(address);
    Require(Evaluate([&] { return OriginalOverlaps(ranges, address, bytes); }) ==
            Evaluate([&] { return mutation.Overlaps(pointer, bytes); }), "Overlaps differs from original scan");
    Require(Evaluate([&] { OriginalAvailable(ranges, address, bytes); return true; }) ==
            Evaluate([&] { mutation.RequireAvailable(pointer, bytes); return true; }), "RequireAvailable differs from original scan");
    Require(Evaluate([&] { return OriginalCovers(ranges, address, bytes); }) ==
            Evaluate([&] { return mutation.Covers(pointer, bytes); }), "Covers differs from original scan");
    checks += 3;
}

Range MakeRange(Address address, std::size_t bytes) {
    return {address, bytes, true, true, address, bytes, true};
}

void Exercise(const std::vector<Range>& ranges, std::mt19937_64& random) {
    GuestAllocations::Mutation mutation;
    for (const auto& range : ranges)
        mutation.Add(reinterpret_cast<void*>(range.address), range.bytes, true, true);
    const auto limit = std::numeric_limits<Address>::max();
    for (auto address : {Address{0}, Address{1}, Address{0x1000}, limit - 1, limit})
        for (auto bytes : {std::size_t{0}, std::size_t{1}, std::size_t{64}, std::size_t{0x10000}, std::numeric_limits<std::size_t>::max()})
            Compare(ranges, mutation, address, bytes);
    for (const auto& range : ranges) {
        for (auto address : {range.address - 1, range.address, range.address + 1,
                             range.address + range.bytes, range.address + range.bytes + 1})
            for (auto bytes : {std::size_t{0}, std::size_t{1}, range.bytes, range.bytes + 1})
                Compare(ranges, mutation, address, bytes);
    }
    for (unsigned i = 0; i < 1600; ++i) {
        const auto address = (i % 3 == 0) ? random() : 1 + random() % 0x40000;
        const auto bytes = (i % 5 == 0) ? random() : random() % 0x2000;
        Compare(ranges, mutation, address, bytes);
    }
    for (const auto& range : ranges) mutation.Remove(reinterpret_cast<const void*>(range.address));
}

void CheckSplitRanges() {
    const Address base = 0x100000;
    GuestAllocations::Mutation mutation;
    mutation.Add(reinterpret_cast<void*>(base), 0x3000, true, true);
    mutation.Protect(reinterpret_cast<void*>(base + 0x1000), 0x1000, true, false, [] {});
    auto ranges = std::vector{MakeRange(base, 0x1000), MakeRange(base + 0x1000, 0x1000), MakeRange(base + 0x2000, 0x1000)};
    for (auto address : {base - 1, base, base + 0x800, base + 0x1000, base + 0x2000, base + 0x3000})
        for (auto bytes : {std::size_t{0}, std::size_t{1}, std::size_t{0x1000}, std::size_t{0x3000}})
            Compare(ranges, mutation, address, bytes);
    mutation.Unmap(reinterpret_cast<void*>(base + 0x1000), 0x1000, [](const void*, std::size_t, const void*, bool) {});
    ranges.erase(ranges.begin() + 1);
    Compare(ranges, mutation, base, 0x3000);
    Compare(ranges, mutation, base + 0x1000, 0x1000);
    mutation.Remove(reinterpret_cast<void*>(base));
}

void CheckTransactions() {
    constexpr Address base = 0x20000000;
    auto ptr = [](Address a) { return reinterpret_cast<void*>(a); };
    {
        GuestAllocations::Mutation m;
        m.Add(ptr(base), 0x3000, true, true);
        m.Add(ptr(base + 0x3000), 0x1000, true, true);
        bool called = false;
        try { m.Protect(ptr(base + 0x1000), 0x1000, true, false, [&] { called = true; throw std::runtime_error("apply"); }); }
        catch (const std::runtime_error&) {}
        Require(called && m.Covers(ptr(base), 0x4000) && m.Find(ptr(base)).writable,
                "throwing protect changed registry or permissions");
        m.Protect(ptr(base + 0x1000), 0x1000, true, false, [] {});
        unsigned calls = 0;
        try { m.Unmap(ptr(base + 0x1000), 0x3000, [&](const void*, std::size_t, const void*, bool last) {
            ++calls; if (calls == 1) Require(!last, "partial allocation marked last");
            else { Require(last, "whole second allocation not marked last"); throw std::runtime_error("second piece"); }
        }); } catch (const std::runtime_error&) {}
        Require(calls == 2 && !m.Overlaps(ptr(base + 0x1000), 0x2000) && m.Covers(ptr(base + 0x3000), 0x1000),
                "unmap did not preserve per-piece commit");
        m.Remove(ptr(base)); m.Remove(ptr(base + 0x3000));
    }
    {
        GuestAllocations::Mutation m;
        m.Add(ptr(base), 0x3000, true, true);
        bool called = false;
        const std::function<void()> apply = [&] { called = true; };
        allocationCountdown = 1; // recordChange grows first; the staged plan then allocates
        bool failed = false;
        try { m.Protect(ptr(base + 0x1000), 0x1000, true, false, apply); }
        catch (const std::bad_alloc&) { failed = true; }
        allocationCountdown = -1;
        Require(failed && !called && m.Covers(ptr(base), 0x3000), "allocation failure changed registry or reached apply");
        m.Remove(ptr(base));
    }
    {
        GuestAllocations::Mutation m;
        m.Add(ptr(base), 0x3000, true, true);
        m.Protect(ptr(base + 0x1000), 0x1000, true, false, [] { allocationCountdown = 0; });
        allocationCountdown = -1;
        Require(m.Covers(ptr(base), 0x3000), "allocation-free protect commit lost a range");
        m.Unmap(ptr(base + 0x1000), 0x1000, [](const void*, std::size_t, const void*, bool) { allocationCountdown = 0; });
        allocationCountdown = -1;
        Require(!m.Overlaps(ptr(base + 0x1000), 0x1000), "allocation-free unmap commit retained a range");
        m.Remove(ptr(base));
    }
    {
        GuestAllocations::Mutation m;
        m.Add(ptr(base), 0x1000, true, true);
        m.Add(ptr(base + 0x2000), 0x1000, true, true);
        bool called = false;
        try { m.Protect(ptr(base), 0x1000, false, true, [&] { called = true; }); }
        catch (const std::runtime_error&) {}
        Require(!called && m.Covers(ptr(base), 0x1000), "invalid protection reached apply");
        try { m.Protect(ptr(std::numeric_limits<Address>::max() - 1), 8, true, false, [&] { called = true; }); }
        catch (const std::runtime_error&) {}
        Require(!called, "overflow reached apply");
        try { m.Protect(ptr(base), 0x3000, true, false, [&] { called = true; }); }
        catch (const std::runtime_error&) {}
        Require(!called && m.Covers(ptr(base), 0x1000) && m.Covers(ptr(base + 0x2000), 0x1000), "hole reached apply");
        m.Remove(ptr(base)); m.Remove(ptr(base + 0x2000));
    }
    {
        // Zero-length entries at an allocation boundary must not be silently erased.
        GuestAllocations::Mutation m;
        m.Add(ptr(base), 0x1000, true, true);
        m.Add(ptr(base + 0x1000), 0, true, true);
        m.Add(ptr(base + 0x2000), 0x1000, true, true);
        bool called = false;
        try { m.Protect(ptr(base), 0x3000, true, false, [&] { called = true; }); }
        catch (const std::runtime_error&) {}
        Require(!called && m.Covers(ptr(base), 0x1000) && m.Covers(ptr(base + 0x2000), 0x1000), "zero-size boundary hid a hole");
        m.Remove(ptr(base)); m.Remove(ptr(base + 0x1000)); m.Remove(ptr(base + 0x2000));
    }
    {
        { GuestAllocations::Mutation m; m.Add(ptr(base), 0x1000, true, true); m.Add(ptr(base + 0x2000), 0x1000, true, true); }
        auto lease = GuestAllocations::GuestAllocationsAcquire_nid_postfix();
        Require(lease.size() == 2, "expected two snapshot ranges");
        const auto* unchanged = lease[1].get();
        pinnedLease = &lease; pinWaits = 0;
        GuestAllocations::GuestAllocationsSetPinWaiter_nid_postfix(ReleaseTestLease);
        { GuestAllocations::Mutation m; m.Protect(ptr(base), 0x1000, true, false, [] {}); }
        GuestAllocations::GuestAllocationsSetPinWaiter_nid_postfix(nullptr);
        Require(pinWaits != 0, "pinned mutation did not wait");
        auto after = GuestAllocations::GuestAllocationsAcquire_nid_postfix();
        Require(after.size() == 2 && after[1].get() == unchanged, "unaffected snapshot identity changed");
        // Release the snapshot before removing its ranges.
        after.clear();
        { GuestAllocations::Mutation m; m.Remove(ptr(base)); m.Remove(ptr(base + 0x2000)); }
    }
}

void CheckMainImage() {
#ifdef _WIN32
    { GuestAllocations::Mutation mutation; mutation.RegisterMainImage(); }
    std::vector<Range> ranges;
    {
        const auto lease = GuestAllocations::GuestAllocationsAcquire_nid_postfix();
        for (const auto& range : lease) ranges.push_back(*range);
    }
    // A real executable supplies non-releasable registry ranges without a test-only API.
    Require(!ranges.empty() && !ranges.front().releasable, "main image ranges missing");
    GuestAllocations::Mutation mutation;
    for (const auto& range : ranges) {
        Compare(ranges, mutation, range.address, 1);
        Compare(ranges, mutation, range.address + 1, 0);
        Compare(ranges, mutation, range.address, range.bytes);
    }
#endif
}

void Benchmark() {
    GuestAllocations::Mutation mutation;
    constexpr Address base = 0x100000;
    constexpr unsigned entries = 32768;
    for (unsigned i = 0; i < entries; ++i)
        mutation.Add(reinterpret_cast<void*>(base + i * 0x100), 0x80, true, true);
    unsigned sum = 0;
    const auto start = std::chrono::steady_clock::now();
    for (unsigned i = 0; i < 2000; ++i) {
        const auto address = base + (entries - 1 - (i % 64)) * 0x100;
        const auto* pointer = reinterpret_cast<const void*>(address);
        sum += mutation.Overlaps(pointer, 0x20);
        sum += mutation.Covers(pointer, 0x20);
        mutation.RequireAvailable(reinterpret_cast<const void*>(address + 0x80), 0x20);
    }
    const auto elapsed = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
    Require(sum == 4000, "benchmark result mismatch");
    std::printf("registry_lookup_benchmark entries=%u queries=6000 ms=%.6f\n", entries, elapsed);
    for (unsigned i = 0; i < entries; ++i) mutation.Remove(reinterpret_cast<void*>(base + i * 0x100));
}

void BenchmarkTransactions(unsigned entries) {
    GuestAllocations::Mutation mutation;
    constexpr Address base = 0x40000000;
    for (unsigned i = 0; i < entries; ++i)
        mutation.Add(reinterpret_cast<void*>(base + i * 0x1000), 0x1000, true, true);
    const auto start = std::chrono::steady_clock::now();
    for (unsigned i = 0; i < 128; ++i)
        mutation.Protect(reinterpret_cast<void*>(base + (entries / 2) * 0x1000), 0x1000, true, (i & 1) != 0, [] {});
    const auto elapsed = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
    std::printf("guest_range_transaction_benchmark entries=%u operations=128 ms=%.3f\n", entries, elapsed);
    for (unsigned i = 0; i < entries; ++i)
        mutation.Remove(reinterpret_cast<void*>(base + i * 0x1000));
}
}

int main() {
    try {
        std::mt19937_64 random(0x5265475453);
        Exercise({}, random);
        Exercise({MakeRange(0x1000, 0), MakeRange(0x1100, 0x100), MakeRange(0x1200, 0),
                  MakeRange(0x1300, 0x100), MakeRange(0x1400, 0x100)}, random);
        const auto limit = std::numeric_limits<Address>::max();
        Exercise({MakeRange(0x1000, 0x100), MakeRange(limit - 0x100, 0x100)}, random);
        for (unsigned layout = 0; layout < 64; ++layout) {
            std::vector<Range> ranges;
            Address cursor = 0x1000;
            for (unsigned i = 0; i < 96; ++i) {
                const auto bytes = random() % 0x100;
                ranges.push_back(MakeRange(cursor, bytes));
                cursor += bytes + 1 + random() % 0x100;
            }
            Exercise(ranges, random);
        }
        CheckSplitRanges();
        CheckTransactions();
        Benchmark();
        BenchmarkTransactions(1024);
        BenchmarkTransactions(32768);
        CheckMainImage();
        std::printf("guest_allocation_lookup checks=%llu passed\n", static_cast<unsigned long long>(checks));
        return 0;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "guest_allocation_lookup failed: %s\n", error.what());
        return 1;
    }
}
