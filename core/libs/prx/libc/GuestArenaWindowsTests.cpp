#include "prx/libc/include/GuestArena.hpp"
#include "prx/libc/include/WindowsMappings.hpp"
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <stdexcept>

namespace {
void Require(bool ok, const char* message) {
    if (!ok) throw std::runtime_error(message);
}

MEMORY_BASIC_INFORMATION Query(const void* address) {
    MEMORY_BASIC_INFORMATION info{};
    Require(VirtualQuery(address, &info, sizeof(info)) == sizeof(info), "VirtualQuery failed");
    return info;
}
}

int main() {
    using namespace GuestArena;
    constexpr std::size_t Page = 0x4000;
    void* driverBase = nullptr;
    void* largeHost = nullptr;
    constexpr std::size_t DriverBytes = 0xc00000000ull;
    constexpr std::size_t LargeHostBytes = 0x4000000000ull;
    try {
        // An unconstrained 256 GiB AMD reservation must not occupy guest addresses.
        largeHost = VirtualAlloc(nullptr, LargeHostBytes, MEM_RESERVE, PAGE_NOACCESS);
        Require(largeHost && reinterpret_cast<std::uintptr_t>(largeHost) >= 0xfc00000000ull,
                "large host reservation entered the guest mapping windows");
        // NVIDIA needs 48 GiB of low virtual addresses for its interop reservations.
        driverBase = VirtualAlloc(reinterpret_cast<void*>(0xe000000000ull), DriverBytes, MEM_RESERVE, PAGE_NOACCESS);
        Require(driverBase != nullptr, "guest arena left no low-address interop space");
        auto* race = reinterpret_cast<std::uint8_t*>(0x29000e0000ull);
        Require(Query(race).State == MEM_RESERVE, "race fixed mapping window was not protected");
        GuestArenaMarkUsed_nid_postfix(race, 0x10000);
        GuestArenaCommit_nid_postfix(race, 0x10000, PAGE_READWRITE, Page);
        race[0] = 9;
        GuestArenaReset_nid_postfix(race, 0x10000);
        GuestArenaRelease_nid_postfix(race, 0x10000);
        auto* startup = reinterpret_cast<std::uint8_t*>(0x1020000000ull);
        Require(Query(startup).State == MEM_RESERVE, "startup fixed mapping window was not protected");
        GuestArenaMarkUsed_nid_postfix(startup, 0x200000);
        GuestArenaCommit_nid_postfix(startup, 0x200000, PAGE_READWRITE, Page);
        startup[0] = 7;
        GuestArenaReset_nid_postfix(startup, 0x200000);
        GuestArenaRelease_nid_postfix(startup, 0x200000);

        auto* first = static_cast<std::uint8_t*>(GuestArenaAllocate_nid_postfix(Page, Page));
        auto* second = static_cast<std::uint8_t*>(GuestArenaAllocate_nid_postfix(Page, Page));
        Require(second == first + Page, "sub-granularity guest allocations stopped packing together");
        GuestArenaCommit_nid_postfix(first, Page, PAGE_READWRITE, Page);
        GuestArenaCommit_nid_postfix(second, Page, PAGE_READWRITE, Page);
        first[0] = 11;
        second[0] = 22;
        GuestArenaReset_nid_postfix(first, Page);
        GuestArenaRelease_nid_postfix(first, Page);
        Require(second[0] == 22, "reset damaged a neighboring guest mapping");
        Require(GuestArenaAllocate_nid_postfix(Page, Page) == first, "released guest range was not reused");
        GuestArenaCommit_nid_postfix(first, Page, PAGE_READWRITE, Page);
        first[0] = 33;

        const auto driverAddress = reinterpret_cast<std::uintptr_t>(largeHost);
        auto* large = WindowsMappings::Get().FindAndReserve(driverAddress - 0x10000, driverAddress + LargeHostBytes + 0x200000000ull,
                                                          0x200000000ull, 0x10000);
        Require(large && reinterpret_cast<std::uintptr_t>(large) >= driverAddress + LargeHostBytes,
                "reservation search did not skip foreign host memory");
        Require(Query(driverBase).AllocationBase == driverBase, "driver reservation was changed");

        bool rejected = false;
        try { GuestArenaMarkUsed_nid_postfix(driverBase, Page); }
        catch (const std::runtime_error&) { rejected = true; }
        Require(rejected, "fixed guest allocation accepted a host reservation");
        rejected = false;
        try { GuestArenaMarkUsed_nid_postfix(reinterpret_cast<void*>(0xec00000000ull), Page); }
        catch (const std::runtime_error&) { rejected = true; }
        Require(rejected, "fixed guest allocation stole unused interop address space");
        rejected = false;
        try { GuestArenaReset_nid_postfix(driverBase, Page); }
        catch (const std::runtime_error&) { rejected = true; }
        Require(rejected && Query(driverBase).AllocationBase == driverBase,
                "guest reset changed a host reservation");

        auto* fixed = reinterpret_cast<std::uint8_t*>(0xf3ffe00000ull);
        GuestArenaMarkUsed_nid_postfix(fixed, 2 * Page);
        GuestArenaCommit_nid_postfix(fixed, 2 * Page, PAGE_READWRITE, Page);
        fixed[0] = 44;
        fixed[Page] = 55;
        GuestArenaReset_nid_postfix(fixed, 2 * Page);
        HANDLE section = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_EXECUTE_READWRITE, 0, 2 * Page, nullptr);
        Require(section != nullptr, "could not create guest backing");
        GuestArenaMap_nid_postfix(fixed, 2 * Page, section, 0, PAGE_READWRITE);
        fixed[0] = 66;
        fixed[Page] = 77;
        auto* alias = static_cast<std::uint8_t*>(GuestArenaMapAlias_nid_postfix(reinterpret_cast<std::uintptr_t>(fixed), 2 * Page));
        Require(alias[Page] == 77, "guest alias did not share the backing");
        alias[Page] = 88;
        GuestArenaReset_nid_postfix(fixed, Page);
        Require(fixed[Page] == 88, "partial unmap damaged the rest of a shared view");
        GuestArenaCommit_nid_postfix(fixed, Page, PAGE_READWRITE, Page);
        fixed[0] = 99;
        Require(fixed[Page] == 88, "partial recommit damaged shared backing");
        GuestArenaUnmapAlias_nid_postfix(alias);
        GuestArenaReset_nid_postfix(fixed, 2 * Page);
        GuestArenaRelease_nid_postfix(fixed, 2 * Page);
        CloseHandle(section);

        // PERF-GTS-264: a large shared mapping is made of bounded views, so a partial unmap only
        // splits the view it falls in; aliases still span the views of one section.
        constexpr std::size_t View = 0x200000;
        constexpr std::size_t LargeBytes = 3 * View + 2 * Page;
        auto* big = static_cast<std::uint8_t*>(GuestArenaAllocate_nid_postfix(LargeBytes, 0x10000));
        HANDLE bigSection = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_EXECUTE_READWRITE, 0, LargeBytes, nullptr);
        Require(bigSection != nullptr, "could not create large guest backing");
        GuestArenaMap_nid_postfix(big, LargeBytes, bigSection, 0, PAGE_READWRITE);
        Require(Query(big + View).AllocationBase == big + View && Query(big + 3 * View).AllocationBase == big + 3 * View,
                "a large shared mapping was not split into bounded views");
        for (std::size_t at = 0; at < LargeBytes; at += Page) big[at] = static_cast<std::uint8_t>(at / Page + 1);
        auto* across = static_cast<std::uint8_t*>(GuestArenaMapAlias_nid_postfix(reinterpret_cast<std::uintptr_t>(big + View - Page), 2 * Page));
        Require(across != nullptr && across[0] == big[View - Page] && across[Page] == big[View],
                "an alias across two shared views did not share the backing");
        GuestArenaUnmapAlias_nid_postfix(across);
        GuestArenaReset_nid_postfix(big + View + 4 * Page, Page);
        for (std::size_t at = 0; at < LargeBytes; at += Page) {
            if (at == View + 4 * Page) continue;
            Require(big[at] == static_cast<std::uint8_t>(at / Page + 1), "partial unmap of a bounded view damaged shared memory");
        }
        Require(Query(big).AllocationBase == big && Query(big + 2 * View).AllocationBase == big + 2 * View,
                "partial unmap remapped views outside the one it fell in");
        GuestArenaReset_nid_postfix(big, LargeBytes);
        GuestArenaRelease_nid_postfix(big, LargeBytes);
        CloseHandle(bigSection);

        VirtualFree(driverBase, 0, MEM_RELEASE);
        VirtualFree(largeHost, 0, MEM_RELEASE);
        std::puts("PASS: partitioned arena, host collisions, reuse, fixed mappings, partial unmap, shared aliases, bounded shared views");
        return 0;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "FAIL: %s (Windows error %lu)\n", error.what(), GetLastError());
        return 1;
    }
}
