#ifndef CORE_LIBS_PRX_LIBKERNEL_TIME_INCLUDE_TIME_HPP
#define CORE_LIBS_PRX_LIBKERNEL_TIME_INCLUDE_TIME_HPP

#include <chrono>
#include <cstdint>
#include "SceTypes.hpp"
#include "prx/libc/include/general/VabiMacros.hpp"

extern "C" {

    std::uint64_t APS5_VABI sceKernelGetProcessTime();
    std::uint64_t APS5_VABI sceKernelGetProcessTimeCounter();
    std::uint64_t APS5_VABI sceKernelGetProcessTimeCounterFrequency();
    int APS5_VABI sceKernelUsleep_nid_postfix(KernelUseconds microseconds);
    int APS5_VABI sceKernelNanosleep(const KernelTimespec* rqtp, KernelTimespec* rmtp);
    int APS5_VABI nanosleep_nid_postfix(const KernelTimespec* rqtp, KernelTimespec* rmtp);
    int APS5_VABI clock_gettime_nid_postfix(int clockId, KernelTimespec* tp);
    int APS5_VABI clock_getres_nid_postfix(int clockId, KernelTimespec* res);
    int APS5_VABI gettimeofday_nid_postfix(KernelTimeval* tv, KernelTimezone* tz);

    // Debug aid: APS5_TRACE_WAITS=1 aggregates blocking waits (event flags, semaphores, condition
    // variables, event queues) per guest call site and prints the sites that wait longest every 3 s,
    // with their timeout rate; a site that times out once per frame is a lost wake-up.
    void KernelTraceWait_nid_postfix(const char* kind, const void* caller, std::uint64_t waitedNanos, bool timedOut);

}

// SHARED_DIAG=1: blocking waits, sleeps, contended mutexes and file reads are timed for the renderer's
// stutter diagnostics (libSceVideoOut.prx's RyGtDiagRecordNs). Zones as in shadGT's ryty_diag.h.
enum class KernelDiagZone : std::uint32_t { Equeue = 50, Sema = 51, EventFlag = 52, Mutex = 53, Cond = 54, Sleep = 55, FileRead = 56, CondWoken = 57, EqueueWoken = 58 };
bool KernelDiagEnabled();
void KernelDiagRecord(KernelDiagZone zone, std::uint64_t nanos, const void* caller);
/// A mark with a raw argument (CondWoken: signalling thread id << 32 | its call site's offset in
/// the executable; EqueueWoken: the first event's filter << 48 | its ident).
void KernelDiagMark(KernelDiagZone zone, std::uint64_t arg);
/// The calling thread's id and a call site as an offset in the executable, packed for CondWoken.
std::uint64_t KernelDiagWaker(const void* caller);

class KernelDiagScope {
public:
    KernelDiagScope(KernelDiagZone zone, const void* caller) : m_zone(zone), m_caller(caller) {
        if (KernelDiagEnabled()) m_start = std::chrono::steady_clock::now();
    }
    ~KernelDiagScope() {
        if (m_start != std::chrono::steady_clock::time_point{})
            KernelDiagRecord(m_zone, static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - m_start).count()), m_caller);
    }
    KernelDiagScope(const KernelDiagScope&) = delete;
    KernelDiagScope& operator=(const KernelDiagScope&) = delete;

private:
    KernelDiagZone m_zone;
    const void* m_caller;
    std::chrono::steady_clock::time_point m_start{};
};

#endif
