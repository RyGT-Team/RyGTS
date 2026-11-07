#ifndef CORE_LIBS_PRX_LIBKERNEL_FILE_FILE_HPP
#define CORE_LIBS_PRX_LIBKERNEL_FILE_FILE_HPP

#include <functional>
#include <cstddef>
#include <cstdint>

#include "SceTypes.hpp"
#include "prx/libc/include/General.hpp"

extern "C" {

int APS5_VABI sceKernelOpen(const char* path, int flags, std::uint16_t mode);
int APS5_VABI sceKernelClose(int d);
std::int64_t APS5_VABI sceKernelRead(int d, void* buf, std::size_t nbytes);
std::int64_t APS5_VABI sceKernelWrite(int d, const void* buf, std::size_t nbytes);
std::int64_t APS5_VABI sceKernelLseek(int d, std::int64_t offset, int whence);
int APS5_VABI sceKernelStat(const char* path, FileStat* sb);
int APS5_VABI sceKernelUnlink(const char* path);

}

// Before the host kernel writes guest memory directly (a file read into a guest buffer), lets a
// renderer that write-protects guest pages it caches drop them; the write would otherwise fail
// on a protected page instead of faulting. Returns false if opt-in tracking is enabled but the
// required unsafe-range marker is unavailable; callers must not start the host write in that case.
bool PrepareGuestWrite(void* buffer, std::size_t bytes);
// Reads into guest memory: read(target) returns bytes read or -1. The renderer can protect the
// buffer again (a GPU write to it) between PrepareGuestWrite and the read; Windows then fails the
// read instead of faulting, and the error escaped as a C++ exception into the game's file thread.
// A failed read is made again into host memory and copied, so the copy faults like a guest store.
std::int64_t ReadIntoGuest(void* buffer, std::size_t bytes, const std::function<std::int64_t(void*)>& read);

#endif
