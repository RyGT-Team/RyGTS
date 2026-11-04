#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <deque>
#include <filesystem>
#include <future>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

static void Check(bool condition) {
    if (!condition) {
        std::fputs("disk-store wait/Flush check failed\n", stderr);
        std::abort();
    }
}
struct CompiledVariant { std::uint64_t id; };
static constexpr unsigned FormatVersion = 3;
static std::uint64_t SourceVersion() { return 0; }
static std::string hex(std::uint64_t) { return "fixture"; }

class StoreFixture {
public:
    void BlockNextWrite() {
        std::lock_guard lock(testMutex);
        blockWrite = true;
        entered = false;
    }
    void AwaitBlockedWrite() {
        std::unique_lock lock(testMutex);
        Check(testWake.wait_for(lock, std::chrono::seconds(2), [&] { return entered; }));
    }
    void ReleaseWrite() {
        std::lock_guard lock(testMutex);
        blockWrite = false;
        testWake.notify_all();
    }
    void CheckWritten(std::uint64_t count) {
        std::lock_guard lock(testMutex);
        Check(written.size() == count);
        for (std::uint64_t i = 0; i < count; ++i) Check(written[i] == i);
    }
protected:
    struct Job {
        std::vector<std::byte> key;
        std::shared_ptr<const CompiledVariant> variant;
    };
    void housekeeping() {}
    void report(bool) {}
    void write(const Job& job) {
        std::unique_lock lock(testMutex);
        Check(job.variant && job.key.size() == 1);
        Check(job.key[0] == std::byte(job.variant->id & 0xff));
        entered = true;
        testWake.notify_all();
        testWake.wait(lock, [&] { return !blockWrite; });
        written.push_back(job.variant->id);
    }
    std::filesystem::path directory{"disk-store-fixture"};
    std::mutex mutex;
    std::condition_variable wake;
    std::condition_variable idle;
    std::deque<Job> jobs;
    bool writing = false;
private:
    std::mutex testMutex;
    std::condition_variable testWake;
    bool blockWrite = false;
    bool entered = false;
    std::vector<std::uint64_t> written;
};
#include "disk_store_wait_methods.inc"

template <typename Store>
static void Verify() {
    // Production uses a process-lifetime writer/store too. Leaving this instance
    // alive avoids introducing a stop protocol absent from the extracted code.
    auto* store = new Store;
    std::thread([store] { store->run(); }).detach();
    store->BlockNextWrite();
    auto first = std::make_shared<const CompiledVariant>(CompiledVariant{0});
    std::weak_ptr<const CompiledVariant> owned = first;
    store->Store({std::byte{0}}, std::move(first));
    store->AwaitBlockedWrite();
    std::promise<void> flushing;
    auto started = flushing.get_future();
    auto flushed = std::async(std::launch::async, [store, &flushing] {
        flushing.set_value();
        store->Flush();
    });
    Check(started.wait_for(std::chrono::seconds(2)) == std::future_status::ready);
    Check(flushed.wait_for(std::chrono::milliseconds(20)) == std::future_status::timeout);
    Check(!owned.expired());
    store->ReleaseWrite();
    Check(flushed.wait_for(std::chrono::seconds(2)) == std::future_status::ready);
    flushed.get();
    Check(owned.expired());
    store->CheckWritten(1);
    // Each Flush leaves the writer idle before the next publication; bursts
    // check that jobs are ordered and no notification is needed beyond Store.
    std::uint64_t next = 1;
    for (unsigned round = 0; round < 8; ++round) {
        for (unsigned item = 0; item < 32; ++item, ++next) {
            auto value = std::make_shared<const CompiledVariant>(CompiledVariant{next});
            store->Store({std::byte(next & 0xff)}, std::move(value));
        }
        store->Flush();
        store->CheckWritten(next);
    }
}
int main() {
    Verify<TimedBaseline>();
    Verify<Candidate>();
    std::puts("PASS actual DiskStore Store/Flush/run: blocked writes, ownership, idle publication and ordered bursts");
}
