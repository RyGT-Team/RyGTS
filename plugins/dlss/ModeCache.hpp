// SPDX-License-Identifier: MIT

#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <optional>
#include <utility>

namespace RygtDlss {

struct Resolution {
    std::uint32_t inputWidth;
    std::uint32_t inputHeight;
    std::uint32_t outputWidth;
    std::uint32_t outputHeight;

    bool operator==(const Resolution& other) const {
        return inputWidth == other.inputWidth && inputHeight == other.inputHeight &&
               outputWidth == other.outputWidth && outputHeight == other.outputHeight;
    }
};

// Remembers the selected mode for a few resolutions at once. The renderer can scale more than one
// picture per frame (the scene, and the UI-HIRES proxy at the output size), so a single entry made
// every frame query NGX again for each of them.
template <typename TMode>
class ModeCache {
public:
    static constexpr std::size_t Capacity = 8;

    template <typename TQuery>
    std::optional<TMode> Get(const Resolution& resolution, TQuery&& query) {
        for (const auto& entry : entries) {
            if (entry && entry->resolution == resolution) {
                return entry->mode;
            }
        }
        const auto mode = std::forward<TQuery>(query)();
        if (mode) {
            entries[next] = Entry{resolution, *mode};
            next = (next + 1) % Capacity;
        }
        return mode;
    }

    void Reset() {
        for (auto& entry : entries) {
            entry.reset();
        }
        next = 0;
    }

private:
    struct Entry {
        Resolution resolution;
        TMode mode;
    };

    std::array<std::optional<Entry>, Capacity> entries{};
    std::size_t next = 0;
};

} // namespace RygtDlss
