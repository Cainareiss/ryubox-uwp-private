#include "RyuboxFrontendHost.h"
#include <windows.h>
#include <algorithm>
#include <fstream>
#include <sstream>

namespace
{
std::string Utf8(const std::wstring& value)
{
    if (value.empty()) return {};
    const int bytes = WideCharToMultiByte(CP_UTF8, 0, value.data(),
        static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    std::string result(static_cast<std::size_t>(std::max(0, bytes)), '\0');
    if (bytes > 0)
        WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
            result.data(), bytes, nullptr, nullptr);
    return result;
}
}

RyuboxFrontendHost::RyuboxFrontendHost() {}
RyuboxFrontendHost::~RyuboxFrontendHost() {}

std::vector<UwpImGuiFrontend::LibraryItem> RyuboxFrontendHost::SnapshotCatalogue()
{
    std::scoped_lock lock(m_mutex);
    return m_catalogue;
}

std::filesystem::path RyuboxFrontendHost::CoverPath(std::size_t index)
{
    return std::filesystem::path(L"ryubox-cover-") += std::to_wstring(index);
}

void RyuboxFrontendHost::ReplaceCatalogue(const std::vector<std::wstring>& titles)
{
    std::scoped_lock lock(m_mutex);
    m_catalogue.clear();
    m_textures.clear();
    m_catalogue.reserve(titles.size());
    for (std::size_t index = 0; index < titles.size(); ++index)
    {
        UwpImGuiFrontend::LibraryItem item{};
        item.id = static_cast<UwpImGuiFrontend::ItemId>(index + 1);
        item.name = Utf8(titles[index]);
        item.platform = "Nintendo Switch";
        item.format = "Handheld";
        item.media.push_back({ UwpImGuiFrontend::MediaKind::Cover2D, CoverPath(index) });
        m_catalogue.push_back(std::move(item));
    }
}

void RyuboxFrontendHost::SetCatalogueTexture(std::size_t index,
    UwpImGuiFrontend::TextureHandle texture)
{
    std::scoped_lock lock(m_mutex);
    m_textures[CoverPath(index).native()] = texture;
}

void RyuboxFrontendHost::ClearCatalogueTextures()
{
    std::scoped_lock lock(m_mutex);
    m_textures.clear();
}

UwpImGuiFrontend::RunningContent RyuboxFrontendHost::GetRunningContent() const
{
    return {};
}

UwpImGuiFrontend::DsuEndpoint RyuboxFrontendHost::GetDsuEndpoint() const
{
    return {};
}

bool RyuboxFrontendHost::StartStream(const UwpImGuiFrontend::StreamRequest&)
{
    return false;
}

void RyuboxFrontendHost::StopStream(bool)
{
}

std::optional<UwpImGuiFrontend::ActiveStream> RyuboxFrontendHost::GetActiveStream() const
{
    return std::nullopt;
}

std::optional<std::string> RyuboxFrontendHost::TakeStreamFailure()
{
    return std::nullopt;
}

void RyuboxFrontendHost::Log(UwpImGuiFrontend::LogLevel, std::string_view)
{
}

std::filesystem::path RyuboxFrontendHost::ResourceRoot() const
{
    wchar_t modulePath[MAX_PATH]{};
    const DWORD length = GetModuleFileNameW(nullptr, modulePath, MAX_PATH);
    return length == 0 ? std::filesystem::path{} :
        std::filesystem::path(modulePath).parent_path() / L"resources";
}

std::filesystem::path RyuboxFrontendHost::StateRoot() const
{
    wchar_t localAppData[MAX_PATH]{};
    const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData, MAX_PATH);
    if (length == 0 || length >= MAX_PATH)
        return {};
    std::filesystem::path root(localAppData);
    // Packaged processes already receive the package-local LocalState path.
    // Desktop diagnostics use an isolated Ryubox directory as well.
    root /= L"Ryubox";
    std::error_code error;
    std::filesystem::create_directories(root, error);
    return error ? std::filesystem::path{} : root;
}

UwpImGuiFrontend::TextureHandle RyuboxFrontendHost::LoadTexture(const std::filesystem::path& path)
{
    std::scoped_lock lock(m_mutex);
    const auto found = m_textures.find(path.native());
    if (found != m_textures.end())
        return found->second;
    return {};
}

void RyuboxFrontendHost::ReleaseTexture(UwpImGuiFrontend::TextureHandle)
{
}

bool RyuboxFrontendHost::RequestLaunch(UwpImGuiFrontend::ItemId item)
{
    if (item == 0 || !m_actionCallback)
        return false;
    m_actionCallback(static_cast<int>(item - 1));
    return true;
}

void RyuboxFrontendHost::OpenContextMenu(UwpImGuiFrontend::ItemId)
{
}

bool RyuboxFrontendHost::ToggleFavorite(UwpImGuiFrontend::ItemId item)
{
    std::scoped_lock lock(m_mutex);
    if (item == 0 || item > m_catalogue.size())
        return false;
    auto& selected = m_catalogue[static_cast<std::size_t>(item - 1)];
    selected.favorite = !selected.favorite;
    return true;
}

void RyuboxFrontendHost::Execute(UwpImGuiFrontend::HostCommand command)
{
    if (m_actionCallback)
    {
        switch (command)
        {
        case UwpImGuiFrontend::HostCommand::AddContent:
            m_actionCallback(-1000);
            return;
        case UwpImGuiFrontend::HostCommand::ExitApplication:
            m_actionCallback(-1010);
            return;
        default:
            break;
        }
    }
    if (m_commandCallback)
        m_commandCallback(command);
}

void RyuboxFrontendHost::DrawSettingsPage(std::string_view)
{
}

void RyuboxFrontendHost::OpenUri(std::string_view)
{
}

void RyuboxFrontendHost::PersistFrontendState(std::string_view data)
{
    const auto root = StateRoot();
    if (root.empty()) return;
    const auto target = root / L"frontend-state.json";
    const auto temporary = root / L"frontend-state.tmp";
    {
        std::ofstream stream(temporary, std::ios::binary | std::ios::trunc);
        if (!stream) return;
        stream.write(data.data(), static_cast<std::streamsize>(data.size()));
        stream.flush();
        if (!stream) return;
    }
    std::error_code error;
    std::filesystem::remove(target, error);
    error.clear();
    std::filesystem::rename(temporary, target, error);
    if (error) std::filesystem::remove(temporary, error);
}

std::string RyuboxFrontendHost::LoadFrontendState() const
{
    const auto root = StateRoot();
    if (root.empty()) return {};
    std::ifstream stream(root / L"frontend-state.json", std::ios::binary);
    if (!stream) return {};
    return { std::istreambuf_iterator<char>(stream), std::istreambuf_iterator<char>() };
}
