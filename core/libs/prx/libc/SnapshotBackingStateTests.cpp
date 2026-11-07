#include "prx/libc/include/SnapshotBackingState.hpp"
#include <cstdio>
#include <cstdlib>
#include <thread>
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr,"failure line %d: %s\n",__LINE__,#x); std::abort(); } } while(false)
int main(int argc, char**) {
    using namespace SnapshotBacking;
    if (argc > 1) {
#ifdef _WIN32
        _putenv("RYGT_NATIVE_SNAPSHOT_LEASE=0");
#else
        setenv("RYGT_NATIVE_SNAPSHOT_LEASE", "0", 1);
#endif
        const auto before = state.load();
        { Mutation ignored; MarkUnsafe(0x20000, 1); }
        CHECK(state.load() == before && !RangeSafe(0x20000, 1));
        std::puts("PASS: disabled tracking leaves state unchanged");
        return 0;
    }
#ifdef _WIN32
    _putenv("RYGT_NATIVE_SNAPSHOT_LEASE=1");
#else
    setenv("RYGT_NATIVE_SNAPSHOT_LEASE", "1", 1);
#endif
    const auto initial = state.load();
    { Mutation a; CHECK((state.load() & 0xffffu) == 1);
      { Mutation b; CHECK((state.load() & 0xffffu) == 2); }
      CHECK((state.load() & 0xffffu) == 1); }
    CHECK((state.load() & 0xffffu) == 0 && state.load() > initial);
    CHECK(RangeSafe(0x20000, 0x10000));
    MarkUnsafe(0x2ffff, 2);
    CHECK(!RangeSafe(0x20000, 1) && !RangeSafe(0x30000, 1));
    CHECK(RangeSafe(0x40000, 1));
    std::thread writer([] { MarkUnsafe(0x50000, 1); }); writer.join();
    CHECK(!RangeSafe(0x50000, 1));
    MarkUnsafe(AddressLimit - 1, 2);
    CHECK(state.load() == Invalid && !RangeSafe(0x40000, 1));
    std::puts("PASS: nested mutation, unsafe page boundaries, overflow fail-closed");
}
