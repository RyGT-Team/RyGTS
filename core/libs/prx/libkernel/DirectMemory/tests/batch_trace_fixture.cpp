#include <windows.h>
#include <algorithm>
#include <atomic>
#include <cassert>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <limits>
#include <stdexcept>
#include <string>
struct KernelBatchMapEntry { void* start; std::uint64_t offset,length; char protection; int operation; };
#define APS5_VABI
constexpr int SCE_KERNEL_ERROR_EINVAL=-1;
static int calls=0, fail_at=-1;
static int DoMapDirect(void**,std::size_t,int,int,std::int64_t,std::size_t) { return ++calls==fail_at ? -9 : 0; }
static int sceKernelMunmap(std::uint64_t,std::size_t) { ++calls; return 0; }
static int DoMprotect(void*,std::size_t,int) { ++calls; return 0; }
static int _mapFlexible(void**,std::size_t,int,int) { ++calls; return 0; }
template<class F> static int GuardMemoryCall(const char*, F f) { try { return f(); } catch (...) { return -2; } }
#include "batch-trace-under-test.inc"
#include "batch-map-under-test.inc"
int main() {
 _putenv_s("RYGT_BATCH_MAP_TRACE","1");
 CreateDirectoryA("logs",nullptr);
 char marker[128]; std::snprintf(marker,sizeof(marker),"logs/batch-map-%lu.capture",GetCurrentProcessId());
 DeleteFileA(marker);
 assert(std::freopen("batch-test.log","w",stderr));
 KernelBatchMapEntry entries[]={{reinterpret_cast<void*>(0x10000),0,0x4000,3,0},
                                {reinterpret_cast<void*>(0x14000),0x4000,0x4000,3,0},
                                {reinterpret_cast<void*>(0x18000),0x8000,0x4000,3,0}};
 int processed=-1;
 assert(sceKernelBatchMap2(entries,3,&processed,0)==0 && processed==3 && calls==3);
 FILE* marker_file=std::fopen(marker,"w"); assert(marker_file); std::fclose(marker_file);
 for (int i=0;i<300;++i) { processed=-1; assert(sceKernelBatchMap2(entries,0,&processed,0)==0 && processed==0 && calls==3); }
 SYSTEM_INFO info{}; GetSystemInfo(&info);
 auto* pages=static_cast<char*>(VirtualAlloc(nullptr,2*info.dwPageSize,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE)); assert(pages);
 auto* guarded=reinterpret_cast<KernelBatchMapEntry*>(pages+info.dwPageSize-sizeof(void*));
 guarded->offset=0; guarded->length=0; guarded->protection=0; guarded->operation=0;
 DWORD prior{}; assert(VirtualProtect(pages,info.dwPageSize,PAGE_NOACCESS,&prior));
 calls=0; fail_at=1; processed=-1;
 assert(sceKernelBatchMap2(guarded,1,&processed,0)==-9 && processed==0 && calls==1);
 assert(VirtualFree(pages,0,MEM_RELEASE));
 calls=0; fail_at=2; processed=-1;
 assert(sceKernelBatchMap2(entries,3,&processed,0)==-9 && processed==1 && calls==2);
 fail_at=-1; calls=0; entries[1].operation=99; processed=-1;
 assert(sceKernelBatchMap2(entries,3,&processed,0)==-2 && processed==-1 && calls==1);
 entries[1].operation=0;
 for (int i=0;i<260;++i) assert(sceKernelBatchMap2(entries,3,&processed,0)==0 && processed==3);
 std::fflush(stderr); FILE* log=std::fopen("batch-test.log","r"); assert(log);
 char line[2048]; int count=0; bool partial=false, exception=false;
 while(std::fgets(line,sizeof(line),log)) { ++count; partial |= std::strstr(line,"attempted=2 processed=1 error=-9 exception=0") != nullptr;
   exception |= std::strstr(line,"attempted=2 processed=1 error=0 exception=1") != nullptr; }
 std::fclose(log); assert(count==256 && partial && exception);
}
