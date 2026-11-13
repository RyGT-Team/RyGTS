#ifndef CORE_LIBS_PRX_LIBKERNEL_DIRECTMEMORY_DIRECTMEMORY_HPP
#define CORE_LIBS_PRX_LIBKERNEL_DIRECTMEMORY_DIRECTMEMORY_HPP

#include <cstdint>
#include <cstddef>
#include <mutex>

#include "prx/libkernel/KernelErrors.hpp"

#ifdef RTY_PS4_MEMORY
// Base PS4: 5248 MiB in total, of which 448 MiB is flexible memory.
static constexpr size_t DIRECT_MEMORY_SIZE = (5248ULL - 448ULL) * 1024 * 1024;
#else
static constexpr size_t DIRECT_MEMORY_SIZE = 13824ULL * 1024 * 1024;
#endif
static constexpr size_t PS5_PAGE_SIZE = 0x4000;

int DirectMemoryAlloc(int64_t searchStart, int64_t searchEnd, size_t len, size_t alignment, int memoryType, int64_t* physOut);
void DirectMemoryFree(int64_t start, size_t len);
void CreateDirectMemoryBacking(int64_t start, size_t len, int memoryType);
bool QueryDirectMapping(std::uintptr_t address, std::uintptr_t* start, std::uintptr_t* end, std::uint64_t* offset, int* memoryType);
void ForgetDirectMemory(int64_t start, size_t len);
void UnmapDirectMemory(int64_t start, size_t len);
bool DirectMemoryFind(int64_t offset, bool findNext, int64_t* start, int64_t* end, int* memoryType);
size_t DirectMemoryFreeRun(uint64_t offset, uint64_t limit);
int DoMapDirect(void** addr, size_t len, int prot, int flags, int64_t physStart, size_t alignment);
int DoMapAnon(void** addr, size_t len, int prot, int flags);
int DoMprotect(const void* addr, size_t len, int prot);
int DoMunmap(void* addr, size_t len);
int DoReserveVirtual(void** addr, size_t len, int flags, size_t alignment);
bool GuestProtection(uintptr_t addr, int* prot);

// PERF-GTS-265: tells the renderer once that a run of adjacent fixed direct maps replaces
// [addr, addr + len), and holds the mapping transition while the run is mapped; DoMapDirect on
// this thread does not tell it again for each piece inside the range.
class FixedReplacementNotice {
public:
    FixedReplacementNotice(void* addr, size_t len, std::size_t entries);
    ~FixedReplacementNotice();
    FixedReplacementNotice(const FixedReplacementNotice&) = delete;
    FixedReplacementNotice& operator=(const FixedReplacementNotice&) = delete;

private:
    std::lock_guard<std::recursive_mutex> transition;
    void* start;
    size_t bytes;
};

// PERF-GTS-268: whether one registered guest allocation holds all of [addr, addr + len), so a
// fixed direct map of it is remapped in place (RYGT_BATCH_MAP_MERGE=0: always false).
bool FixedRangeInOneAllocation(const void* addr, size_t len);

#endif