// Keep protocol/cache identities in UTF-8 and extend paths only for Windows I/O.
std::string json_escape(const std::string& value);

static fs::path utf8_path(const std::string& value) {
    return fs::u8path(value);
}

static std::string path_utf8(const fs::path& path) {
    const auto bytes = path.u8string();
    std::string value(bytes.begin(), bytes.end());
#ifdef _WIN32
    std::string prefix = value.substr(0, 8);
    std::transform(prefix.begin(), prefix.end(), prefix.begin(), [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
    if (prefix == "\\\\?\\unc\\") return "\\\\" + value.substr(8);
    if (value.starts_with("\\\\?\\") && value.size() > 6 && value[5] == ':') return value.substr(4);
#endif
    return value;
}

static fs::path native_file_path(const fs::path& path) {
#ifdef _WIN32
    if (path.empty()) return path;
    const auto original = path.wstring();
    if (original.starts_with(L"\\\\?\\") || original.starts_with(L"\\\\.\\")) return path;
    auto absolute = fs::absolute(path).lexically_normal();
    const auto value = absolute.make_preferred().wstring();
    if (value.starts_with(L"\\\\")) return fs::path(L"\\\\?\\UNC\\" + value.substr(2));
    return fs::path(L"\\\\?\\" + value);
#else
    return path;
#endif
}
