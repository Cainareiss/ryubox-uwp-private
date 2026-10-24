#pragma once

#include "../UWP-ImGuiFrontend/include/UwpImGuiFrontend/Host.h"
#include <vector>
#include <string>
#include <filesystem>
#include <optional>
#include <mutex>
#include <unordered_map>

class RyuboxFrontendHost : public UwpImGuiFrontend::IFrontendHost
{
public:
    RyuboxFrontendHost();
    ~RyuboxFrontendHost() override;

    using HostCommandCallback = void(*)(UwpImGuiFrontend::HostCommand);
    using ActionCallback = void(*)(int);
    void SetCommandCallback(HostCommandCallback cb) { m_commandCallback = cb; }
    void SetActionCallback(ActionCallback cb) { m_actionCallback = cb; }
    void ReplaceCatalogue(const std::vector<std::wstring>& titles);
    void SetCatalogueTexture(std::size_t index, UwpImGuiFrontend::TextureHandle texture);
    void ClearCatalogueTextures();

    // ICompanionHost
    std::vector<UwpImGuiFrontend::LibraryItem> SnapshotCatalogue() override;
    UwpImGuiFrontend::RunningContent GetRunningContent() const override;
    UwpImGuiFrontend::DsuEndpoint GetDsuEndpoint() const override;
    bool StartStream(const UwpImGuiFrontend::StreamRequest& request) override;
    void StopStream(bool secondaryScreen) override;
    std::optional<UwpImGuiFrontend::ActiveStream> GetActiveStream() const override;
    std::optional<std::string> TakeStreamFailure() override;
    void Log(UwpImGuiFrontend::LogLevel level, std::string_view message) override;

    // IFrontendHost
    std::filesystem::path ResourceRoot() const override;
    std::filesystem::path StateRoot() const override;
    UwpImGuiFrontend::TextureHandle LoadTexture(const std::filesystem::path& path) override;
    void ReleaseTexture(UwpImGuiFrontend::TextureHandle texture) override;
    bool RequestLaunch(UwpImGuiFrontend::ItemId item) override;
    void OpenContextMenu(UwpImGuiFrontend::ItemId item) override;
    bool ToggleFavorite(UwpImGuiFrontend::ItemId item) override;
    void Execute(UwpImGuiFrontend::HostCommand command) override;
    void DrawSettingsPage(std::string_view pageId) override;
    void OpenUri(std::string_view uri) override;
    void PersistFrontendState(std::string_view data) override;
    std::string LoadFrontendState() const override;

private:
    static std::filesystem::path CoverPath(std::size_t index);

    mutable std::mutex m_mutex;
    std::vector<UwpImGuiFrontend::LibraryItem> m_catalogue;
    std::unordered_map<std::wstring, UwpImGuiFrontend::TextureHandle> m_textures;
    HostCommandCallback m_commandCallback = nullptr;
    ActionCallback m_actionCallback = nullptr;
};
