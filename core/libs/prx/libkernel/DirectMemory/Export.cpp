#include <atomic>
#include <cstdint>
#include <cstddef>
#include <cstring>
#include <cstdlib>
#include "SceTypes.hpp"
#include "prx/libc/include/General.hpp"
#include "prx/libc/include/GuestAllocations.hpp"
#include "prx/libkernel/DirectMemory/DirectMemory.hpp"
#include "prx/libkernel/DirectMemory/BatchMapStage.hpp"
#include "prx/libkernel/KernelErrors.hpp"
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <fstream>
#include <sstream>
#include <sys/mman.h>
#endif
#include <algorithm>
#include <stdexcept>
#include <string>
#include <iterator>
#include <map>
#include <cstdio>
#include <mutex>
#include <limits>
#include <exception>

namespace {

// One bounded report per opted-in batch; it owns no mapping locks.
struct BatchMapTrace {
 bool active{}; unsigned ordinal{}; int requested{}, flags{}, attempted{}, succeeded{}, error{};
 unsigned op[6]{}, pages16{}, pages64{}, other{};
 std::uint64_t bytes{}, start{}, frequency{}, direct_ticks{}, direct_max{};
 int exceptions{};
 BatchMapStages stages{};
 static std::uint64_t Clock() {
#ifdef _WIN32
  LARGE_INTEGER q{}; QueryPerformanceCounter(&q); return static_cast<std::uint64_t>(q.QuadPart);
#else
  return 0;
#endif
 }
 explicit BatchMapTrace(int count, int batch_flags) : requested(count), flags(batch_flags) {
#ifdef _WIN32
  if (count==0) return;
  static const bool enabled=[] { const char* e=std::getenv("RYGT_BATCH_MAP_TRACE"); return e && e[0]=='1' && e[1]=='\0'; }();
  if (!enabled) return;
  char path[128]; std::snprintf(path,sizeof(path),"logs/batch-map-%lu.capture",GetCurrentProcessId());
  if (GetFileAttributesA(path)==INVALID_FILE_ATTRIBUTES) return;
  static std::atomic<unsigned> claimed{};
  unsigned value=claimed.load(std::memory_order_relaxed);
  while (value<256 && !claimed.compare_exchange_weak(value,value+1,std::memory_order_relaxed)) {}
  if (value>=256) return;
  ordinal=value+1; LARGE_INTEGER freq{}; QueryPerformanceFrequency(&freq);
  frequency=static_cast<std::uint64_t>(freq.QuadPart); exceptions=std::uncaught_exceptions(); active=true; start=Clock();
#endif
 }
 void Operation(int operation) {
  if (!active) return;
  ++attempted; ++op[operation>=0 && operation<5 ? operation : 5];
 }
 void Entry(std::uint64_t length) {
  if (!active) return;
  const auto max=(std::numeric_limits<std::uint64_t>::max)();
  bytes = length > max-bytes ? max : bytes+length;
  pages16 += length==0x4000; pages64 += length==0x10000; other += length!=0x4000 && length!=0x10000;
 }
 void Direct(std::uint64_t begin) { if (active) { const auto elapsed=Clock()-begin; direct_ticks+=elapsed; direct_max=std::max(direct_max,elapsed); } }
 ~BatchMapTrace() {
#ifdef _WIN32
  if (!active) return;
  const auto end=Clock();
  std::fprintf(stderr,"[batch-map] pid=%lu tid=%lu ordinal=%u start=%llu end=%llu frequency=%llu requested=%d flags=%d attempted=%d processed=%d error=%d exception=%d op=%u/%u/%u/%u/%u/%u bytes=%llu size16k=%u size64k=%u other=%u direct_ticks=%llu direct_max=%llu\n",
   GetCurrentProcessId(),GetCurrentThreadId(),ordinal,static_cast<unsigned long long>(start),static_cast<unsigned long long>(end),static_cast<unsigned long long>(frequency),requested,flags,attempted,succeeded,error,std::uncaught_exceptions()>exceptions,op[0],op[1],op[2],op[3],op[4],op[5],static_cast<unsigned long long>(bytes),pages16,pages64,other,static_cast<unsigned long long>(direct_ticks),static_cast<unsigned long long>(direct_max));
  for (unsigned i=0;i<4;++i) { const auto& s=stages.stage[i];
   std::fprintf(stderr,"[batch-map-stage] pid=%lu ordinal=%u stage=%u attempts=%llu completed=%llu ticks=%llu max=%llu\n",GetCurrentProcessId(),ordinal,i,static_cast<unsigned long long>(s.attempts),static_cast<unsigned long long>(s.completed),static_cast<unsigned long long>(s.ticks),static_cast<unsigned long long>(s.maximum)); }
#endif
 }
};
struct BatchDirectTimer {
 BatchMapTrace& trace; std::uint64_t start; BatchMapStages* previous{};
 explicit BatchDirectTimer(BatchMapTrace& t) : trace(t), start(t.active ? BatchMapTrace::Clock() : 0), previous(diag412_set_context(t.active ? &t.stages : nullptr)) {}
 ~BatchDirectTimer() { diag412_set_context(previous); if (start) trace.Direct(start); }
};

constexpr size_t FLEXIBLE_MEMORY_SIZE = 448ULL * 1024 * 1024;

constexpr int PRT_APERTURE_COUNT = 3;

struct PrtAperture {
    void* address;
    size_t length;
};

struct NamedRange {
 uintptr_t end;
 std::string name;
};

std::mutex g_rangeNameLock;
std::map<uintptr_t, NamedRange> g_rangeNames;

void EraseRangeNames(uintptr_t start, uintptr_t end) {
 auto it = g_rangeNames.lower_bound(start);
 if (it != g_rangeNames.begin() && std::prev(it)->second.end > start) --it;
 while (it != g_rangeNames.end() && it->first < end) {
  const auto rangeStart = it->first;
  const auto range = it->second;
  it = g_rangeNames.erase(it);
  if (rangeStart < start) g_rangeNames.emplace(rangeStart, NamedRange{start, range.name});
  if (range.end > end) it = g_rangeNames.emplace(end, NamedRange{range.end, range.name}).first;
 }
}

void ApplyRangeName(uintptr_t address, VirtualQueryInfo* info) {
 std::lock_guard lock(g_rangeNameLock);
 auto next = g_rangeNames.upper_bound(address);
 if (next != g_rangeNames.begin()) {
  const auto containing = std::prev(next);
  if (address < containing->second.end) {
   info->start = std::max<uintptr_t>(info->start, containing->first);
   info->end = std::min<uintptr_t>(info->end, containing->second.end);
   std::strncpy(info->name, containing->second.name.c_str(), sizeof(info->name) - 1);
   return;
  }
  info->start = std::max<uintptr_t>(info->start, containing->second.end);
 }
 if (next != g_rangeNames.end()) info->end = std::min<uintptr_t>(info->end, next->first);
}

std::mutex g_prtLock;
PrtAperture g_prtApertures[PRT_APERTURE_COUNT] = {};

std::mutex g_flexibleLock;
std::map<uintptr_t, size_t> g_flexibleRanges;

size_t _flexibleUsedLocked() {
    size_t used = 0;
    for (const auto& [start, len] : g_flexibleRanges) used += len;
    return used;
}

int _mapFlexible(void** addr, size_t len, int prot, int flags) {
    std::lock_guard lock(g_flexibleLock);
    if (len > FLEXIBLE_MEMORY_SIZE - std::min(FLEXIBLE_MEMORY_SIZE, _flexibleUsedLocked())) {
        std::fprintf(stderr, "[memory] flexible memory exhausted: request 0x%zx with 0x%zx of 0x%zx in use\n", len, _flexibleUsedLocked(), static_cast<size_t>(FLEXIBLE_MEMORY_SIZE));
        return SCE_KERNEL_ERROR_ENOMEM;
    }
    const int result = DoMapAnon(addr, len, prot, flags);
    if (result == 0) g_flexibleRanges[reinterpret_cast<uintptr_t>(*addr)] = len;
    return result;
}

void _releaseFlexible(uintptr_t start, size_t len) {
    std::lock_guard lock(g_flexibleLock);
    const uintptr_t end = start + len;
    auto it = g_flexibleRanges.upper_bound(start);
    if (it != g_flexibleRanges.begin()) --it;
    while (it != g_flexibleRanges.end() && it->first < end) {
        const uintptr_t rangeStart = it->first;
        const uintptr_t rangeEnd = rangeStart + it->second;
        if (rangeEnd <= start) { ++it; continue; }
        it = g_flexibleRanges.erase(it);
        if (rangeStart < start) g_flexibleRanges[rangeStart] = start - rangeStart;
        if (rangeEnd > end) g_flexibleRanges[end] = rangeEnd - end;
    }
}

}

// A Ryty error thrown inside a memory call reached the game's code, which cannot unwind it, and
// ended the process (Lance's crash leaving a race for the home menu, 5,500 map changes in the last
// 2 s). The call now fails with an error the game can handle, and the reason is logged.
template <typename F>
int GuardMemoryCall(const char* name, F&& call) {
    try {
        return call();
    } catch (const std::bad_alloc&) {
        std::fprintf(stderr, "[memory] %s: out of host memory\n", name);
        return SCE_KERNEL_ERROR_ENOMEM;
    } catch (const std::exception& error) {
        static std::atomic<unsigned> logged{0};
        if (logged.fetch_add(1) < 50) {
            std::fprintf(stderr, "[memory] %s failed: %s\n", name, error.what());
        }
        return SCE_KERNEL_ERROR_EINVAL;
    }
}

extern "C" {

int APS5_VABI sceKernelAllocateDirectMemory(int64_t search_start, int64_t search_end, size_t len, size_t alignment, int memory_type, int64_t* phys_addr_out) {
 if (search_start < 0 || search_end <= search_start || len == 0
  || (len & (PS5_PAGE_SIZE - 1)) || !phys_addr_out
  || (alignment != 0 && (alignment & (PS5_PAGE_SIZE - 1))))
  return SCE_KERNEL_ERROR_EINVAL;
 const int result = DirectMemoryAlloc(search_start, search_end, len, alignment, memory_type, phys_addr_out);
 if (result != 0) std::fprintf(stderr, "[memory] allocate direct 0x%llx-0x%llx len=0x%zx align=0x%zx type=%d failed: 0x%x\n", static_cast<long long>(search_start), static_cast<long long>(search_end), len, alignment, memory_type, result);
 return result;
}

int APS5_VABI sceKernelAllocateMainDirectMemory(size_t len, size_t alignment, int memory_type, int64_t* phys_addr_out) {
 return sceKernelAllocateDirectMemory(0, static_cast<int64_t>(DIRECT_MEMORY_SIZE), len, alignment, memory_type, phys_addr_out);
}

int APS5_VABI sceKernelAvailableDirectMemorySize(int64_t search_start, int64_t search_end, size_t alignment, int64_t* phys_addr_out, size_t* size_out) {
 if (!phys_addr_out || !size_out) return SCE_KERNEL_ERROR_EINVAL;
 int64_t tmpPhys = 0;
 int ret = DirectMemoryAlloc(search_start, search_end, PS5_PAGE_SIZE, alignment, -1, &tmpPhys);
 if (ret != 0) { *phys_addr_out = 0; *size_out = 0; return ret; }
 DirectMemoryFree(tmpPhys, PS5_PAGE_SIZE);
 *phys_addr_out = tmpPhys;
 *size_out = DirectMemoryFreeRun(static_cast<uint64_t>(tmpPhys), static_cast<uint64_t>(search_end));
 return 0;
}

int APS5_VABI sceKernelDirectMemoryQuery(int64_t offset, int flags, void* info, size_t info_size) {
 constexpr int SCE_KERNEL_DMQ_FIND_NEXT = 1;
 if (!info || offset < 0) return SCE_KERNEL_ERROR_EINVAL;
 struct DirectMemoryQueryInfo { int64_t start; int64_t end; int memory_type; };
 if (info_size < sizeof(DirectMemoryQueryInfo)) return SCE_KERNEL_ERROR_EINVAL;
 auto* q = static_cast<DirectMemoryQueryInfo*>(info);
 if (!DirectMemoryFind(offset, (flags & SCE_KERNEL_DMQ_FIND_NEXT) != 0, &q->start, &q->end, &q->memory_type)) return SCE_KERNEL_ERROR_EACCES;
 return 0;
}

size_t APS5_VABI sceKernelGetDirectMemorySize(void) {
 return DIRECT_MEMORY_SIZE;
}

int APS5_VABI sceKernelMapDirectMemory(void** addr, size_t len, int prot, int flags, int64_t direct_memory_start, size_t alignment) {
 return GuardMemoryCall("sceKernelMapDirectMemory", [&]() -> int {
 void* requested = addr ? *addr : nullptr;
 const int result = DoMapDirect(addr, len, prot, flags, direct_memory_start, alignment);
 if (result != 0) std::fprintf(stderr, "[memory] map direct %p len=0x%zx prot=0x%x flags=0x%x phys=0x%llx align=0x%zx failed: 0x%x\n", requested, len, prot, flags, static_cast<long long>(direct_memory_start), alignment, result);
 return result;
 });
}

int APS5_VABI sceKernelMapDirectMemory2(void** addr, size_t len, int type, int prot, int flags, int64_t direct_memory_start, size_t alignment) {
 return GuardMemoryCall("sceKernelMapDirectMemory2", [&]() -> int {
 (void)type;
 return DoMapDirect(addr, len, prot, flags, direct_memory_start, alignment);
 });
}

int APS5_VABI sceKernelMapFlexibleMemory(void** addr_in_out, size_t len, int prot, int flags) {
 return GuardMemoryCall("sceKernelMapFlexibleMemory", [&]() -> int {
 return _mapFlexible(addr_in_out, len, prot, flags);
 });
}

int APS5_VABI sceKernelSetVirtualRangeName(const void* addr, uint64_t len, const char* name);

int APS5_VABI sceKernelMapNamedDirectMemory(void** addr, size_t len, int prot, int flags, int64_t direct_memory_start, size_t alignment, const char* name) {
 return GuardMemoryCall("sceKernelMapNamedDirectMemory", [&]() -> int {
 const int result = DoMapDirect(addr, len, prot, flags, direct_memory_start, alignment);
 if (result == 0 && name) sceKernelSetVirtualRangeName(*addr, len, name);
 return result;
 });
}

int32_t APS5_VABI sceKernelMapNamedFlexibleMemory(void** addr_in_out, size_t len, int prot, int flags, const char* name) {
 return GuardMemoryCall("sceKernelMapNamedFlexibleMemory", [&]() -> int {
 const int result = _mapFlexible(addr_in_out, len, prot, flags);
 if (result == 0 && name) sceKernelSetVirtualRangeName(*addr_in_out, len, name);
 return result;
 });
}

int APS5_VABI sceKernelMprotect(const void* addr, size_t len, int prot) {
 return GuardMemoryCall("sceKernelMprotect", [&]() -> int {
 return DoMprotect(addr, len, prot);
 });
}

int APS5_VABI sceKernelMunmap(uint64_t vaddr, size_t len) {
 return GuardMemoryCall("sceKernelMunmap", [&]() -> int {
 const int result = DoMunmap(reinterpret_cast<void*>(vaddr), len);
 if (result == 0) _releaseFlexible(static_cast<uintptr_t>(vaddr), len);
 return result;
 });
}

int APS5_VABI sceKernelReleaseDirectMemory(int64_t start, size_t len) {
 return GuardMemoryCall("sceKernelReleaseDirectMemory", [&]() -> int {
 if (start < 0 || len == 0) return SCE_KERNEL_ERROR_EINVAL;
 UnmapDirectMemory(start, len);
 DirectMemoryFree(start, len);
 return 0;
 });
}

int APS5_VABI sceKernelReserveVirtualRange(void** addr, size_t len, int flags, size_t alignment) {
 return GuardMemoryCall("sceKernelReserveVirtualRange", [&]() -> int {
 void* requested = addr ? *addr : nullptr;
 const int result = DoReserveVirtual(addr, len, flags, alignment);
 if (result != 0) std::fprintf(stderr, "[memory] reserve %p len=0x%zx flags=0x%x align=0x%zx failed: 0x%x\n", requested, len, flags, alignment, result);
 return result;
 });
}

int APS5_VABI sceKernelVirtualQuery(const void* addr, int flags, VirtualQueryInfo* info, uint64_t info_size) {
 if (!info || info_size < sizeof(VirtualQueryInfo)) return SCE_KERNEL_ERROR_EINVAL;
 memset(info, 0, sizeof(VirtualQueryInfo));
 const auto address = reinterpret_cast<uintptr_t>(addr);
 // The mapping that contains the address, or with SCE_KERNEL_VQ_FIND_NEXT (flags bit 0) the first
 // mapping at or above it: titles walk their mappings and check that a mapping covers a whole
 // allocation, so the answer must be the registered allocation, not a page.
 constexpr int findNext = 1;
 const auto lease = GuestAllocations::GuestAllocationsAcquire_nid_postfix();
 const GuestAllocations::Range* best = nullptr;
 for (const auto& range : lease) {
  const auto begin = range->allocationAddress;
  const auto end = begin + range->allocationBytes;
  if (address >= begin && address < end) { best = range.get(); break; }
  if ((flags & findNext) != 0 && begin > address && (best == nullptr || begin < best->allocationAddress)) best = range.get();
 }
 if (best != nullptr) {
  info->start = best->allocationAddress;
  info->end = best->allocationAddress + best->allocationBytes;
  info->protection = (best->readable ? 1 : 0) | (best->writable ? 2 : 0) | (!best->releasable ? 4 : 0);
  int recorded = 0;
  if (GuestProtection(std::max<uintptr_t>(address, info->start), &recorded)) info->protection = recorded;
  std::uintptr_t directStart = 0;
  std::uintptr_t directEnd = 0;
  std::uint64_t physicalOffset = 0;
  int memoryType = 0;
  const bool direct = QueryDirectMapping(std::max<uintptr_t>(address, info->start), &directStart, &directEnd, &physicalOffset, &memoryType);
  info->is_direct = direct ? 1u : 0u;
  info->is_flexible = !direct && best->releasable ? 1u : 0u;
  if (direct) {
   info->start = std::max(info->start, directStart);
   info->end = std::min(info->end, directEnd);
   info->memory_type = memoryType;
  }
  info->is_committed = 1;
  ApplyRangeName(std::max<uintptr_t>(address, info->start), info);
  if (direct) info->offset = physicalOffset + info->start - directStart;
  return 0;
 }
 // Memory the registry does not know (the title's own heap blocks, stacks): the host's committed
 // region around the address is the honest extent; unmapped memory is an error, as on the PS5.
 #ifdef _WIN32
 MEMORY_BASIC_INFORMATION host{};
 if (VirtualQuery(addr, &host, sizeof(host)) == 0 || host.State != MEM_COMMIT) return SCE_KERNEL_ERROR_EACCES;
 info->start = reinterpret_cast<uintptr_t>(host.BaseAddress);
 info->end = info->start + host.RegionSize;
 const bool writable = (host.Protect & (PAGE_READWRITE | PAGE_EXECUTE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_WRITECOPY)) != 0;
 const bool executable = (host.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) != 0;
 #else
 std::ifstream maps("/proc/self/maps");
 std::string line;
 bool found = false, writable = false, executable = false;
 while (std::getline(maps, line)) {
  std::istringstream fields(line);
  uintptr_t begin = 0, end = 0;
  char dash = 0;
  std::string perms;
  if (!(fields >> std::hex >> begin >> dash >> end >> perms) || address < begin || address >= end) continue;
  if (perms.size() < 3 || perms[0] != 'r') return SCE_KERNEL_ERROR_EACCES;
  info->start = begin;
  info->end = end;
  writable = perms[1] == 'w';
  executable = perms[2] == 'x';
  found = true;
  break;
 }
 if (!found) return SCE_KERNEL_ERROR_EACCES;
 #endif
 info->protection = 1 | (writable ? 2 : 0) | (executable ? 4 : 0);
 info->is_flexible = 1;
 info->is_committed = 1;
 ApplyRangeName(std::max<uintptr_t>(address, info->start), info);
 return 0;
}

// ---------------------------------------------------------------------------
// Moved as-is (not yet implemented) from the monolithic libkernel/Export.cpp.
// ---------------------------------------------------------------------------

// As sceKernelReleaseDirectMemory, but the whole range must be allocated.
int APS5_VABI sceKernelCheckedReleaseDirectMemory(int64_t start, size_t len) {
 return GuardMemoryCall("sceKernelCheckedReleaseDirectMemory", [&]() -> int {
 if (start < 0 || len == 0 || (static_cast<std::uint64_t>(start) & (PS5_PAGE_SIZE - 1)) != 0 || (len & (PS5_PAGE_SIZE - 1)) != 0) return SCE_KERNEL_ERROR_EINVAL;
 for (std::uint64_t offset = 0; offset < len;) {
  int64_t blockStart = 0;
  int64_t blockEnd = 0;
  int memoryType = 0;
  const auto address = static_cast<std::uint64_t>(start) + offset;
  if (!DirectMemoryFind(static_cast<int64_t>(address), false, &blockStart, &blockEnd, &memoryType) || static_cast<std::uint64_t>(blockStart) > address) return SCE_KERNEL_ERROR_ENOENT;
  offset = static_cast<std::uint64_t>(blockEnd) - static_cast<std::uint64_t>(start);
 }
 return sceKernelReleaseDirectMemory(start, len);
 });
}

int APS5_VABI sceKernelMtypeprotect(const void* addr, size_t len, int type, int prot) {
 return GuardMemoryCall("sceKernelMtypeprotect", [&]() -> int {
 (void)type;
 return DoMprotect(addr, len, prot);
 });
}

int APS5_VABI sceKernelQueryMemoryProtection(void* addr, void** start, void** end, int* prot) {
 VirtualQueryInfo info{};
 const int result = sceKernelVirtualQuery(addr, 0, &info, sizeof(info));
 if (result != 0) return result;
 if (start) *start = reinterpret_cast<void*>(info.start);
 if (end) *end = reinterpret_cast<void*>(info.end);
 if (prot) *prot = info.protection;
 return 0;
}

int APS5_VABI sceKernelIsStack(void* addr, void** start, void** end) {
 (void)addr;
 (void)start;
 (void)end;
 NotImplemented_nid_no_patch(__func__);
 return 0;
}

int APS5_VABI sceKernelAvailableFlexibleMemorySize(size_t* size) {
 if (!size) return SCE_KERNEL_ERROR_EINVAL;
 std::lock_guard lock(g_flexibleLock);
 *size = FLEXIBLE_MEMORY_SIZE - std::min(FLEXIBLE_MEMORY_SIZE, _flexibleUsedLocked());
 return 0;
}

int APS5_VABI sceKernelConfiguredFlexibleMemorySize(size_t* size) {
 if (!size) return SCE_KERNEL_ERROR_EINVAL;
 *size = FLEXIBLE_MEMORY_SIZE;
 return 0;
}

int APS5_VABI sceKernelSetVirtualRangeName(const void* addr, uint64_t len, const char* name) {
 const auto start = reinterpret_cast<uintptr_t>(addr);
 if (!addr || len == 0 || !name || len > UINTPTR_MAX - start) return SCE_KERNEL_ERROR_EINVAL;
 std::lock_guard lock(g_rangeNameLock);
 EraseRangeNames(start, start + len);
 g_rangeNames.emplace(start, NamedRange{start + len, std::string(name, strnlen(name, 31))});
 return 0;
}

int APS5_VABI sceKernelClearVirtualRangeName(const void* addr, uint64_t len) {
 const auto start = reinterpret_cast<uintptr_t>(addr);
 if (!addr || len == 0 || len > UINTPTR_MAX - start) return SCE_KERNEL_ERROR_EINVAL;
 std::lock_guard lock(g_rangeNameLock);
 EraseRangeNames(start, start + len);
 return 0;
}

int APS5_VABI sceKernelGetPageTableStats(int* cpu_total, int* cpu_available, int* gpu_total, int* gpu_available) {
 (void)cpu_total;
 (void)cpu_available;
 (void)gpu_total;
 (void)gpu_available;
 NotImplemented_nid_no_patch(__func__);
 return 0;
}

int APS5_VABI sceKernelGetPrtAperture(int index, void** addr, size_t* len) {
 if (index < 0 || index >= PRT_APERTURE_COUNT || !addr || !len) return SCE_KERNEL_ERROR_EINVAL;
 std::lock_guard lock(g_prtLock);
 *addr = g_prtApertures[index].address;
 *len = g_prtApertures[index].length;
 return 0;
}

int APS5_VABI sceKernelSetPrtAperture(int index, void* addr, size_t len) {
 if (index < 0 || index >= PRT_APERTURE_COUNT) return SCE_KERNEL_ERROR_EINVAL;
 std::lock_guard lock(g_prtLock);
 g_prtApertures[index] = {addr, len};
 return 0;
}

int APS5_VABI sceKernelBatchMap2(KernelBatchMapEntry* entries, int num_entries, int* num_entries_out, int flags);

int APS5_VABI sceKernelBatchMap(KernelBatchMapEntry* entries, int num_entries, int* num_entries_out) {
 constexpr int MapFixed = 0x10;
 return sceKernelBatchMap2(entries, num_entries, num_entries_out, MapFixed);
}

int APS5_VABI sceKernelBatchMap2(KernelBatchMapEntry* entries, int num_entries, int* num_entries_out, int flags) {
 return GuardMemoryCall("sceKernelBatchMap2", [&]() -> int {
 if (!entries || num_entries < 0) return SCE_KERNEL_ERROR_EINVAL;
 BatchMapTrace trace{num_entries, flags};
 constexpr int OpMapDirect = 0;
 constexpr int OpUnmap = 1;
 constexpr int OpProtect = 2;
 constexpr int OpMapFlexible = 3;
 constexpr int OpTypeProtect = 4;
 int processed = 0;
 int result = 0;
 for (; processed < num_entries; ++processed) {
  auto& entry = entries[processed];
  const auto operation = entry.operation;
  if (trace.active) trace.Operation(operation);
  switch (operation) {
  case OpMapDirect: {
   if (trace.active) trace.Entry(entry.length);
   BatchDirectTimer timer{trace};
   result = DoMapDirect(&entry.start, entry.length, static_cast<uint8_t>(entry.protection), flags, static_cast<int64_t>(entry.offset), 0);
   break;
  }
  case OpUnmap:
   if (trace.active) trace.Entry(entry.length);
   result = sceKernelMunmap(reinterpret_cast<uint64_t>(entry.start), entry.length);
   break;
  case OpProtect:
  case OpTypeProtect:
   if (trace.active) trace.Entry(entry.length);
   result = DoMprotect(entry.start, entry.length, static_cast<uint8_t>(entry.protection));
   break;
  case OpMapFlexible:
   if (trace.active) trace.Entry(entry.length);
   result = _mapFlexible(&entry.start, entry.length, static_cast<uint8_t>(entry.protection), flags);
   break;
  default:
   throw std::invalid_argument("sceKernelBatchMap2: unsupported operation " + std::to_string(entry.operation));
  }
  if (result != 0) { if(trace.active) trace.error=result; break; }
  if (trace.active) ++trace.succeeded;
 }
 if (num_entries_out) *num_entries_out = processed;
 return result;
 });
}

}

extern "C" {

int APS5_VABI sceKernelMlock_nid_postfix(void* address, std::uint64_t length) {
    if (!address || length == 0 || length > static_cast<std::uint64_t>(SIZE_MAX)) return SCE_KERNEL_ERROR_EINVAL;
#ifdef _WIN32
    return VirtualLock(address, static_cast<SIZE_T>(length)) ? 0 : SCE_KERNEL_ERROR_ENOMEM;
#else
    return ::mlock(address, static_cast<std::size_t>(length)) == 0 ? 0 : SCE_KERNEL_ERROR_ENOMEM;
#endif
}

int APS5_VABI sceKernelMunlock() {
    return 0;
}

}
