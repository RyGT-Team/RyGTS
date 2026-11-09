// Windows-only fixture; the runner injects the actual ReadIntoGuest function.
#include <windows.h>
#include <atomic>
#include <cerrno>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <functional>
#include <vector>
static unsigned calls; static std::size_t sizes[4];
static void Require(bool ok) { if (!ok) std::abort(); }
static void PrepareGuestWrite(void* p, std::size_t n) {
    sizes[calls++] = n;
    if (p && n) { DWORD old; Require(VirtualProtect(p, n, PAGE_READWRITE, &old) != 0); }
}
// INSERT_READ_INTO_GUEST
int main() {
    auto* guest = static_cast<char*>(VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    Require(guest != nullptr);
    unsigned reads = 0;
    auto n = ReadIntoGuest(guest, 64, [&](void* p) -> std::int64_t {
        if (++reads == 1) { DWORD old; Require(VirtualProtect(guest, 4096, PAGE_READONLY, &old)); errno = EIO; return -1; }
        std::memcpy(p, "hello", 5); return 5;
    });
    Require(n == 5 && reads == 2 && calls == 2 && sizes[0] == 64 && sizes[1] == 5 && std::memcmp(guest, "hello", 5) == 0);
    calls = reads = 0;
    n = ReadIntoGuest(guest, 64, [&](void*) -> std::int64_t { ++reads; return 3; });
    Require(n == 3 && reads == 1 && calls == 1 && sizes[0] == 64);
    calls = reads = 0;
    n = ReadIntoGuest(guest, 64, [&](void*) -> std::int64_t { if (++reads == 1) { errno = EIO; return -1; } return 0; });
    Require(n == 0 && reads == 2 && calls == 2 && sizes[1] == 0);
    calls = reads = 0;
    n = ReadIntoGuest(guest, 64, [&](void*) -> std::int64_t { if (++reads == 1) { errno = EIO; return -1; } errno = EINVAL; return -1; });
    Require(n == -1 && errno == EIO && reads == 2 && calls == 1);
    Require(VirtualFree(guest, 0, MEM_RELEASE));
    std::puts("read copyback protection tests passed");
}
