#include <cstdint>
#include <cstdio>
#include <cstdlib>
extern "C" void RytySnapshotMarkUnsafeTagged(std::uint64_t, std::uint64_t, std::uint32_t) noexcept;
extern "C" std::uint32_t RytySnapshotUnsafeCauses(std::uint64_t, std::uint64_t) noexcept;
extern "C" int RytySnapshotRangeSafe(std::uint64_t, std::uint64_t) noexcept;
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr,"FAIL %d: %s\n",__LINE__,#x); std::abort(); } } while(false)
int main() {
    _putenv("RYGT_NATIVE_SNAPSHOT_LEASE=1"); _putenv("RYGT_SNAPSHOT_POISON_TRACE=1");
    constexpr std::uint64_t base=0xf400000000ull;
    CHECK(RytySnapshotUnsafeCauses(base,0x20000)==0);
    RytySnapshotMarkUnsafeTagged(base+0xffff,2,1);
    CHECK(!RytySnapshotRangeSafe(base,0x20000));
    CHECK(RytySnapshotUnsafeCauses(base,0x20000)==1);
    RytySnapshotMarkUnsafeTagged(base,1,2);
    CHECK(RytySnapshotUnsafeCauses(base,1)==1);
    RytySnapshotMarkUnsafeTagged(base+0x20000,1,4);
    CHECK(RytySnapshotUnsafeCauses(base,0x30000)==5);
    CHECK(RytySnapshotUnsafeCauses(base,0)==0);
    RytySnapshotMarkUnsafeTagged(1ull<<40,1,8);
    CHECK(RytySnapshotUnsafeCauses(base,0x30000)==5);
    std::puts("PASS: actual first-poison C ABI sticky causes and range boundary");
}
