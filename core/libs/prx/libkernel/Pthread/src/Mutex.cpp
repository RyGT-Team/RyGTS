#include "../include/Pthread.hpp"
#include "../include/Mutex.hpp"
#include "prx/libkernel/KernelErrors.hpp"
#include "prx/libkernel/Time/include/TimedWait.hpp"
#include "prx/libkernel/Time/include/Time.hpp"
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <limits>
#include <memory>
#include <stdexcept>

namespace {

constexpr int sceNotPermitted = static_cast<int>(0x80020001u);
constexpr int sceDeadlock = static_cast<int>(0x8002000bu);
constexpr int sceBusy = static_cast<int>(0x80020010u);
constexpr int sceTimedOut = static_cast<int>(0x8002003cu);
std::mutex initializationMutex;

// PERF-GTS-269 (RYGT_MUTEX_SPIN=<tries>, default 64, 0 = off): a lock that is held is tried a
// few more times before the thread blocks. The host mutex blocks in the kernel at once and its
// unlock then has to wake the waiter there; GT Sport's twelve job threads share short locks, and
// at the Autopolis race start they spent 13 to 29 ms a frame in scePthreadMutexLock.
int MutexSpinTries() {
    static const int tries = [] {
        const char* env = std::getenv("RYGT_MUTEX_SPIN");
        if (!env || !*env) return 64;
        const long value = std::strtol(env, nullptr, 10);
        return static_cast<int>(value < 0 ? 0 : value > 4096 ? 4096 : value);
    }();
    return tries;
}

template<typename TNative>
bool SpinAcquire(TNative& native) {
    const int tries = MutexSpinTries();
    for (int i = 0; i < tries; ++i) {
        if (native.try_lock()) return true;
        for (int pause = 0; pause < 16; ++pause) __builtin_ia32_pause();
    }
    return false;
}

PthreadMutex destroyedMutex() {
    return reinterpret_cast<PthreadMutex>(std::uintptr_t{2});
}

bool isInitializedMutex(PthreadMutex mutex) {
    return mutex && mutex != destroyedMutex() && reinterpret_cast<std::uintptr_t>(mutex) != 1;
}

PthreadMutex resolveMutex(PthreadMutex* mutex, bool initialize) {
    if (!mutex)
        return nullptr;
    std::atomic_ref<PthreadMutex> slot(*mutex);
    if (const auto current = slot.load(std::memory_order_acquire); isInitializedMutex(current))
        return current;
    std::lock_guard lock(initializationMutex);
    const auto current = slot.load(std::memory_order_acquire);
    // Invalid guest handles must return through the pthread ABI, not unwind through host threads.
    if (current == destroyedMutex() || reinterpret_cast<std::uintptr_t>(current) == 1)
        return nullptr;
    if (current)
        return current;
    if (!initialize)
        return nullptr;
    auto* created = new PthreadMutexPrivate();
    slot.store(created, std::memory_order_release);
    return created;
}

template<typename TAcquire>
int acquireMutex(PthreadMutex mutex, TAcquire acquire, int unavailable, bool tryOnly) {
    const auto thread = std::this_thread::get_id();
    const bool owned = mutex->_owner.load(std::memory_order_acquire) == thread;
    if (owned && mutex->_type != MutexType::Recursive) {
        if (tryOnly)
            return sceBusy;
        if (mutex->_type == MutexType::ErrorCheck)
            return sceDeadlock;
        throw std::runtime_error("Mutex is already owned by the current thread");
    }
    if (owned && mutex->_count == std::numeric_limits<int>::max())
        throw std::overflow_error("Recursive mutex lock count overflow");
    // RYGT_DIAG: a lock that is not free at once is timed as a contended wait.
    const auto timedAcquire = [&](auto& native) {
        if (tryOnly || !KernelDiagEnabled() || native.try_lock())
            return tryOnly || !KernelDiagEnabled() ? acquire(native) : true;
        KernelDiagScope diag(KernelDiagZone::Mutex, nullptr);
        return acquire(native);
    };
    if (mutex->_type == MutexType::Recursive) {
        if (!timedAcquire(mutex->_rmtx))
            return unavailable;
        ++mutex->_count;
    } else {
        if (!timedAcquire(mutex->_mtx))
            return unavailable;
    }
    mutex->_owner.store(thread, std::memory_order_release);
    return 0;
}

}

int MutexOperations::Timedlock(PthreadMutex* mutex, const KernelTimespec* abstime) {
    if (!abstime || abstime->tv_sec < 0 || abstime->tv_nsec < 0 || abstime->tv_nsec >= 1000000000)
        return SCE_KERNEL_ERROR_EINVAL;
    auto* resolved = resolveMutex(mutex, true);
    if (!resolved) return SCE_KERNEL_ERROR_EINVAL;
    const auto maximum = std::chrono::nanoseconds::max().count();
    if (abstime->tv_sec > (maximum - abstime->tv_nsec) / 1000000000)
        throw std::overflow_error("Absolute mutex timeout exceeds the host clock range");
    const auto duration = std::chrono::nanoseconds(abstime->tv_sec * 1000000000 + abstime->tv_nsec);
    if (std::chrono::duration<long double>(duration) >= std::chrono::duration<long double>(std::chrono::system_clock::duration::max()))
        throw std::overflow_error("Absolute mutex timeout exceeds the host clock range");
    const auto deadline = std::chrono::system_clock::time_point(std::chrono::duration_cast<std::chrono::system_clock::duration>(duration));
    return acquireMutex(resolved, [&](auto& native) { return native.try_lock_until(deadline); }, sceTimedOut, false);
}

extern "C" {

int APS5_VABI scePthreadMutexattrInit(PthreadMutexattr* attr) {
    if (!attr)
        throw std::invalid_argument("Mutex attribute pointer is null");
    *attr = new PthreadMutexattrPrivate{MutexType::Normal};
    return 0;
}

int APS5_VABI scePthreadMutexattrDestroy(PthreadMutexattr* attr) {
    if (!attr || !*attr)
        throw std::invalid_argument("Mutex attributes are not initialized");
    delete *attr;
    *attr = nullptr;
    return 0;
}

int APS5_VABI scePthreadMutexattrSettype(PthreadMutexattr* attr, int type) {
    if (!attr || !*attr)
        throw std::invalid_argument("Mutex attributes are not initialized");
    switch (type) {
    case 1: (*attr)->type = MutexType::ErrorCheck; break;
    case 2: (*attr)->type = MutexType::Recursive; break;
    case 3: (*attr)->type = MutexType::Normal; break;
    case 4: (*attr)->type = MutexType::Normal; break;
    default: throw std::invalid_argument("Invalid mutex type");
    }
    return 0;
}

int APS5_VABI scePthreadMutexattrSetprotocol(PthreadMutexattr* attr, int protocol) {
    if (!attr || !*attr)
        throw std::invalid_argument("Mutex attributes are not initialized");
    constexpr int PrioNone = 0;
    constexpr int PrioInherit = 1;
    if (protocol != PrioNone && protocol != PrioInherit)
        throw std::invalid_argument("Mutex priority protection is unsupported");
    return 0;
}

int APS5_VABI scePthreadMutexInit(PthreadMutex* mutex, const PthreadMutexattr* attr, const char*) {
    if (!mutex)
        throw std::invalid_argument("Mutex pointer is null");
    if (attr && !*attr)
        throw std::invalid_argument("Mutex attributes are not initialized");
    auto replacement = std::make_unique<PthreadMutexPrivate>();
    if (attr)
        replacement->_type = (*attr)->type;
    std::lock_guard lock(initializationMutex);
    std::atomic_ref<PthreadMutex>(*mutex).store(replacement.release(), std::memory_order_release);
    return 0;
}

int APS5_VABI scePthreadMutexDestroy(PthreadMutex* mutex) {
    if (!mutex)
        return SCE_KERNEL_ERROR_EINVAL;
    std::lock_guard lock(initializationMutex);
    std::atomic_ref<PthreadMutex> slot(*mutex);
    const auto current = slot.load(std::memory_order_acquire);
    if (current == destroyedMutex() || reinterpret_cast<std::uintptr_t>(current) == 1)
        return SCE_KERNEL_ERROR_EINVAL;
    if (current && current->_owner.load(std::memory_order_acquire) != std::thread::id{})
        return SCE_KERNEL_ERROR_EBUSY;
    delete current;
    slot.store(destroyedMutex(), std::memory_order_release);
    return 0;
}

int APS5_VABI scePthreadMutexLock(PthreadMutex* mutex) {
    auto* resolved = resolveMutex(mutex, true);
    if (!resolved) return SCE_KERNEL_ERROR_EINVAL;
    return acquireMutex(resolved, [](auto& native) {
        if (!SpinAcquire(native)) native.lock();
        return true;
    }, 0, false);
}

int APS5_VABI scePthreadMutexUnlock(PthreadMutex* mutex) {
    auto* current = resolveMutex(mutex, false);
    if (!current) return SCE_KERNEL_ERROR_EINVAL;
    if (current->_owner.load(std::memory_order_acquire) != std::this_thread::get_id())
        return sceNotPermitted;
    if (current->_type == MutexType::Recursive) {
        if (--current->_count == 0)
            current->_owner.store(std::thread::id{}, std::memory_order_release);
        current->_rmtx.unlock();
    } else {
        current->_owner.store(std::thread::id{}, std::memory_order_release);
        current->_mtx.unlock();
    }
    return 0;
}

int APS5_VABI scePthreadMutexTimedlock(PthreadMutex* mutex, KernelUseconds usec) {
    const auto deadline = TimedWait::DeadlineNanos(usec);
    return acquireMutex(resolveMutex(mutex, true), [=](auto& native) {
        return TimedWait::AcquireUntil(deadline, [&] { return native.try_lock(); }, [&](std::uint64_t micros) { return native.try_lock_for(std::chrono::microseconds(micros)); });
    }, sceTimedOut, false);
}

int APS5_VABI scePthreadMutexTrylock(PthreadMutex* mutex) {
    auto* resolved = resolveMutex(mutex, true);
    if (!resolved) return SCE_KERNEL_ERROR_EINVAL;
    return acquireMutex(resolved, [](auto& native) { return native.try_lock(); }, sceBusy, true);
}

}
