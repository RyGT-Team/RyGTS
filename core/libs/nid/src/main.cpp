#include <nid/BinaryPatcherFactory.hpp>
#include <nid/ExportExclusions.hpp>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>

namespace {

std::vector<std::uint8_t> _readFile(const std::string& path) {
    std::ifstream f(path, std::ios::binary);
    if (!f) throw std::runtime_error("cannot open: " + path);
    return {std::istreambuf_iterator<char>(f), {}};
}

void _writeFile(const std::string& path, const std::vector<std::uint8_t>& data) {
    std::ofstream f(path, std::ios::binary);
    if (!f) throw std::runtime_error("cannot write: " + path);
    f.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size()));
}

}

int main(int argc, char* argv[]) {
    if (argc < 3) {
        std::cerr << "Usage: nid_patcher <library_name> [--preserve-exports <unpatched-library>] [--preserve-prefix <prefix>] <file> [file2 ...]\n";
        return 1;
    }

    const std::string libraryName = argv[1];
    std::unordered_set<std::string> excludedExports;
    std::vector<std::string> preservedPrefixes;
    std::vector<std::string> paths;
    try {
        bool hasReference = false;
        for (int i = 2; i < argc; ++i) {
            const std::string argument = argv[i];
            if (argument == "--preserve-exports") {
                if (hasReference || i + 1 == argc) throw std::runtime_error("--preserve-exports requires exactly one reference library");
                hasReference = true;
                excludedExports = Nid::ReadExportExclusions(argv[++i]);
            } else if (argument == "--preserve-prefix") {
                if (i + 1 == argc || std::string(argv[i + 1]).empty() || std::string(argv[i + 1]).starts_with("--"))
                    throw std::runtime_error("--preserve-prefix requires a nonempty prefix");
                preservedPrefixes.emplace_back(argv[++i]);
            } else {
                if (argument.starts_with("--")) throw std::runtime_error("unknown option: " + argument);
                paths.push_back(argument);
            }
        }
        if (paths.empty()) throw std::runtime_error("no files to patch");
    } catch (const std::exception& e) {
        std::cerr << "FAIL: " << e.what() << '\n';
        return 2;
    }

    for (const std::string& path : paths) {
        try {
            auto binary = _readFile(path);
            const auto patcher = Nid::MakePatcher(binary);
            auto exclusions = excludedExports;
            if (!preservedPrefixes.empty()) {
                const auto names = Nid::ReadExportExclusions(path);
                for (const auto& name : names)
                    for (const auto& prefix : preservedPrefixes)
                        if (name.starts_with(prefix)) exclusions.insert(name);
            }
            patcher->PatchNids(binary, libraryName, exclusions);
            _writeFile(path, binary);
            std::cout << "OK: " << path << "\n";
        } catch (const std::exception& e) {
            std::cerr << "FAIL: " << path << ": " << e.what() << "\n";
            return 2;
        }
    }

    return 0;
}
