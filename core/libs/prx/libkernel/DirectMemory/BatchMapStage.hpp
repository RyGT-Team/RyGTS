#pragma once
#include <cstdint>
struct BatchMapStageCounter { std::uint64_t attempts{}, completed{}, ticks{}, maximum{}; };
struct BatchMapStages { BatchMapStageCounter stage[4]{}; };
// Internal cross-translation-unit context; excluded from the Windows GNU DLL exports.
extern "C"
#if !defined(_WIN32) && defined(__GNUC__)
__attribute__((visibility("hidden")))
#endif
BatchMapStages* diag412_set_context(BatchMapStages*) noexcept;
