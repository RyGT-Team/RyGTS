#include "prx/libc/include/GuestAllocations.hpp"
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <thread>
extern "C" void* RytySnapshotTryPinReadable(std::uint64_t, std::uint64_t) noexcept;
extern "C" void RytySnapshotReleasePin(void*) noexcept;
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr,"FAIL %d: %s\n",__LINE__,#x); std::abort(); } } while(false)
int main() {
 _putenv("RYGT_NATIVE_SNAPSHOT_LEASE=1"); auto p=reinterpret_cast<void*>(0xf300000000ull);
 { GuestAllocations::Mutation m; m.Add(p,0x10000,true,true); }
 CHECK(!RytySnapshotTryPinReadable(reinterpret_cast<std::uint64_t>(p)+0xffff,2));
 auto pin=RytySnapshotTryPinReadable(reinterpret_cast<std::uint64_t>(p),0x10000); CHECK(pin);
 std::atomic<int> phase{0}; std::thread t([&] { GuestAllocations::Mutation m; phase.store(1); m.Protect(p,0x10000,false,false,[&]{phase.store(2);}); });
 while (!phase.load()) std::this_thread::yield();
 std::this_thread::sleep_for(std::chrono::milliseconds(50)); CHECK(phase.load()==1);
 RytySnapshotReleasePin(pin); t.join(); CHECK(phase.load()==2);
 CHECK(!RytySnapshotTryPinReadable(reinterpret_cast<std::uint64_t>(p),1));
 std::puts("PASS: actual opaque range pin blocks registered protection until release");
}
