#include <atomic>
#include <cstdlib>
#ifdef _WIN32
#include <windows.h>
#endif
#include <cstdint>
#include <cstdio>
#include <cstddef>
#include "SceTypes.hpp"
#include "prx/libSceSaveData/SaveDataCore.hpp"
#include "prx/libc/include/General.hpp"
#include "prx/libkernel/AppMetadata/include/AppMetadata.hpp"
#include <algorithm>
#include <cstring>
#include <iterator>
#include <mutex>

// Savedata imports bind by NID either to libSceSaveData.native or (via the relinker alias table,
// by name) to this library. Every entry point used to return SoftFailNp (0x800242e2), which is the
// NP-family "online service not available" code - wrong dialect for a purely local save call and
// fatal-looking for callers. All entry points now delegate to SaveDataCore.hpp: a local,
// file-backed implementation that needs no PSN account and tolerates any userId
// (0 / 65535 / 268435456), mirroring the native library.

namespace {
// Logs the first three calls of each entry point, then stays silent, keeping guest logs readable.
#ifndef NDEBUG
#define SAVEDATA_TRACE(...) \
    do { \
        static std::atomic<int> traceCount{0}; \
        if (traceCount.fetch_add(1, std::memory_order_relaxed) < 3) { \
            std::fprintf(stderr, "[SAVEDATA:core] " __VA_ARGS__); \
            std::fputc('\n', stderr); \
            std::fflush(stderr); \
        } \
    } while (0)
#else
#define SAVEDATA_TRACE(...) do {} while (0)
#endif
}  // namespace

extern "C" {

int APS5_VABI sceSaveDataInitialize3(const void* init) {
    const int rc = savedata::initialize_internal(init);
    SAVEDATA_TRACE("sceSaveDataInitialize3 -> 0x%08x", static_cast<unsigned>(rc));
    return rc;
}
int APS5_VABI sceSaveDataTerminate(void) {
    const int rc = savedata::terminate_internal();
    SAVEDATA_TRACE("sceSaveDataTerminate -> 0x%08x", static_cast<unsigned>(rc));
    return rc;
}
int APS5_VABI sceSaveDataBackup(const SaveDataBackup* backup) {
    const int rc = savedata::backup_internal(backup, nullptr);
    SAVEDATA_TRACE("sceSaveDataBackup -> 0x%08x", static_cast<unsigned>(rc));
    return rc;
}
int APS5_VABI sceSaveDataGetEventResult(const void* event_param, SaveDataEvent* event) {
    const int rc = savedata::get_event_result_internal(event_param, event);
    SAVEDATA_TRACE("sceSaveDataGetEventResult -> 0x%08x", static_cast<unsigned>(rc));
    return rc;
}
int APS5_VABI sceSaveDataGetSaveDataMemory2(SaveDataMemoryGet2* get_param) {
    if (get_param == nullptr) {
        return savedata::SD_ERROR_PARAMETER;
    }
    const int rc = savedata::get_memory_internal(get_param);
    SAVEDATA_TRACE("get2 user=%d slot=%u -> 0x%08x", static_cast<int>(get_param->user_id),
             static_cast<unsigned>(get_param->slot_id), static_cast<unsigned>(rc));
    return rc;
}
int APS5_VABI sceSaveDataSetSaveDataMemory2(const SaveDataMemorySet2* set_param) {
    if (set_param == nullptr) {
        return savedata::SD_ERROR_PARAMETER;
    }
    const int rc = savedata::set_memory_internal(set_param);
    SAVEDATA_TRACE("set2 user=%d slot=%u num=%u -> 0x%08x",
             static_cast<int>(set_param->user_id), static_cast<unsigned>(set_param->slot_id),
             static_cast<unsigned>(set_param->data_num), static_cast<unsigned>(rc));
    return rc;
}
int APS5_VABI sceSaveDataSetupSaveDataMemory2(const SaveDataMemorySetup2* setup_param,
                                              SaveDataMemorySetupResult* result) {
    if (setup_param != nullptr) {
        SAVEDATA_TRACE("setup2 option=0x%x user=%d size=0x%zx icon=0x%zx param=%p slot=%u",
                 static_cast<unsigned>(setup_param->option), static_cast<int>(setup_param->user_id),
                 setup_param->memory_size, setup_param->icon_memory_size,
                 static_cast<const void*>(setup_param->init_param), static_cast<unsigned>(setup_param->slot_id));
    }
    const int rc = savedata::setup_memory_internal(setup_param, result, nullptr);
    SAVEDATA_TRACE("setup2 -> 0x%08x existed=0x%zx", static_cast<unsigned>(rc),
             result != nullptr ? result->existed_memory_size : static_cast<std::size_t>(0));
    return rc;
}
int APS5_VABI sceSaveDataSyncSaveDataMemory(const void* sync_param) {
    if (sync_param == nullptr) {
        return savedata::SD_ERROR_PARAMETER;
    }
    const auto* sync = static_cast<const savedata::MemorySync*>(sync_param);
    const int rc = savedata::sync_memory_internal(sync->user_id, sync->slot_id, sync->option, nullptr, true);
    SAVEDATA_TRACE("sync user=%d slot=%u option=%u -> 0x%08x", static_cast<int>(sync->user_id),
             static_cast<unsigned>(sync->slot_id), static_cast<unsigned>(sync->option),
             static_cast<unsigned>(rc));
    return rc;
}
int APS5_VABI sceSaveDataTransferringMount(const SaveDataTransferringMount* mount,
                                           SaveDataMountResult* mount_result) {
    const int rc = savedata::transferring_mount_internal(mount, mount_result);
    SAVEDATA_TRACE("sceSaveDataTransferringMount -> 0x%08x", static_cast<unsigned>(rc));
    return rc;
}

// GT Sport's boot script assumes loading outlasts the boot screen's setup: the boot screen starts
// listening for "loading done" and only then initializes the flag that handler reads. From fast
// storage the save data loads first, the handler sees the flag unset and the script fails
// (ProductBootScreen.ad:99). For those titles the first save access (the search for the save,
// before the game decides to create one) waits until the renderer has drawn the boot screen; the
// shadGT renderer signals this named event once the game draws steadily. Waiting only at the
// mount was not enough on a fresh save: the game's "Saving" indicator drew first.
static void WaitForBootScreen() {
    static std::once_flag once;
    std::call_once(once, [] {
        static const char* const titles[] = {"CUSA03220", "CUSA02168"};
        const AppTitleId title = GetAppTitleId_nid_postfix();
        if (std::none_of(std::begin(titles), std::end(titles), [&](const char* id) { return std::strcmp(title.value, id) == 0; })) return;
#ifdef _WIN32
        char name[64];
        std::snprintf(name, sizeof(name), "Local\\RytyFirstDraw-%lu", GetCurrentProcessId());
        if (HANDLE event = CreateEventA(nullptr, TRUE, FALSE, name)) {
#ifndef NDEBUG
            const DWORD start = GetTickCount();
#endif
            const DWORD result = WaitForSingleObject(event, 20000);
            (void)result;
            SAVEDATA_TRACE("waited %lu ms for the boot screen before the first save access: %s", GetTickCount() - start,
                           result == WAIT_OBJECT_0 ? "drawn" : "timed out");
            CloseHandle(event);
        }
#endif
    });
}

// The PS4 mount API. Mount3 (PS5) adds system blocks and a transaction resource to this layout.
struct SaveDataMount2 {
    std::int32_t user_id;
    std::int32_t pad;
    const SceSaveDataDirName* dir_name;
    std::uint64_t blocks;
    std::uint32_t mount_mode;
    std::uint8_t reserved[32];
    std::int32_t pad2;
};
static_assert(sizeof(SaveDataMount2) == 64, "SceSaveDataMount2 is 64 bytes");

int APS5_VABI sceSaveDataMount2(const SaveDataMount2* mount, SaveDataMountResult* mount_result) {
    if (mount == nullptr) {
        return savedata::SD_ERROR_PARAMETER;
    }
    WaitForBootScreen();
    const int rc = savedata::mount_internal(mount->dir_name, mount->mount_mode, mount->blocks,
                                            mount->user_id, mount_result, "sceSaveDataMount2");
    SAVEDATA_TRACE("sceSaveDataMount2 dir=%s mode=0x%x blocks=%llu -> 0x%08x point=%s",
                   mount->dir_name != nullptr ? mount->dir_name->data : "(null)", mount->mount_mode,
                   static_cast<unsigned long long>(mount->blocks), static_cast<unsigned>(rc),
                   rc == 0 && mount_result != nullptr ? mount_result->mount_point.data : "");
    return rc;
}
int APS5_VABI sceSaveDataUmount(const SaveDataMountPoint* mount_point) {
    const int rc = savedata::umount_internal(0, mount_point, "sceSaveDataUmount");
    SAVEDATA_TRACE("sceSaveDataUmount -> 0x%08x", static_cast<unsigned>(rc));
    return rc;
}
// No backups are kept, so unmounting with a backup is a plain unmount.
int APS5_VABI sceSaveDataUmountWithBackup(const SaveDataMountPoint* mount_point) {
    const int rc = savedata::umount_internal(0, mount_point, "sceSaveDataUmountWithBackup");
    SAVEDATA_TRACE("sceSaveDataUmountWithBackup -> 0x%08x", static_cast<unsigned>(rc));
    return rc;
}
int APS5_VABI sceSaveDataCheckBackupData(const void* check) {
    (void)check;
    SAVEDATA_TRACE("sceSaveDataCheckBackupData -> not found");
    return savedata::SD_ERROR_NOT_FOUND;
}
int APS5_VABI sceSaveDataRestoreBackupData(const void* restore) {
    (void)restore;
    SAVEDATA_TRACE("sceSaveDataRestoreBackupData -> not found");
    return savedata::SD_ERROR_NOT_FOUND;
}
int APS5_VABI sceSaveDataDirNameSearch(const SaveDataDirNameSearchCond* cond,
                                       SaveDataDirNameSearchResult* result) {
    WaitForBootScreen();
    const int rc = savedata::dir_name_search_internal(cond, result);
    SAVEDATA_TRACE("sceSaveDataDirNameSearch pattern=%s hits=%u -> 0x%08x",
                   cond != nullptr && cond->dir_name != nullptr ? cond->dir_name->data : "(all)",
                   result != nullptr ? result->hit_num : 0u, static_cast<unsigned>(rc));
    return rc;
}
int APS5_VABI sceSaveDataGetParam(const SaveDataMountPoint* mount_point, std::uint32_t param_type,
                                  void* param_buf, std::size_t param_buf_size, std::size_t* got_size) {
    const int rc = savedata::get_param_internal(mount_point, param_type, param_buf, param_buf_size, got_size);
    SAVEDATA_TRACE("sceSaveDataGetParam type=%u -> 0x%08x", param_type, static_cast<unsigned>(rc));
    return rc;
}
int APS5_VABI sceSaveDataSetParam(const SaveDataMountPoint* mount_point, std::uint32_t param_type,
                                  const void* param_buf, std::size_t param_buf_size) {
    const int rc = savedata::set_param_internal(mount_point, param_type, param_buf, param_buf_size);
    SAVEDATA_TRACE("sceSaveDataSetParam type=%u -> 0x%08x", param_type, static_cast<unsigned>(rc));
    return rc;
}
int APS5_VABI sceSaveDataSaveIcon(const SaveDataMountPoint* mount_point, const SaveDataIcon* icon) {
    const int rc = savedata::save_icon_internal(mount_point, icon);
    SAVEDATA_TRACE("sceSaveDataSaveIcon -> 0x%08x", static_cast<unsigned>(rc));
    return rc;
}
int APS5_VABI sceSaveDataDelete(const SaveDataDelete* del) {
    if (del == nullptr) {
        return savedata::SD_ERROR_PARAMETER;
    }
    const int rc = savedata::delete_internal(del->user_id, del->dir_name, del->title_id);
    SAVEDATA_TRACE("sceSaveDataDelete dir=%s -> 0x%08x",
                   del->dir_name != nullptr ? del->dir_name->data : "(null)", static_cast<unsigned>(rc));
    return rc;
}
}
