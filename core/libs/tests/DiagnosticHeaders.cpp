#include "prx/libkernel/Time/include/Time.hpp"
#include "prx/libc/include/general/LogMacros.hpp"
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <string_view>
#include <type_traits>

static void Check(bool value) { if (!value) std::abort(); }
#if !defined(NDEBUG)
static unsigned records;
bool KernelDiagEnabled() { return true; }
void KernelDiagRecord(KernelDiagZone, std::uint64_t, const void*) { ++records; }
#endif

int main(int argc, char** argv) {
    Check(argc == 2);
    const std::string_view level = argv[1];
    { std::ofstream config("RyGT.ini"); config << "stdout_level=" << level << '\n'; }
    unsigned trace = 0, debug = 0, errors = 0;
    APS5_LOG_TRACE("trace %u", ++trace);
    APS5_LOG_DEBUG("debug %u", ++debug);
    APS5_LOG_ERR("retained error %u", ++errors);
    Check(trace == (level == "Trace" ? 1u : 0u));
    Check(debug == (level == "Trace" || level == "Debug" ? 1u : 0u));
    Check(errors == 1);
#if defined(NDEBUG)
    static_assert(std::is_empty_v<KernelDiagScope>);
    static_assert(!KernelDiagEnabled());
    Check(KernelDiagWaker(nullptr) == 0);
    KernelDiagRecord(KernelDiagZone::Sleep, 1, nullptr);
    KernelDiagMark(KernelDiagZone::Sleep, 1);
    { KernelDiagScope scope{KernelDiagZone::Sleep, nullptr}; }
#else
    { KernelDiagScope scope{KernelDiagZone::Sleep, nullptr}; }
    Check(records == 1);
#endif
    std::puts("PASS runtime diagnostic headers and configurable logging");
}
