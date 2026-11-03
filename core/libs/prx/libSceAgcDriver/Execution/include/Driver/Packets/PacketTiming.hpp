#ifndef CORE_LIBS_PRX_LIBSCEAGCDRIVER_EXECUTION_INCLUDE_DRIVER_PACKETTIMING_HPP
#define CORE_LIBS_PRX_LIBSCEAGCDRIVER_EXECUTION_INCLUDE_DRIVER_PACKETTIMING_HPP

#include "prx/libSceAgcDriver/Execution/include/Driver/Dispatch/DispatchTiming.hpp"
#include <array>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <map>
#include <utility>

namespace AgcDriver::DriverDetail {

struct WorkerProfile {
    double dispatchMs = 0;
    double drawMs = 0;
    double waitMs = 0;
#if !defined(NDEBUG)
    std::chrono::steady_clock::time_point start = std::chrono::steady_clock::now();
#else
    std::chrono::steady_clock::time_point start{};
#endif
    std::chrono::steady_clock::time_point reported = start;
};

struct PacketProfile {
    std::map<std::uint32_t, std::pair<std::uint64_t, double>> byOpcode;
#if !defined(NDEBUG)
    std::chrono::steady_clock::time_point lastReport = std::chrono::steady_clock::now();
#else
    std::chrono::steady_clock::time_point lastReport{};
#endif
    std::uint64_t submissions = 0;

    double flushMs = 0;

    std::array<std::pair<std::uint64_t, double>, static_cast<std::size_t>(DispatchOutcome::Count)> dispatchOutcomes{};
};

struct PacketTimer {
    bool enabled; std::uint32_t key; std::uint32_t queue; PacketProfile& profile; std::chrono::steady_clock::time_point start;
    ~PacketTimer();
};

}

#endif
