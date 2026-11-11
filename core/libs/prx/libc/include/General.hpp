#ifndef CORE_LIBS_PRX_LIBC_INCLUDE_GENERAL_HPP
#define CORE_LIBS_PRX_LIBC_INCLUDE_GENERAL_HPP

#include <stdexcept>
#include <filesystem>
#include <utility>
#include <vector>

#include "general/LogMacros.hpp"
#include "general/VabiMacros.hpp"
#include "general/ExportMacros.hpp"

extern "C" void NotImplemented_nid_no_patch(const char* funcName);
extern "C" void CxaFinalize_nid_no_patch(void* dsoHandle);

extern "C" std::filesystem::path ResolvePath_nid_no_patch(const char* path);
// FEAT-GTS-470: a folder under /app0 is the update's and the base game's folders together. Fills
// `entries` (host path, is a folder) with the merged contents and returns true for such a folder;
// false when `path` is not one (list the folder ResolvePath gives, as before).
extern "C" bool ListLayeredDirectory_nid_no_patch(const char* path, std::vector<std::pair<std::filesystem::path, bool>>* entries);
// Guest path aliases: a guest prefix such as "/savedata0" (a save-data mount point) resolves to a host
// directory instead of the run directory; the prefix matches whole path components only.
extern "C" void AddPathAlias_nid_no_patch(const char* guestPrefix, const char* hostPath);
extern "C" void RemovePathAlias_nid_no_patch(const char* guestPrefix);

#define APS5_INVALID_ARG_EX throw std::invalid_argument(std::string(__func__) + ": invalid argument")

#define APS5_DUMMY_FUN \
int DummyFunction_nid_no_patch() { \
    NotImplemented_nid_no_patch(__func__); \
    return 0; \
}

#endif
