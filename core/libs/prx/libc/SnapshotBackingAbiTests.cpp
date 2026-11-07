#include "prx/libc/include/GuestArena.hpp"
#include "prx/libc/include/GuestAllocations.hpp"
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <atomic>
#include <thread>
extern "C" int RytySnapshotBackingState(std::uint64_t*, std::uint64_t*) noexcept;
extern "C" int RytySnapshotQueryUniqueBacking(std::uint64_t, std::uint64_t, std::uint64_t*) noexcept;
extern "C" std::uint32_t RytySnapshotBackingVersion() noexcept;
extern "C" int RytySnapshotRangeSafe(std::uint64_t, std::uint64_t) noexcept;
extern "C" void RytySnapshotMarkUnsafe(std::uint64_t, std::uint64_t) noexcept;
extern "C" void* RytySnapshotMapRendererAlias(std::uint64_t, std::uint64_t) noexcept;
extern "C" void* RytySnapshotTryPinReadable(std::uint64_t, std::uint64_t) noexcept;
extern "C" void RytySnapshotReleasePin(void*) noexcept;
extern "C" int RytySnapshotProtectClamped(std::uint64_t, std::uint64_t, std::uint32_t) noexcept;
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr,"FAIL line %d: %s\n",__LINE__,#x); std::abort(); } } while(false)
static void* waiterPin;
static std::uintptr_t waiterAddress;
static bool nestedWaiter() {
    GuestArena::GuestArenaSetProtection_nid_postfix(waiterAddress, 0x4000, PAGE_READWRITE);
    RytySnapshotReleasePin(waiterPin); waiterPin=nullptr;
    return true;
}
int main() {
    using namespace GuestArena;
    _putenv("RYGT_NATIVE_SNAPSHOT_LEASE=1");
    constexpr std::size_t Page=0x4000;
    auto* first=reinterpret_cast<void*>(0xf3ff000000ull);
    auto* second=reinterpret_cast<void*>(0xf3ff010000ull);
    GuestArenaMarkUsed_nid_postfix(first,Page); GuestArenaMarkUsed_nid_postfix(second,Page);
    HANDLE section=CreateFileMappingW(INVALID_HANDLE_VALUE,nullptr,PAGE_EXECUTE_READWRITE,0,Page,nullptr); CHECK(section);
    GuestArenaMap_nid_postfix(first,Page,section,0,PAGE_READWRITE);
    std::uint64_t epoch{}, active{};
    CHECK(RytySnapshotBackingVersion()==2);
    CHECK(RytySnapshotBackingState(&epoch,&active));
    CHECK(RytySnapshotQueryUniqueBacking(reinterpret_cast<std::uintptr_t>(first),Page,&epoch));
    HANDLE dup{}; CHECK(DuplicateHandle(GetCurrentProcess(),section,GetCurrentProcess(),&dup,0,FALSE,DUPLICATE_SAME_ACCESS));
    GuestArenaMap_nid_postfix(second,Page,dup,0,PAGE_READWRITE);
    CHECK(!RytySnapshotQueryUniqueBacking(reinterpret_cast<std::uintptr_t>(first),Page,&epoch));
    GuestArenaReset_nid_postfix(second,Page); GuestArenaRelease_nid_postfix(second,Page); CloseHandle(dup);
    CHECK(RytySnapshotQueryUniqueBacking(reinterpret_cast<std::uintptr_t>(first),Page,&epoch));
    GuestArenaMarkUsed_nid_postfix(second,Page);
    std::atomic<int> phase{0};
    std::thread mapper([&] {
        for (int i=0;i<50;++i) {
            GuestArenaMap_nid_postfix(second,Page,section,0,PAGE_READWRITE);
            phase.store(1,std::memory_order_release);
            while (phase.load(std::memory_order_acquire)!=2) std::this_thread::yield();
            GuestArenaReset_nid_postfix(second,Page);
            phase.store(3,std::memory_order_release);
            while (phase.load(std::memory_order_acquire)!=0) std::this_thread::yield();
        }
    });
    for (int i=0;i<50;++i) {
        while (phase.load(std::memory_order_acquire)!=1) std::this_thread::yield();
        CHECK(!RytySnapshotQueryUniqueBacking(reinterpret_cast<std::uintptr_t>(first),Page,&epoch));
        phase.store(2,std::memory_order_release);
        while (phase.load(std::memory_order_acquire)!=3) std::this_thread::yield();
        CHECK(RytySnapshotQueryUniqueBacking(reinterpret_cast<std::uintptr_t>(first),Page,&epoch));
        phase.store(0,std::memory_order_release);
    }
    mapper.join();
    { GuestAllocations::Mutation m; m.Add(first, Page, true, true); }
    { GuestAllocations::Mutation m; GuestArenaSetProtection_nid_postfix(reinterpret_cast<std::uintptr_t>(first),Page,PAGE_READWRITE); }
    { GuestAllocations::Mutation m; m.Protect(first,Page,true,true,[&] { GuestArenaSetProtection_nid_postfix(reinterpret_cast<std::uintptr_t>(first),Page,PAGE_READWRITE); }); }
    auto pin = RytySnapshotTryPinReadable(reinterpret_cast<std::uintptr_t>(first),Page); CHECK(pin);
    std::atomic<int> guarded{0};
    std::thread raw([&] { guarded.store(1); GuestArenaSetProtection_nid_postfix(reinterpret_cast<std::uintptr_t>(first),Page,PAGE_READWRITE); guarded.store(2); });
    while (!guarded.load()) std::this_thread::yield();
    std::this_thread::sleep_for(std::chrono::milliseconds(30)); CHECK(guarded.load()==1);
    RytySnapshotReleasePin(pin); raw.join(); CHECK(guarded.load()==2);
    waiterAddress=reinterpret_cast<std::uintptr_t>(second);
    waiterPin=RytySnapshotTryPinReadable(reinterpret_cast<std::uintptr_t>(first),Page); CHECK(waiterPin);
    GuestAllocations::GuestAllocationsSetPinWaiter_nid_postfix(nestedWaiter);
    GuestArenaSetProtection_nid_postfix(reinterpret_cast<std::uintptr_t>(first),Page,PAGE_READWRITE);
    CHECK(!waiterPin); GuestAllocations::GuestAllocationsSetPinWaiter_nid_postfix(nullptr);
    const auto mappedAddr = reinterpret_cast<std::uintptr_t>(first);
    MEMORY_BASIC_INFORMATION memory{};
    GuestArenaSetProtection_nid_postfix(mappedAddr, Page, PAGE_READONLY);
    CHECK(RytySnapshotProtectClamped(mappedAddr, Page, 3));
    CHECK(VirtualQuery(first, &memory, sizeof(memory)) == sizeof(memory)); CHECK((memory.Protect & 0xffu)==PAGE_READONLY);
    GuestArenaSetProtection_nid_postfix(mappedAddr, Page, PAGE_NOACCESS);
    CHECK(RytySnapshotProtectClamped(mappedAddr, Page, 3));
    CHECK(VirtualQuery(first, &memory, sizeof(memory)) == sizeof(memory)); CHECK((memory.Protect & 0xffu)==PAGE_NOACCESS);
    GuestArenaSetProtection_nid_postfix(mappedAddr, Page, PAGE_READWRITE);
    CHECK(RytySnapshotProtectClamped(mappedAddr, Page, 3));
    CHECK(RytySnapshotProtectClamped(mappedAddr, Page, 2)==-1);
    CHECK(RytySnapshotProtectClamped(mappedAddr, 2*Page, 3)==-1);
    CHECK(RytySnapshotProtectClamped(mappedAddr+Page, Page, 3)==0);
    auto* region = reinterpret_cast<void*>(0xf3ff020000ull);
    auto* gap = reinterpret_cast<void*>(0xf3ff024000ull);
    GuestArenaMarkUsed_nid_postfix(region, 2 * Page);
    HANDLE wide = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_EXECUTE_READWRITE, 0, 2 * Page, nullptr); CHECK(wide);
    HANDLE replacement = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_EXECUTE_READWRITE, 0, Page, nullptr); CHECK(replacement);
    GuestArenaMap_nid_postfix(region, 2 * Page, wide, 0, PAGE_READWRITE);
    CHECK(RytySnapshotQueryUniqueBacking(reinterpret_cast<std::uintptr_t>(region), 2 * Page, &epoch));
    GuestArenaReset_nid_postfix(gap, Page);
    CHECK(RytySnapshotQueryUniqueBacking(reinterpret_cast<std::uintptr_t>(region), Page, &epoch));
    CHECK(!RytySnapshotQueryUniqueBacking(reinterpret_cast<std::uintptr_t>(gap), Page, &epoch));
    GuestArenaMap_nid_postfix(gap, Page, replacement, 0, PAGE_READWRITE);
    CHECK(RytySnapshotQueryUniqueBacking(reinterpret_cast<std::uintptr_t>(region), 2 * Page, &epoch));
    GuestArenaReset_nid_postfix(region, 2 * Page);
    GuestArenaRelease_nid_postfix(region, 2 * Page);
    CloseHandle(wide); CloseHandle(replacement);
    const auto firstAddr = reinterpret_cast<std::uintptr_t>(first);
    const auto secondAddr = reinterpret_cast<std::uintptr_t>(second);
    CHECK(RytySnapshotRangeSafe(firstAddr,Page));
    void* trusted=RytySnapshotMapRendererAlias(firstAddr,Page); CHECK(trusted);
    CHECK(RytySnapshotRangeSafe(firstAddr,Page));
    RytySnapshotMarkUnsafe(firstAddr,Page); CHECK(!RytySnapshotRangeSafe(firstAddr,Page));
    GuestArenaUnmapAlias_nid_postfix(trusted);
    GuestArenaMap_nid_postfix(second,Page,section,0,PAGE_READWRITE);
    CHECK(RytySnapshotRangeSafe(secondAddr,Page));
    void* legacy=GuestArenaMapAlias_nid_postfix(secondAddr,Page); CHECK(legacy);
    CHECK(!RytySnapshotRangeSafe(secondAddr,Page));
    GuestArenaUnmapAlias_nid_postfix(legacy);
    GuestArenaReset_nid_postfix(second,Page); GuestArenaRelease_nid_postfix(second,Page);
    { GuestAllocations::Mutation m; m.Remove(first); }
    GuestArenaReset_nid_postfix(first,Page); GuestArenaRelease_nid_postfix(first,Page); CloseHandle(section);
    std::puts("PASS: actual C ABI unique guest backing and duplicate-handle rejection");
}
