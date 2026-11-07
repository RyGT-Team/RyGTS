#include "prx/libc/include/GuestArena.hpp"
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
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr,"FAIL line %d: %s\n",__LINE__,#x); std::abort(); } } while(false)
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
    CHECK(RytySnapshotBackingVersion()==1);
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
    GuestArenaReset_nid_postfix(first,Page); GuestArenaRelease_nid_postfix(first,Page); CloseHandle(section);
    std::puts("PASS: actual C ABI unique guest backing and duplicate-handle rejection");
}
