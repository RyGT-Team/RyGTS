#include <atomic>
#include <cstdint>
#include <cstddef>
#include <cstdio>
#include "SceTypes.hpp"
#include "prx/libc/include/General.hpp"
#ifdef _WIN32
#include <windows.h>
#endif

namespace {

// RyGT: the shadGT renderer (libSceVideoOut.prx) has the on-screen keyboard dialog and exports its
// functions by NID. When it is there, every call goes to it; otherwise the dialog finishes at once
// with a default text, as before.
template <typename Fn>
Fn Renderer(const char* nid) {
#ifdef _WIN32
    if (HMODULE renderer = GetModuleHandleA("libSceVideoOut.prx")) {
        return reinterpret_cast<Fn>(GetProcAddress(renderer, nid));
    }
#endif
    return nullptr;
}

using NoArgFn = int(APS5_VABI*)();
using FormFn = int(APS5_VABI*)(PositionAndForm*);
using SizeFn = int(APS5_VABI*)(const Param*, uint32_t*, uint32_t*);
using SizeExtFn = int(APS5_VABI*)(const Param*, const ExtendedParam*, uint32_t*, uint32_t*);
using ResultFn = int(APS5_VABI*)(Result*);
using InitFn = int(APS5_VABI*)(const Param*, const ExtendedParam*);

constexpr int StatusNone = 0;
constexpr int StatusRunning = 1;
constexpr int StatusFinished = 2;
constexpr std::uint32_t EndStatusOk = 0;
constexpr std::uint32_t EndStatusAborted = 2;
constexpr int ErrorNotOpened = static_cast<int>(0x80bc0002u);

std::atomic<int> status{StatusNone};
std::atomic<int> polls{0};
std::atomic<std::uint32_t> endStatus{EndStatusOk};

}

extern "C" {

int APS5_VABI sceImeDialogAbort(void) {
    if (static const auto f = Renderer<NoArgFn>("oBmw4xrmfKs"); f) return f();
    if (status.load() != StatusRunning) return ErrorNotOpened;
    endStatus = EndStatusAborted;
    status = StatusFinished;
    return 0;
}

int APS5_VABI sceImeDialogGetPanelPositionAndForm(PositionAndForm* form) {
    if (static const auto f = Renderer<FormFn>("8jqzzPioYl8"); f) return f(form);
    if (form == nullptr) return ErrorNotOpened;
    *form = PositionAndForm{};
    form->width = 1920;
    form->height = 1080;
    return 0;
}

int APS5_VABI sceImeDialogGetPanelSize(const Param* param, uint32_t* width, uint32_t* height) {
    if (static const auto f = Renderer<SizeFn>("wqsJvRXwl58"); f) return f(param, width, height);
    (void)param;
    if (width != nullptr) *width = 1920;
    if (height != nullptr) *height = 1080;
    return 0;
}

int APS5_VABI sceImeDialogGetPanelSizeExtended(const Param* param, const ExtendedParam* extended, uint32_t* width, uint32_t* height) {
    if (static const auto f = Renderer<SizeExtFn>("CRD+jSErEJQ"); f) return f(param, extended, width, height);
    (void)extended;
    return sceImeDialogGetPanelSize(param, width, height);
}

int APS5_VABI sceImeDialogGetResult(Result* result) {
    if (static const auto f = Renderer<ResultFn>("x01jxu+vxlc"); f) return f(result);
    std::fprintf(stderr, "[ime] GetResult (status %d)\n", status.load());
    if (result == nullptr || status.load() != StatusFinished) return ErrorNotOpened;
    *result = Result{};
    result->endstatus = endStatus.load();
    return 0;
}

int APS5_VABI sceImeDialogGetStatus(void) {
    if (static const auto f = Renderer<NoArgFn>("IADmD4tScBY"); f) return f();
    if (status.load() == StatusRunning && ++polls > 1) status = StatusFinished;
#ifndef NDEBUG
    static std::atomic<int> traced{0};
    const int count = traced.fetch_add(1);
    if (count < 8 || count % 2000 == 0) std::fprintf(stderr, "[ime] GetStatus #%d -> %d\n", count, status.load());
#endif
    return status.load();
}

int APS5_VABI sceImeDialogInit(const Param* param, const ExtendedParam* extended) {
    if (static const auto f = Renderer<InitFn>("NUeBrN7hzf0"); f) {
        std::fprintf(stderr, "[ime] dialog shown by the renderer (max text length %u)\n", param != nullptr ? param->max_text_length : 0u);
        return f(param, extended);
    }
    (void)extended;
    static std::atomic<int> reported{0};
    bool filled = false;
    if (param != nullptr && param->input_text_buffer != nullptr && param->max_text_length != 0 && param->input_text_buffer[0] == u'\0') {
        static constexpr char16_t defaultText[] = u"Slayer";
        std::uint32_t length = 0;
        while (defaultText[length] != u'\0' && length < param->max_text_length) {
            param->input_text_buffer[length] = defaultText[length];
            ++length;
        }
        param->input_text_buffer[length] = u'\0';
        filled = true;
    }
    std::fprintf(stderr, "[ime] Init #%d\n", reported.load());
    if (reported.fetch_add(1) == 0) std::fprintf(stderr, "[ime] dialog requested (max text length %u): finishing at once with %s\n", param != nullptr ? param->max_text_length : 0u, filled ? "a default text" : "the text unchanged");
    polls = 0;
    endStatus = EndStatusOk;
    status = StatusRunning;
    return 0;
}

int APS5_VABI sceImeDialogTerm(void) {
    if (static const auto f = Renderer<NoArgFn>("gyTyVn+bXMw"); f) return f();
    std::fprintf(stderr, "[ime] Term (status %d)\n", status.load());
    if (status.load() == StatusNone) return ErrorNotOpened;
    status = StatusNone;
    return 0;
}

}
