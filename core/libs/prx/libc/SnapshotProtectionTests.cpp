#include <cstdint>
#include <cstdio>
#include <cstdlib>
extern "C" int RytySnapshotProtectionBegin() noexcept;
extern "C" void RytySnapshotProtectionEnd() noexcept;
extern "C" int RytySnapshotBackingState(std::uint64_t*, std::uint64_t*) noexcept;
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr,"FAIL %d: %s\n",__LINE__,#x); std::abort(); } } while(false)
int main() {
    _putenv("RYGT_NATIVE_SNAPSHOT_LEASE=1");
    std::uint64_t before{},after{},active{};
    CHECK(RytySnapshotBackingState(&before,&active));
    CHECK(RytySnapshotProtectionBegin());
    CHECK(!RytySnapshotBackingState(&after,&active));
    CHECK(RytySnapshotProtectionBegin());
    RytySnapshotProtectionEnd();
    CHECK(!RytySnapshotBackingState(&after,&active));
    RytySnapshotProtectionEnd();
    CHECK(RytySnapshotBackingState(&after,&active)); CHECK(after>before); CHECK(active==0);
    std::puts("PASS: actual protection ABI invalidates epoch and brackets nested transitions");
}
