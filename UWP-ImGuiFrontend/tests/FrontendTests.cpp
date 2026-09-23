#include "UwpImGuiFrontend/UwpImGuiFrontend.h"

#include <imgui_internal.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cmath>
#include <cstring>
#include <deque>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <source_location>
#include <stdexcept>
#include <thread>
#include <unordered_map>
#include <utility>

namespace
{
using namespace UwpImGuiFrontend;

void Check(bool condition,
	const std::source_location& location = std::source_location::current())
{
	if (!condition)
	{
		std::fprintf(stderr, "frontend test check failed at %s:%u\n",
			location.file_name(), location.line());
		throw std::runtime_error("frontend test check failed");
	}
}

ImGuiIO& CreateTestImGuiContext()
{
	ImGui::CreateContext();
	ImGuiIO& io = ImGui::GetIO();
	io.IniFilename = nullptr;
	io.LogFilename = nullptr;
	return io;
}

class FakeScreenScraper final : public IScreenScraperService
{
public:
	ScreenScraperProgress GetProgress() const override { return {}; }
	bool Start(ScreenScraperRequest) override { return true; }
	bool ConsumeFinished() override { return false; }
	void RequestStop() override { stopped = true; }
	bool HasPendingScan(const std::filesystem::path&) const override { return false; }
	bool ResumePendingScan(ScreenScraperRequest) override { return false; }
	std::optional<std::vector<std::string>> GetAvailableMediaTypes(
		const std::filesystem::path&) const override { return std::nullopt; }
	std::uint64_t BeginSearch(ScreenScraperSearchRequest request) override
	{
		search.generation++;
		search.query = std::move(request.query);
		search.state = ScreenScraperSearchState::Searching;
		return search.generation;
	}
	ScreenScraperSearchSnapshot GetSearchSnapshot() const override { return search; }
	void CancelSearch() override { search.state = ScreenScraperSearchState::Idle; }
	void RequestSearchPreview(std::uint64_t, std::uint64_t,
		ScreenScraperArtwork, std::uint32_t) override {}
	ScreenScraperSearchSnapshot search{};
	bool stopped = false;
};

class FakeCompanion final : public ICompanionService
{
public:
	void Initialize(ICompanionHost&, std::string, std::string) override { initialized = true; }
	void Shutdown() override { initialized = false; }
	void AttachTransport(IExtensionPacketTransport*) override {}
	void DetachTransport(IExtensionPacketTransport*) override {}
	void UpdateRunningContent(RunningContent content) override { running = std::move(content); }
	void UpdateSelection(ItemId item) override { selected = item; }
	bool TryBeginLocalLaunch(ItemId item) override
	{
		if (!acceptLocalLaunchReservation)
			return false;
		if (localLaunchItem)
			return *localLaunchItem == item;
		localLaunchItem = item;
		return true;
	}
	void CancelLocalLaunch(ItemId item) override
	{
		if (localLaunchItem == item)
			localLaunchItem.reset();
	}
	std::optional<ItemId> TakePendingLaunch() override
	{
		return std::exchange(pendingLaunch, std::nullopt);
	}
	std::optional<ItemId> TakePendingSelection() override
	{
		return std::exchange(pendingSelection, std::nullopt);
	}
	void CompletePendingLaunch(bool succeeded) override { launchSucceeded = succeeded; }
	NavigationState GetNavigationState() const override { return navigation; }
	CompanionStatus GetStatus() const override { return {}; }
	bool HandleExtensionPacket(std::span<const std::uint8_t>,
		std::vector<std::vector<std::uint8_t>>&) override { return false; }

	bool initialized = false;
	bool launchSucceeded = false;
	bool acceptLocalLaunchReservation = true;
	std::optional<ItemId> localLaunchItem;
	ItemId selected = 0;
	RunningContent running;
	NavigationState navigation;
	std::optional<ItemId> pendingLaunch;
	std::optional<ItemId> pendingSelection;
};

class FakeHost final : public IFrontendHost
{
public:
	std::vector<LibraryItem> SnapshotCatalogue() override { return catalogue; }
	RunningContent GetRunningContent() const override { return running; }
	DsuEndpoint GetDsuEndpoint() const override { return {}; }
	bool StartStream(const StreamRequest&) override { return false; }
	void StopStream(bool) override {}
	std::optional<ActiveStream> GetActiveStream() const override { return std::nullopt; }
	std::optional<std::string> TakeStreamFailure() override { return std::nullopt; }
	void Log(LogLevel, std::string_view) override {}
	std::filesystem::path ResourceRoot() const override { return {}; }
	std::filesystem::path StateRoot() const override { return {}; }
	TextureHandle LoadTexture(const std::filesystem::path& path) override
	{
		loadedTextures.push_back(path);
		return { nextTextureId++, 320, 180 };
	}
	void ReleaseTexture(TextureHandle) override {}
	TextureHandle AcquireVideoFrame(const std::filesystem::path& path) override
	{
		videoRequests.push_back(path);
		return {};
	}
	void StopVideoPlayback() override { videoStopCount++; }
	bool RequestLaunch(ItemId item) override
	{
		launched = item;
		return launchAccepted;
	}
	void OpenContextMenu(ItemId item) override { contextItem = item; }
	void Execute(HostCommand command) override { commands.push_back(command); }
	void DrawSettingsPage(std::string_view) override {}
	void OpenUri(std::string_view) override {}
	void PersistFrontendState(std::string_view) override {}
	std::string LoadFrontendState() const override { return {}; }

	std::vector<LibraryItem> catalogue;
	RunningContent running;
	std::deque<HostCommand> commands;
	std::vector<std::filesystem::path> loadedTextures;
	std::vector<std::filesystem::path> videoRequests;
	std::uintptr_t nextTextureId = 1;
	std::size_t videoStopCount = 0;
	ItemId launched = 0;
	ItemId contextItem = 0;
	bool launchAccepted = true;
};

constexpr std::uint32_t kTestStreamStart = 0x150000;
constexpr std::uint32_t kTestAuxStreamStart = 0x150100;
constexpr std::uint32_t kTestAuxStreamStop = 0x150101;
constexpr std::uint32_t kTestStreamStatus = 0x150002;
constexpr std::size_t kTestDsuHeaderSize = 20;

std::uint32_t ReadTestU32(const std::uint8_t* data)
{
	return (std::uint32_t)data[0] | ((std::uint32_t)data[1] << 8) |
		((std::uint32_t)data[2] << 16) | ((std::uint32_t)data[3] << 24);
}

void WriteTestU16(std::uint8_t* data, std::uint16_t value)
{
	data[0] = (std::uint8_t)value;
	data[1] = (std::uint8_t)(value >> 8);
}

void WriteTestU32(std::uint8_t* data, std::uint32_t value)
{
	for (int byte = 0; byte < 4; ++byte)
		data[byte] = (std::uint8_t)(value >> (byte * 8));
}

std::uint32_t TestCrc32(const std::uint8_t* data, std::size_t size)
{
	static const std::array<std::uint32_t, 256> table = [] {
		std::array<std::uint32_t, 256> values{};
		for (std::uint32_t index = 0; index < values.size(); ++index)
		{
			std::uint32_t value = index;
			for (int bit = 0; bit < 8; ++bit)
				value = (value & 1) ? (0xEDB88320u ^ (value >> 1)) : (value >> 1);
			values[index] = value;
		}
		return values;
	}();

	std::uint32_t crc = 0xFFFFFFFFu;
	for (std::size_t index = 0; index < size; ++index)
		crc = table[(crc ^ data[index]) & 0xFF] ^ (crc >> 8);
	return crc ^ 0xFFFFFFFFu;
}

std::vector<std::uint8_t> BuildTestDsuRequest(std::uint32_t type,
	std::vector<std::uint8_t> payload)
{
	std::vector<std::uint8_t> packet(kTestDsuHeaderSize + payload.size(), 0);
	std::memcpy(packet.data(), "DSUS", 4);
	WriteTestU16(packet.data() + 4, 1001);
	WriteTestU16(packet.data() + 6, (std::uint16_t)(payload.size() + 4));
	WriteTestU32(packet.data() + 12, 0x12345678);
	WriteTestU32(packet.data() + 16, type);
	std::memcpy(packet.data() + kTestDsuHeaderSize, payload.data(), payload.size());
	WriteTestU32(packet.data() + 8, TestCrc32(packet.data(), packet.size()));
	return packet;
}

std::vector<std::uint8_t> BuildTestAuxStart(std::uint8_t screenId,
	std::string_view url)
{
	std::vector<std::uint8_t> payload(271, 0);
	payload[0] = screenId;
	WriteTestU32(payload.data() + 1, 4);
	const std::size_t urlSize = std::min<std::size_t>(url.size(), 255);
	std::memcpy(payload.data() + 5, url.data(), urlSize);
	WriteTestU32(payload.data() + 261, 3000);
	WriteTestU16(payload.data() + 265, 640);
	WriteTestU16(payload.data() + 267, 480);
	payload[269] = 60;
	payload[270] = 1;
	return BuildTestDsuRequest(kTestAuxStreamStart, std::move(payload));
}

std::vector<std::uint8_t> BuildTestMainStart(std::string_view url)
{
	std::vector<std::uint8_t> payload(270, 0);
	WriteTestU32(payload.data(), 4);
	const std::size_t urlSize = std::min<std::size_t>(url.size(), 255);
	std::memcpy(payload.data() + 4, url.data(), urlSize);
	WriteTestU32(payload.data() + 260, 3000);
	WriteTestU16(payload.data() + 264, 640);
	WriteTestU16(payload.data() + 266, 480);
	payload[268] = 60;
	payload[269] = 1;
	return BuildTestDsuRequest(kTestStreamStart, std::move(payload));
}

class MultiStreamTransport final : public IExtensionPacketTransport
{
public:
	void RequestControllerData(std::uint8_t) override {}

	void SendExtensionPacket(std::vector<std::uint8_t> packet, bool) override
	{
		{
			std::lock_guard lock(m_mutex);
			m_packets.emplace_back(std::move(packet));
		}
		m_condition.notify_all();
	}

	bool WaitForStatus(std::uint32_t state, std::uint8_t screenId,
		std::chrono::milliseconds timeout = std::chrono::seconds(3))
	{
		std::unique_lock lock(m_mutex);
		return m_condition.wait_for(lock, timeout, [&] {
			return std::any_of(m_packets.begin(), m_packets.end(),
				[state, screenId](const auto& packet) {
					return packet.size() >= kTestDsuHeaderSize + 13 &&
						ReadTestU32(packet.data() + 16) == kTestStreamStatus &&
						ReadTestU32(packet.data() + kTestDsuHeaderSize) == state &&
						packet[kTestDsuHeaderSize + 12] == screenId;
				});
		});
	}

private:
	std::mutex m_mutex;
	std::condition_variable m_condition;
	std::vector<std::vector<std::uint8_t>> m_packets;
};

class MultiStreamHost final : public ICompanionHost
{
public:
	std::vector<LibraryItem> SnapshotCatalogue() override { return {}; }
	RunningContent GetRunningContent() const override { return {}; }
	DsuEndpoint GetDsuEndpoint() const override
	{
		DsuEndpoint endpoint;
		endpoint.streamingEnabled = true;
		endpoint.auxiliaryStreamingScreenMask = 0x0F;
		endpoint.independentScreenStreamsSupported = true;
		return endpoint;
	}

	bool StartStream(const StreamRequest&) override
	{
		legacyMethodCalled = true;
		return false;
	}
	void StopStream(bool) override { legacyMethodCalled = true; }
	std::optional<ActiveStream> GetActiveStream() const override
	{
		legacyMethodCalled = true;
		return std::nullopt;
	}
	std::optional<std::string> TakeStreamFailure() override
	{
		legacyMethodCalled = true;
		return std::nullopt;
	}

	bool StartScreenStream(const StreamRequest& request) override
	{
		if (request.screenId > MaxCompanionScreenId)
			return false;
		{
			std::lock_guard lock(m_mutex);
			m_requests.emplace_back(request);
			m_active[request.screenId] = ActiveStream{
				.secondaryScreen = request.secondaryScreen,
				.streamType = request.streamType,
				.url = request.url,
				.width = request.width,
				.height = request.height,
				.framesPerSecond = request.framesPerSecond,
				.audioEnabled = request.audioEnabled,
				.screenId = request.screenId,
			};
		}
		m_condition.notify_all();
		return true;
	}

	void StopScreenStream(std::uint8_t screenId) override
	{
		{
			std::lock_guard lock(m_mutex);
			m_stops.emplace_back(screenId);
			if (screenId < m_active.size())
				m_active[screenId].reset();
		}
		m_condition.notify_all();
	}

	std::optional<ActiveStream> GetActiveScreenStream(
		std::uint8_t screenId) const override
	{
		std::lock_guard lock(m_mutex);
		return screenId < m_active.size() ? m_active[screenId] : std::nullopt;
	}

	std::optional<std::string> TakeScreenStreamFailure(
		std::uint8_t screenId) override
	{
		std::lock_guard lock(m_mutex);
		if (screenId >= m_failures.size())
			return std::nullopt;
		return std::exchange(m_failures[screenId], std::nullopt);
	}

	void Log(LogLevel, std::string_view) override {}

	bool WaitForActive(std::uint8_t screenId,
		std::chrono::milliseconds timeout = std::chrono::seconds(3)) const
	{
		std::unique_lock lock(m_mutex);
		return m_condition.wait_for(lock, timeout, [&] {
			return screenId < m_active.size() && m_active[screenId].has_value();
		});
	}

	bool WaitForStopped(std::uint8_t screenId,
		std::chrono::milliseconds timeout = std::chrono::seconds(3)) const
	{
		std::unique_lock lock(m_mutex);
		return m_condition.wait_for(lock, timeout, [&] {
			return screenId < m_active.size() && !m_active[screenId].has_value() &&
				std::find(m_stops.begin(), m_stops.end(), screenId) != m_stops.end();
		});
	}

	void Fail(std::uint8_t screenId, std::string message)
	{
		std::lock_guard lock(m_mutex);
		m_failures[screenId] = std::move(message);
	}

	std::vector<StreamRequest> Requests() const
	{
		std::lock_guard lock(m_mutex);
		return m_requests;
	}

	mutable std::atomic<bool> legacyMethodCalled{ false };

private:
	mutable std::mutex m_mutex;
	mutable std::condition_variable m_condition;
	std::array<std::optional<ActiveStream>, MaxCompanionScreenId + 1> m_active{};
	std::array<std::optional<std::string>, MaxCompanionScreenId + 1> m_failures{};
	std::vector<StreamRequest> m_requests;
	std::vector<std::uint8_t> m_stops;
};

void TestLayout()
{
	const LayoutContext fullHd = MakeLayoutContext({ 1920.0f, 1080.0f });
	Check(fullHd.scale == 1.5f);
	Check(fullHd.widthUnits == 1280.0f);
	Check(fullHd.heightUnits == 720.0f);

	const LayoutContext ultraWide = MakeLayoutContext({ 2560.0f, 1080.0f });
	Check(ultraWide.scale == 1.5f);
	Check(ultraWide.widthUnits > 1706.0f && ultraWide.widthUnits < 1707.0f);
	const GridLayout grid = MakeGridLayout(ultraWide, 16);
	Check(grid.columns == 5);
	Check(grid.rows == 3);
	Check(grid.pageCount == 2);
	Check(grid.tileSize == 150.0f);
}

void TestShellState()
{
	ShellState state;
	state.SetItemCount(0);
	Check(state.PageCount() == 1);
	state.SetItemCount(16);
	Check(state.PageCount() == 2);
	state.Select(100);
	Check(state.SelectedIndex() == 15);
	state.Move(Direction::Right);
	Check(state.SelectedIndex() == 15);
}

void TestReusableViewState()
{
	SplitViewState split;
	split.SetItemCount(3);
	split.MoveSelection(2, 3);
	Check(split.selected == 2);
	split.MoveSelection(1, 3);
	Check(split.selected == 2);
	split.MoveFocus(1);
	Check(split.focusedPane == SplitPane::Content);
	Check(split.requestedFocus == SplitPane::Content);
	split.SetItemCount(1);
	Check(split.selected == 0);

	ActionListState actions;
	actions.Move(4, 5);
	Check(actions.selected == 4);
	actions.Move(-2, 5);
	Check(actions.selected == 2);
	actions.SetItemCount(0);
	Check(actions.selected == 0 && actions.scroll == 0.0f);

	DataTableState table;
	table.Select(9, 4);
	Check(table.selected == 3);
	table.Move(-2, 4);
	Check(table.selected == 1 && table.requestFocus);
	table.SetItemCount(0);
	Check(table.selected == 0);

	const auto titleActions = MakeDefaultTitleOptionActions(true);
	Check(titleActions.size() == 4);
	Check(titleActions[0].id == "start" && titleActions[0].label == "Start");
	Check(titleActions[1].id == "favorite" && titleActions[1].checkable &&
		titleActions[1].checked);
	Check(titleActions[2].id == "metadata" &&
		titleActions[2].label == "Edit Metadata");
	Check(titleActions[3].id == "controller-mapping" &&
		titleActions[3].label == "Controller Mapping");
}

void TestNotificationCenter()
{
	NotificationCenter notifications(2);
	notifications.Push("First", 60.0f);
	notifications.Push("Second", 60.0f);
	notifications.Push("Third", 60.0f);
	const auto snapshot = notifications.Snapshot();
	Check(snapshot.size() == 2);
	Check(snapshot[0].message == "Third");
	Check(snapshot[1].message == "Second");
	notifications.Clear();
	Check(notifications.Snapshot().empty());
}

void TestStorageLocationMatching()
{
	const auto presets = MakeXboxStorageLocationPresets(
		L"D:\\DevelopmentFiles\\Sample", L"E:\\Sample");
	Check(presets[0].id == "development-files");
	Check(presets[0].label == "Development files");
	Check(presets[1].id == "usb-storage");
	Check(presets[1].label == "USB storage");
	Check(FindStorageLocationPreset(L"d:/developmentfiles/sample/", presets) == 0);
	Check(FindStorageLocationPreset(L"E:\\Sample", presets) == 1);
	Check(!FindStorageLocationPreset(L"F:\\Custom", presets));
	Check(!FindStorageLocationPreset({}, presets));
	Check(MakeStorageLocationFutureAccessToken("game storage") ==
		L"uwp-imgui-folder-game-storage");
	Check(MakeStorageLocationFutureAccessToken({}) ==
		L"uwp-imgui-folder-storage-location");
}

void TestEsDeMediaLayout()
{
	const auto layout = MakeEsDeMediaLayout(
		L"E:\\ES-DE\\downloaded_media", "wiiu",
		L"E:\\Games\\wiiu\\Racing\\Mario Kart 8.wua",
		L"E:\\Games\\wiiu");
	Check(layout.relativeGamePath == L"Racing\\Mario Kart 8.wua");
	Check(ResolveEsDeArtworkPath(layout, ScreenScraperArtwork::Box2D) ==
		L"E:\\ES-DE\\downloaded_media\\wiiu\\covers\\Racing\\Mario Kart 8.png");
	Check(ResolveEsDeArtworkPath(layout, ScreenScraperArtwork::Box3D) ==
		L"E:\\ES-DE\\downloaded_media\\wiiu\\3dboxes\\Racing\\Mario Kart 8.png");
	Check(ResolveEsDeArtworkPath(layout, ScreenScraperArtwork::MixRecalboxV1) ==
		L"E:\\ES-DE\\downloaded_media\\wiiu\\miximages-v1\\Racing\\Mario Kart 8.png");
	Check(ResolveEsDeArtworkPath(layout, ScreenScraperArtwork::MixRecalboxV2) ==
		L"E:\\ES-DE\\downloaded_media\\wiiu\\miximages\\Racing\\Mario Kart 8.png");
	Check(ResolveEsDeArtworkPath(layout, ScreenScraperArtwork::Logo) ==
		L"E:\\ES-DE\\downloaded_media\\wiiu\\marquees\\Racing\\Mario Kart 8.png");
	Check(ResolveEsDeVideoPath(layout) ==
		L"E:\\ES-DE\\downloaded_media\\wiiu\\videos\\Racing\\Mario Kart 8.mp4");
	EsDeMediaLayout invalidLayout = layout;
	invalidLayout.systemName = "../outside";
	Check(ResolveEsDeArtworkPath(invalidLayout,
		ScreenScraperArtwork::Box2D).empty());
	Check(ResolveEsDeArtworkPath(layout, ScreenScraperArtwork::Box2D,
		".png/../../outside").empty());

	const auto nonce = std::chrono::steady_clock::now().time_since_epoch().count();
	const auto temporaryRoot = std::filesystem::temp_directory_path() /
		("uwp_imgui_frontend_esde_" + std::to_string(nonce));
	const auto temporaryLayout = MakeEsDeMediaLayout(temporaryRoot, "wiiu",
		L"E:\\Games\\wiiu\\The Wind Waker HD.wua");
	const auto jpeg = ResolveEsDeArtworkPath(temporaryLayout,
		ScreenScraperArtwork::FanArt, ".jpg");
	std::error_code error;
	std::filesystem::create_directories(jpeg.parent_path(), error);
	{
		std::ofstream output(jpeg, std::ios::binary | std::ios::trunc);
		output << "existing ES-DE media";
	}
	Check(FindExistingEsDeArtworkPath(temporaryLayout,
		ScreenScraperArtwork::FanArt) == jpeg);
	std::filesystem::remove_all(temporaryRoot, error);
}

void TestSettingsPageComposition()
{
	SettingsViewModel model;
	Check(model.communityLinkGroups.size() == 1);
	Check(model.communityLinkGroups[0].label == "Discord");
	Check(model.communityLinkGroups[0].links.size() == 2);
	Check(model.communityLinkGroups[0].links[0].uri ==
		"https://discord.gg/WCmxvvxHqu");
	Check(model.communityLinkGroups[0].links[1].uri ==
		"https://discord.com/invite/f82Tr22vRZ");
	Check(model.communityLinks.size() == 1);
	Check(model.communityLinks[0].label == "APP Store");
	Check(model.communityLinks[0].uri ==
		"https://emulationrevival.github.io/");
	model.companion.servers = {
		{ "disabled", "Disabled", "192.168.0.10", 26760, false },
		{ "primary", "Primary", "192.168.0.20", 26761, true },
		{ "secondary", "Secondary", "192.168.0.30", 26762, true },
	};
	model.companion.streamingEnabled = true;
	model.companion.streamingUrl = "http://192.168.0.20:8080";
	const DsuEndpoint endpoint = MakeCompanionEndpoint(model.companion);
	Check(endpoint.enabled);
	Check(endpoint.host == "192.168.0.20");
	Check(endpoint.port == 26761);
	Check(endpoint.streamingEnabled);
	Check(endpoint.streamingUrl == "http://192.168.0.20:8080");
	const std::array extensions{
		SettingsExtensionPage{ "graphics", "Graphics", true, {} },
		SettingsExtensionPage{ "hidden", "Hidden", false, {} },
	};
	const auto tabs = BuildSettingsTabs(model, extensions);
	Check(tabs.size() == 7);
	Check(tabs[0].id == "interface" && tabs[0].label == "System");
	Check(tabs[1].id == "dsu-streaming" && tabs[1].label == "DSU/Streaming");
	Check(tabs[2].id == "storage");
	Check(tabs[3].id == "screenscraper");
	Check(tabs[4].id == "community");
	Check(tabs[5].id == "graphics" && tabs[5].enabled);
	Check(tabs[6].id == "hidden" && !tabs[6].enabled);

	model.showInterface = false;
	model.showCommunity = false;
	const auto reduced = BuildSettingsTabs(model);
	Check(reduced.size() == 3);
	Check(reduced.front().id == "dsu-streaming");

	SettingsView view;
	view.SelectPage("storage", model);
	Check(view.SelectedPageId() == "storage");
	const std::size_t selected = view.State().selected;
	view.SelectPage("missing", model);
	Check(view.State().selected == selected);
}

void TestGraphicsViewSelection()
{
	GraphicsViewModel model;
	model.pages = {
		{ .id = "general", .label = "General" },
		{ .id = "advanced", .label = "Advanced" },
	};
	GraphicsView view;
	view.SelectPage("advanced", model);
	Check(view.SelectedPageId() == "advanced");
	Check(view.State().selected == 1);
	view.SelectPage("missing", model);
	Check(view.State().selected == 1);
}

void TestLibraryViewSelection()
{
	const std::array items{
		LibraryItem{ .id = 11, .name = "First" },
		LibraryItem{ .id = 22, .name = "Second" },
	};
	LibraryView view;
	Check(LibraryViewModel{}.addGamesFolderLabel == "Add games folder");
	Check(MakeDefaultFrontendConfiguration().emptyCatalogueLabel ==
		"Add games folder");
	Check(!view.IsAddGamesFolderOpen());
	view.OpenAddGamesFolder();
	Check(view.IsAddGamesFolderOpen());
	view.CloseAddGamesFolder();
	Check(!view.IsAddGamesFolderOpen());
	view.Select(9, items.size());
	Check(view.SelectedIndex() == 1);
	Check(view.SelectedItem(items) == 22);
	view.Select(0, 0);
	Check(!view.SelectedItem(std::span<const LibraryItem>{}));
}

void TestScreenScraperArtworkMappings()
{
	struct ExpectedArtwork
	{
		ScreenScraperArtwork artwork;
		std::string_view label;
		std::string_view fileName;
		MediaKind mediaKind;
	};
	const std::array expected{
		ExpectedArtwork{ ScreenScraperArtwork::Box2D, "2D box", "box2d.png", MediaKind::Cover2D },
		ExpectedArtwork{ ScreenScraperArtwork::Box3D, "3D box", "box3d.png", MediaKind::Cover3D },
		ExpectedArtwork{ ScreenScraperArtwork::MixRecalboxV1, "Recalbox Mix V1", "mixrbv1.png", MediaKind::MixRecalboxV1 },
		ExpectedArtwork{ ScreenScraperArtwork::MixRecalboxV2, "Recalbox Mix V2", "mixrbv2.png", MediaKind::MixRecalboxV2 },
		ExpectedArtwork{ ScreenScraperArtwork::FanArt, "Fan art", "fanart.png", MediaKind::FanArt },
		ExpectedArtwork{ ScreenScraperArtwork::Screenshot, "Screenshot", "screenshot.png", MediaKind::Screenshot },
		ExpectedArtwork{ ScreenScraperArtwork::Logo, "Game logo", "logo.png", MediaKind::Logo },
		ExpectedArtwork{ ScreenScraperArtwork::BackCover, "2D box back", "box2d_back.png", MediaKind::BackCover },
		ExpectedArtwork{ ScreenScraperArtwork::PhysicalMedia, "2D cartridge", "support2d.png", MediaKind::PhysicalMedia },
	};
	for (const auto& entry : expected)
	{
		Check(ScreenScraperArtworkLabel(entry.artwork) == entry.label);
		Check(ScreenScraperArtworkFileName(entry.artwork) == entry.fileName);
		Check(ScreenScraperArtworkMediaKind(entry.artwork) == entry.mediaKind);
	}

	ScreenScraperMatch match;
	match.previews.push_back({ ScreenScraperArtwork::Logo, 128, "small.png" });
	match.previews.push_back({ ScreenScraperArtwork::Box2D, 512, "box.png" });
	match.previews.push_back({ ScreenScraperArtwork::Logo, 480, "large.png" });
	const auto* logo = FindPreview(match, ScreenScraperArtwork::Logo);
	Check(logo && logo->path == "large.png");
	Check(!FindPreview(match, ScreenScraperArtwork::Box3D));
}

void TestMetadataEditorWorkflow()
{
	Check(MetadataRatingStars("0.8") == 4);
	Check(MetadataRatingStars("4") == 4);
	Check(MetadataRatingValue(5) == "1.0");
	Check(MetadataRatingValue(0).empty());
	Check(MetadataRatingHalfSteps("0.7") == 7);
	Check(MetadataRatingHalfSteps("3.5") == 7);
	Check(MetadataRatingValueFromHalfSteps(7) == "0.7");
	Check(MetadataRatingValueFromHalfSteps(0).empty());

	ScreenScraperMatch match;
	match.media = {
		{ "box-2D", "jeu", "us", "png" },
		{ "wheel", "jeu", "wor", "png" },
		{ "fanart", "systeme", "wor", "png" },
	};
	Check(ScreenScraperMatchHasArtwork(match, ScreenScraperArtwork::Box2D));
	Check(ScreenScraperMatchHasArtwork(match, ScreenScraperArtwork::Logo));
	Check(!ScreenScraperMatchHasArtwork(match, ScreenScraperArtwork::FanArt));
	Check(CycleScreenScraperMatchArtwork(match,
		ScreenScraperArtwork::Box2D, 1) == ScreenScraperArtwork::Logo);
	Check(CycleScreenScraperMatchArtwork(match,
		ScreenScraperArtwork::Logo, 1) == ScreenScraperArtwork::Box2D);
	Check(CycleScreenScraperMatchArtwork(match,
		ScreenScraperArtwork::Box2D, -1) == ScreenScraperArtwork::Logo);
	match.previews.push_back({ ScreenScraperArtwork::FanArt, 480, "fanart.png" });
	Check(ScreenScraperMatchHasArtwork(match, ScreenScraperArtwork::FanArt));

	FakeScreenScraper scraper;
	MetadataEditor editor(scraper);
	bool saved = false;
	GameMetadataRecord editable;
	editable.itemId = 42;
	editable.title = "Example";
	editable.metadata.contentRating = "ESRB E";
	editable.metadata.modes = "Single-player";
	editable.metadata.themes = "Arcade";
	editor.Open({ .record = editable }, {}, {
		.save = [&](const GameMetadataRecord& record, bool selectedMatch) {
			saved = record.itemId == 42 && record.title == "Example" &&
				!selectedMatch && record.metadata.contentRating == "ESRB E" &&
				record.metadata.modes == "Single-player" &&
				record.metadata.themes == "Arcade";
			return true;
		},
	});
	for (int row = 0; row < 8; ++row)
		(void)editor.HandleInput({ .down = true });
	(void)editor.HandleInput({ .accept = true });
	Check(editor.View() == MetadataEditorView::MatchBrowser);
	Check(editor.HandleInput({ .back = true }) == MetadataEditorEvent::None);
	Check(editor.View() == MetadataEditorView::Editor);
	(void)editor.HandleInput({ .down = true });
	Check(editor.HandleInput({ .accept = true }) == MetadataEditorEvent::Saved);
	Check(saved);
	Check(!editor.IsOpen());

	editor.Open({ .record = { .itemId = 7, .title = "Cancel" } }, {}, {});
	Check(editor.HandleInput({ .back = true }) == MetadataEditorEvent::Cancelled);
	Check(!editor.IsOpen());
}

void TestScreenScraperApiValueNormalization()
{
	Check(NormalizeScreenScraperText("007&#8482; Legends") == "007\xe2\x84\xa2 Legends");
	Check(NormalizeScreenScraperText("Spider-Man &trade;") == "Spider-Man \xe2\x84\xa2");
	Check(NormalizeScreenScraperText("Mario &amp; Luigi &reg;") ==
		"Mario & Luigi \xc2\xae");
	Check(NormalizeScreenScraperText("Keep &unknown; intact") ==
		"Keep &unknown; intact");
	Check(NormalizeDisplayText(std::string("Legacy ") + static_cast<char>(0x99)) ==
		"Legacy \xe2\x84\xa2");
	Check(NormalizeDisplayText("Curly &rsquo; quote") ==
		"Curly \xe2\x80\x99 quote");
	Check(NormalizeMetadataReleaseDate("2013-09-20") == "2013-09-20");
	Check(NormalizeMetadataReleaseDate("20130920T000000") == "2013-09-20");
	Check(NormalizeMetadataReleaseDate("20/09/2013") == "2013-09-20");
	Check(NormalizeMetadataReleaseDate("2013/09/20 00:00:00") == "2013-09-20");
	Check(NormalizeMetadataReleaseDate("not known") == "not known");
	Check(NormalizeMetadataReleaseDate("2013-99-20") == "2013-99-20");
	Check(FormatMetadataReleaseDateUk("2013-09-20") == "20/09/2013");
	Check(FormatMetadataReleaseDateUk("20130920T000000") == "20/09/2013");
	Check(FormatMetadataReleaseDateUk("not known") == "not known");

	Check(NormalizeScreenScraperApiRating("0") == "0");
	Check(NormalizeScreenScraperApiRating("1") == "0.05");
	Check(NormalizeScreenScraperApiRating("10") == "0.5");
	Check(NormalizeScreenScraperApiRating("20") == "1");
	Check(NormalizeScreenScraperApiRating(" 15 ") == "0.75");
	Check(NormalizeScreenScraperApiRating("not-a-rating").empty());

	Check(ResolveScreenScraperRegionCode(
		ScreenScraperRegion::Automatic, "USA") == "us");
	Check(ResolveScreenScraperRegionCode(
		ScreenScraperRegion::Automatic, "Europe") == "eu");
	Check(ResolveScreenScraperRegionCode(
		ScreenScraperRegion::Automatic, "Japan") == "jp");
	Check(ResolveScreenScraperRegionCode(
		ScreenScraperRegion::Automatic, "Mixed") == "wor");
	Check(ResolveScreenScraperRegionCode(
		ScreenScraperRegion::Europe, "USA") == "eu");
}

void TestScreenScraperMediaManifestLoading()
{
	const auto nonce = std::chrono::steady_clock::now().time_since_epoch().count();
	const auto directory = std::filesystem::temp_directory_path() /
		("uwp_imgui_frontend_manifest_" + std::to_string(nonce));
	const auto path = directory / "media.json";
	std::error_code error;
	std::filesystem::create_directories(directory, error);
	{
		std::ofstream output(path, std::ios::binary | std::ios::trunc);
		output << R"({"gameId":9876,"available":["box-2D","wheel-hd"],"saved":["box2d.png"]})";
	}
	const auto manifest = LoadScreenScraperMediaManifest(path);
	Check(manifest.has_value());
	Check(manifest->gameId == 9876);
	Check(manifest->available == std::vector<std::string>{ "box-2D", "wheel-hd" });
	Check(manifest->saved == std::vector<std::string>{ "box2d.png" });
	std::filesystem::remove_all(directory, error);
}

void TestGameMetadataRecordPersistence()
{
	const auto nonce = std::chrono::steady_clock::now().time_since_epoch().count();
	const auto directory = std::filesystem::temp_directory_path() /
		("uwp_imgui_frontend_metadata_" + std::to_string(nonce));
	const auto path = directory / "frontend_metadata.json";

	GameMetadataRecord expected;
	expected.itemId = 0x0005000010143500ULL;
	expected.sourceGameId = 123456;
	expected.title = "The Wind Waker \xE2\x84\xA2";
	expected.metadata.description = "A Unicode description: \xE6\xB5\xB7";
	expected.metadata.rating = "4.5";
	expected.metadata.releaseDate = "2013-09-20";
	expected.metadata.developer = "Nintendo EAD";
	expected.metadata.publisher = "Nintendo";
	expected.metadata.genre = "Action adventure";
	expected.metadata.players = "1";
	expected.metadata.contentRating = "E10+";
	expected.metadata.modes = "Single-player";
	expected.metadata.themes = "Fantasy";
	Check(SaveGameMetadataRecordAtomic(path, expected));

	auto loaded = LoadGameMetadataRecord(path);
	Check(loaded.has_value());
	Check(loaded->itemId == expected.itemId);
	Check(loaded->sourceGameId == expected.sourceGameId);
	Check(loaded->title == expected.title);
	Check(loaded->metadata.description == expected.metadata.description);
	Check(loaded->metadata.rating == expected.metadata.rating);
	Check(loaded->metadata.releaseDate == expected.metadata.releaseDate);
	Check(loaded->metadata.developer == expected.metadata.developer);
	Check(loaded->metadata.publisher == expected.metadata.publisher);
	Check(loaded->metadata.genre == expected.metadata.genre);
	Check(loaded->metadata.players == expected.metadata.players);
	Check(loaded->metadata.contentRating == expected.metadata.contentRating);
	Check(loaded->metadata.modes == expected.metadata.modes);
	Check(loaded->metadata.themes == expected.metadata.themes);

	expected.title = "Edited title";
	Check(SaveGameMetadataRecordAtomic(path, expected));
	loaded = LoadGameMetadataRecord(path);
	Check(loaded && loaded->title == "Edited title");
	Check(!std::filesystem::exists(path.string() + ".tmp"));
	std::error_code error;
	std::filesystem::remove_all(directory, error);
}

void DrawShellTestFrame(Frontend& frontend, ShellRenderer& renderer,
	const ShellPresentation& presentation)
{
	ImGuiIO& io = ImGui::GetIO();
	io.DisplaySize = { 1280.0f, 720.0f };
	io.DeltaTime = 1.0f / 60.0f;
	ImGui::NewFrame();
	renderer.Draw(frontend, presentation, io.DeltaTime);
	ImGui::Render();
}

void TestShellArtworkAndBackgroundOverrides()
{
	ImGuiIO& io = CreateTestImGuiContext();
	unsigned char* pixels = nullptr;
	int width = 0;
	int height = 0;
	io.Fonts->GetTexDataAsRGBA32(&pixels, &width, &height);

	FrontendConfiguration configuration;
	configuration.applicationName = "Test";
	configuration.platformName = "Test platform";
	configuration.placeholderArtwork = "placeholder.png";

	FakeHost host;
	host.catalogue.push_back({
		.id = 1,
		.name = "Game without selected artwork",
		.media = { { MediaKind::Video, "background.mp4" } },
	});
	FakeScreenScraper scraper;
	FakeCompanion companion;
	Frontend frontend(host, scraper, companion, configuration);
	frontend.Initialize();
	ShellRenderer renderer(host);

	ShellTheme dynamicVideoTheme = ShellThemes().front();
	dynamicVideoTheme.mediaMode = ShellThemeMediaMode::DynamicVideo;
	ShellPresentation presentation;
	presentation.customTheme = &dynamicVideoTheme;
	presentation.listArtwork = MediaKind::Cover3D;
	DrawShellTestFrame(frontend, renderer, presentation);
	Check(host.videoRequests.size() == 1);
	Check(std::find(host.loadedTextures.begin(), host.loadedTextures.end(),
		std::filesystem::path("placeholder.png")) == host.loadedTextures.end());

	presentation.overrideThemeDynamicBackground = true;
	DrawShellTestFrame(frontend, renderer, presentation);
	Check(host.videoRequests.size() == 1);
	Check(host.videoStopCount > 0);

	presentation.backgroundArtwork = MediaKind::FanArt;
	presentation.platformMedia = { { MediaKind::FanArt, "platform-fanart.png" } };
	DrawShellTestFrame(frontend, renderer, presentation);
	Check(std::find(host.loadedTextures.begin(), host.loadedTextures.end(),
		std::filesystem::path("platform-fanart.png")) != host.loadedTextures.end());

	host.catalogue.front().media.push_back(
		{ MediaKind::FanArt, "unrelated-current-fanart.png" });
	host.catalogue.push_back({
		.id = 2,
		.name = "Player chooser game",
		.media = { { MediaKind::FanArt, "chooser-fanart.png" } },
	});
	host.catalogue.push_back({
		.id = 3,
		.name = "Player chooser game without fan art",
	});
	frontend.RefreshCatalogue();
	presentation.backgroundItem = 2;
	presentation.allowPlatformBackgroundFallback = false;
	DrawShellTestFrame(frontend, renderer, presentation);
	Check(std::find(host.loadedTextures.begin(), host.loadedTextures.end(),
		std::filesystem::path("chooser-fanart.png")) != host.loadedTextures.end());
	Check(std::find(host.loadedTextures.begin(), host.loadedTextures.end(),
		std::filesystem::path("unrelated-current-fanart.png")) == host.loadedTextures.end());

	presentation.backgroundItem = 3;
	presentation.platformMedia = {
		{ MediaKind::FanArt, "forbidden-platform-fanart.png" },
	};
	DrawShellTestFrame(frontend, renderer, presentation);
	Check(std::find(host.loadedTextures.begin(), host.loadedTextures.end(),
		std::filesystem::path("forbidden-platform-fanart.png")) ==
		host.loadedTextures.end());
	Check(std::find(host.loadedTextures.begin(), host.loadedTextures.end(),
		std::filesystem::path("unrelated-current-fanart.png")) == host.loadedTextures.end());
	frontend.Shutdown();

	FakeHost emptyHost;
	FakeScreenScraper emptyScraper;
	FakeCompanion emptyCompanion;
	Frontend emptyFrontend(emptyHost, emptyScraper, emptyCompanion, configuration);
	emptyFrontend.Initialize();
	ShellRenderer emptyRenderer(emptyHost);
	ShellPresentation emptyPresentation;
	emptyPresentation.overrideThemeDynamicBackground = true;
	emptyPresentation.videoBackground = true;
	emptyPresentation.platformMedia = { { MediaKind::Video, "platform-video.mp4" } };
	DrawShellTestFrame(emptyFrontend, emptyRenderer, emptyPresentation);
	Check(emptyHost.videoRequests.size() == 1);
	Check(emptyHost.videoRequests.front() == std::filesystem::path("platform-video.mp4"));
	Check(std::find(emptyHost.loadedTextures.begin(), emptyHost.loadedTextures.end(),
		std::filesystem::path("placeholder.png")) != emptyHost.loadedTextures.end());
	emptyFrontend.Shutdown();

	ImGui::DestroyContext();
}

void TestFrontendNavigationAndCompanionLaunch()
{
	FakeHost host;
	for (ItemId id = 1; id <= 16; ++id)
		host.catalogue.push_back({ .id = id, .name = "Item" });
	FakeScreenScraper scraper;
	FakeCompanion companion;
	auto configuration = MakeDefaultFrontendConfiguration();
	configuration.catalogueLayout = CatalogueLayoutMode::Grid;
	Frontend frontend(host, scraper, companion, configuration);
	frontend.Initialize();
	Check(companion.initialized);
	Check(companion.selected == 1);
	frontend.PushNotification({ "Ready", 60.0f });
	Check(frontend.Notifications().size() == 1);

	FrameInput input;
	input.right = true;
	frontend.HandleInput(input);
	input.right = false;
	frontend.HandleInput(input);
	Check(frontend.State().SelectedIndex() == 1);

	frontend.Select(5);
	input.left = true;
	frontend.HandleInput(input);
	input.left = false;
	frontend.HandleInput(input);
	Check(frontend.FocusArea() == ShellFocusArea::LeftRail);

	input.accept = true;
	frontend.HandleInput(input);
	Check(!host.commands.empty() && host.commands.back() == HostCommand::OpenHome);
	input.accept = false;
	frontend.HandleInput(input);

	companion.pendingSelection = 16;
	frontend.HandleInput(input);
	Check(frontend.State().SelectedIndex() == 15);
	host.contextItem = 0;
	input.context = true;
	frontend.HandleInput(input);
	Check(host.contextItem == 16);
	input.context = false;
	frontend.HandleInput(input);
	frontend.Select(5);
	input.left = true;
	frontend.HandleInput(input);
	input.left = false;
	frontend.HandleInput(input);
	host.contextItem = 0;
	input.context = true;
	frontend.HandleInput(input);
	Check(host.contextItem == 0);
	input.context = false;
	frontend.HandleInput(input);
	companion.pendingLaunch = 3;
	frontend.HandleInput(input);
	Check(host.launched == 3);
	Check(companion.launchSucceeded);

	frontend.Shutdown();
	Check(scraper.stopped);
	Check(!companion.initialized);
	Check(frontend.Notifications().empty());
}

void TestPremiumCarouselNavigation()
{
	FakeHost host;
	for (ItemId id = 1; id <= 12; ++id)
		host.catalogue.push_back({ .id = id, .name = "Carousel item" });
	FakeScreenScraper scraper;
	FakeCompanion companion;
	Frontend frontend(host, scraper, companion);
	frontend.Initialize();

	Check(frontend.Configuration().catalogueLayout == CatalogueLayoutMode::PremiumCarousel3D);
	Check(frontend.Configuration().premiumCarousel.reflections);
	Check(frontend.State().SelectedIndex() == 0);

	FrameInput input;
	input.right = true;
	frontend.HandleInput(input);
	input.right = false;
	frontend.HandleInput(input);
	Check(frontend.State().SelectedIndex() == 1);

	input.left = true;
	frontend.HandleInput(input);
	input.left = false;
	frontend.HandleInput(input);
	Check(frontend.State().SelectedIndex() == 0);

	// Moving left at the first title transfers focus to the left rail.
	input.left = true;
	frontend.HandleInput(input);
	input.left = false;
	frontend.HandleInput(input);
	Check(frontend.FocusArea() == ShellFocusArea::LeftRail);

	frontend.Select(0);
	input.rightShoulder = true;
	frontend.HandleInput(input);
	input.rightShoulder = false;
	frontend.HandleInput(input);
	Check(frontend.State().SelectedIndex() == 5);

	input.leftShoulder = true;
	frontend.HandleInput(input);
	input.leftShoulder = false;
	frontend.HandleInput(input);
	Check(frontend.State().SelectedIndex() == 0);

	frontend.Select(host.catalogue.size() - 1);
	input.right = true;
	frontend.HandleInput(input);
	input.right = false;
	frontend.HandleInput(input);
	Check(frontend.FocusArea() == ShellFocusArea::RightRail);

	frontend.Shutdown();
}

void TestPremiumFrontendInterfaceSettingsBinding()
{
	FakeHost host;
	FakeScreenScraper scraper;
	FakeCompanion companion;
	Frontend frontend(host, scraper, companion);
	frontend.Initialize();

	RyuboxSettingsModel model = MakeDefaultRyuboxSettingsModel();
	SyncRyuboxFrontendInterfaceSettings(frontend, model.interface);
	Check(model.interface.catalogueLayout.selected == 0);
	Check(model.interface.carouselReflections.value);
	Check(model.interface.carouselGlow.value);

	model.interface.catalogueLayout.selected = 1;
	Check(ApplyRyuboxFrontendInterfaceSetting(frontend,
		"interface.catalogueLayout", model.interface));
	Check(frontend.Configuration().catalogueLayout == CatalogueLayoutMode::Grid);

	model.interface.carouselReflections.value = false;
	Check(ApplyRyuboxFrontendInterfaceSetting(frontend,
		"interface.carouselReflections", model.interface));
	Check(!frontend.Configuration().premiumCarousel.reflections);

	model.interface.carouselGlow.value = false;
	Check(ApplyRyuboxFrontendInterfaceSetting(frontend,
		"interface.carouselGlow", model.interface));
	Check(!frontend.Configuration().premiumCarousel.selectedGlow);

	model.interface.carouselDepth.value = 1.25f;
	Check(ApplyRyuboxFrontendInterfaceSetting(frontend,
		"interface.carouselDepth", model.interface));
	Check(std::abs(frontend.Configuration().premiumCarousel.depthStrength - 1.25f) < 0.001f);

	model.interface.launchAnimation.value = false;
	Check(ApplyRyuboxFrontendInterfaceSetting(frontend,
		"interface.launchAnimation", model.interface));
	Check(!frontend.AnimateLaunches());

	Check(!ApplyRyuboxFrontendInterfaceSetting(frontend,
		"interface.unknown", model.interface));
	frontend.Shutdown();
}

void TestAnimatedLaunchCommit()
{
	FakeHost host;
	host.catalogue.push_back({ .id = 42, .name = "Launch item" });
	FakeScreenScraper scraper;
	FakeCompanion companion;
	Frontend frontend(host, scraper, companion);
	frontend.Initialize();
	frontend.ActivateSelection();
	Check(companion.localLaunchItem == 42);
	Check(frontend.PendingLaunchItem() == 42);
	Check(host.launched == 0);
	Check(frontend.CommitPendingLaunch());
	Check(host.launched == 42);
	Check(!frontend.PendingLaunchItem());
	frontend.Shutdown();
}

void TestRuntimeLaunchAnimationSetting()
{
	FakeHost host;
	host.catalogue.push_back({ .id = 43, .name = "Immediate launch item" });
	FakeScreenScraper scraper;
	FakeCompanion companion;
	Frontend frontend(host, scraper, companion);
	frontend.Initialize();
	Check(frontend.AnimateLaunches());
	frontend.SetAnimateLaunches(false);
	Check(!frontend.AnimateLaunches());
	frontend.ActivateSelection();
	Check(!frontend.PendingLaunchItem());
	Check(host.launched == 43);
	frontend.Shutdown();
}

void TestLocalLaunchReservationAndFailure()
{
	FakeHost host;
	host.catalogue.push_back({ .id = 7, .name = "Local item" });
	FakeScreenScraper scraper;
	FakeCompanion companion;
	Frontend frontend(host, scraper, companion);
	frontend.Initialize();

	companion.acceptLocalLaunchReservation = false;
	frontend.ActivateSelection();
	Check(!frontend.PendingLaunchItem());
	Check(host.launched == 0);
	Check(!companion.localLaunchItem);

	companion.acceptLocalLaunchReservation = true;
	host.launchAccepted = false;
	frontend.ActivateSelection();
	Check(companion.localLaunchItem == 7);
	Check(!frontend.CommitPendingLaunch());
	Check(!companion.localLaunchItem);
	frontend.Shutdown();
}

void TestCompanionLocalLaunchLifecycle()
{
	FakeHost host;
	auto companion = CreateCompanionService();
	companion->Initialize(host, "Test application", "Test platform");
	Check(companion->TryBeginLocalLaunch(42));
	Check(companion->TryBeginLocalLaunch(42));
	Check(!companion->TryBeginLocalLaunch(7));

	companion->UpdateRunningContent({ 42, "Running item", true });
	Check(!companion->TryBeginLocalLaunch(7));
	companion->UpdateRunningContent({});
	Check(companion->TryBeginLocalLaunch(7));
	companion->CancelLocalLaunch(7);
	Check(companion->TryBeginLocalLaunch(42));
	companion->CancelLocalLaunch(42);
	companion->Shutdown();
}

void TestCompanionIndependentScreenStreams()
{
	MultiStreamHost host;
	MultiStreamTransport transport;
	auto companion = CreateCompanionService();
	companion->Initialize(host, "Multi-screen test", "Test platform");
	companion->AttachTransport(&transport);
	companion->UpdateRunningContent({ 1, "Running item", true });

	const auto dispatch = [&](std::vector<std::uint8_t> packet) {
		std::vector<std::vector<std::uint8_t>> responses;
		Check(companion->HandleExtensionPacket(packet, responses));
	};
	dispatch(BuildTestMainStart("udp://127.0.0.1:5000"));
	dispatch(BuildTestAuxStart(2, "udp://127.0.0.1:5002"));
	dispatch(BuildTestAuxStart(4, "udp://127.0.0.1:5004"));
	Check(host.WaitForActive(0));
	Check(host.WaitForActive(2));
	Check(host.WaitForActive(4));
	Check(transport.WaitForStatus(1, 0));
	Check(transport.WaitForStatus(1, 2));
	Check(transport.WaitForStatus(1, 4));

	const auto initialRequests = host.Requests();
	Check(initialRequests.size() == 3);
	Check(initialRequests[0].screenId == 0 && !initialRequests[0].secondaryScreen);
	Check(initialRequests[1].screenId == 2 && initialRequests[1].secondaryScreen);
	Check(initialRequests[2].screenId == 4 && initialRequests[2].secondaryScreen);

	dispatch(BuildTestDsuRequest(kTestAuxStreamStop, { 2 }));
	Check(host.WaitForStopped(2));
	Check(transport.WaitForStatus(0, 2));
	Check(host.GetActiveScreenStream(0).has_value());
	Check(host.GetActiveScreenStream(4).has_value());

	dispatch(BuildTestAuxStart(1, "udp://127.0.0.1:5001"));
	Check(host.WaitForActive(1));
	host.Fail(4, "screen 4 encoder failed");
	Check(transport.WaitForStatus(2, 4));
	Check(host.WaitForStopped(4));
	Check(host.GetActiveScreenStream(0).has_value());
	Check(host.GetActiveScreenStream(1).has_value());

	const std::size_t validRequestCount = host.Requests().size();
	dispatch(BuildTestAuxStart(5, "udp://127.0.0.1:5005"));
	Check(transport.WaitForStatus(2, 5));
	Check(host.Requests().size() == validRequestCount);
	Check(!host.legacyMethodCalled.load());

	companion->DetachTransport(&transport);
	companion->Shutdown();
}

template <typename Draw>
void RunImGuiFrame(const WidgetInputState& input,
	const WidgetInputState& previousInput, Draw&& draw)
{
	ImGuiIO& io = ImGui::GetIO();
	io.DisplaySize = { 800.0f, 600.0f };
	io.DeltaTime = 1.0f / 60.0f;
	ImGui::NewFrame();
	BeginWidgetFrame(ShellThemes().front(), input, previousInput, true);
	ImGui::SetNextWindowPos({ 0.0f, 0.0f });
	ImGui::SetNextWindowSize(io.DisplaySize);
	ImGui::Begin("Widget navigation tests", nullptr,
		ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoSavedSettings);
	draw();
	ImGui::End();
	EndWidgetFrame();
	ImGui::Render();
}

void TestWidgetGamepadNavigation()
{
	ImGuiIO& io = CreateTestImGuiContext();
	io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard |
		ImGuiConfigFlags_NavEnableGamepad;
	unsigned char* pixels = nullptr;
	int width = 0;
	int height = 0;
	io.Fonts->GetTexDataAsRGBA32(&pixels, &width, &height);

	SubmitImGuiNavigationInput({ .context = true }, true);
	bool contextReachedImGuiWindowing = false;
	RunImGuiFrame({}, {}, [&] {
		contextReachedImGuiWindowing =
			ImGui::IsKeyDown(ImGuiKey_GamepadFaceLeft);
	});
	Check(!contextReachedImGuiWindowing);
	SubmitImGuiNavigationInput({}, true);

	const std::array choices{
		ChoiceItem{ "one", "One" },
		ChoiceItem{ "two", "Two" },
	};
	std::size_t selected = 0;
	ImGuiID firstComboId = 0;
	RunImGuiFrame({}, {}, [&] {
		(void)ShellCombo("Mode", &selected, choices);
		firstComboId = GImGui->LastItemData.ID;
	});
	selected = 1;
	ImGuiID secondComboId = 0;
	RunImGuiFrame({}, {}, [&] {
		(void)ShellCombo("Mode", &selected, choices);
		secondComboId = GImGui->LastItemData.ID;
	});
	Check(firstComboId != 0 && firstComboId == secondComboId);

	RunImGuiFrame({}, {}, [&] {
		ImGui::SetKeyboardFocusHere();
		(void)ShellButton("Controller action");
	});
	bool buttonPressed = false;
	RunImGuiFrame({ .accept = true }, {}, [&] {
		buttonPressed = ShellButton("Controller action");
	});
	Check(buttonPressed);

#if IMGUI_VERSION_NUM >= 19140
	// The frontend owns focus rendering for navigable invisible buttons.
	constexpr ImU32 unexpectedNavCursor = IM_COL32(251, 3, 197, 255);
	ImVec4 expectedCustomFocus = ShellThemes().front().cursorNormal;
	expectedCustomFocus.w = 0.90f;
	const ImU32 expectedCustomFocusColor =
		ImGui::ColorConvertFloat4ToU32(expectedCustomFocus);
	RunImGuiFrame({}, {}, [&] {
		ImGui::SetKeyboardFocusHere();
		(void)ShellButton("Single custom focus");
	});
	ImDrawList* focusedButtonDrawList = nullptr;
	int focusedButtonFirstVertex = 0;
	int focusedButtonLastVertex = 0;
	bool focusedButtonReceivedFocus = false;
	RunImGuiFrame({}, {}, [&] {
		ImGui::SetNavCursorVisible(true);
		ImGui::GetStyle().Colors[ImGuiCol_NavCursor] =
			ImGui::ColorConvertU32ToFloat4(unexpectedNavCursor);
		focusedButtonDrawList = ImGui::GetWindowDrawList();
		focusedButtonFirstVertex = focusedButtonDrawList->VtxBuffer.Size;
		(void)ShellButton("Single custom focus");
		focusedButtonLastVertex = focusedButtonDrawList->VtxBuffer.Size;
		focusedButtonReceivedFocus = ImGui::IsItemFocused();
	});
	bool foundBuiltInNavCursor = false;
	bool foundCustomFocus = false;
	Check(focusedButtonReceivedFocus);
	Check(focusedButtonDrawList != nullptr);
	for (int vertexIndex = focusedButtonFirstVertex;
		vertexIndex < focusedButtonLastVertex; ++vertexIndex)
	{
		const ImU32 color = focusedButtonDrawList->VtxBuffer[vertexIndex].col;
		foundBuiltInNavCursor |= color == unexpectedNavCursor;
		foundCustomFocus |= color == expectedCustomFocusColor;
	}
	Check(!foundBuiltInNavCursor);
	Check(foundCustomFocus);
#endif

	int narrowValue = 1;
	RunImGuiFrame({}, {}, [&] {
		ImGui::SetKeyboardFocusHere();
		(void)ShellSliderInt("Narrow integer", &narrowValue, 1, 8);
	});
	RunImGuiFrame({ .right = true }, {}, [&] {
		(void)ShellSliderInt("Narrow integer", &narrowValue, 1, 8);
	});
	Check(narrowValue == 2);

	bool popupBegan = false;
	RunImGuiFrame({}, {}, [&] {
		ImGui::OpenPopup("controller-popup");
		popupBegan = BeginShellPopup("controller-popup");
		if (popupBegan)
		{
			(void)ShellButton("Popup action");
			EndShellPopup();
		}
	});
	Check(popupBegan);
	bool backConsumed = false;
	RunImGuiFrame({ .back = true }, {}, [&] {
		if (BeginShellPopup("controller-popup"))
		{
			(void)ShellButton("Popup action");
			EndShellPopup();
		}
		backConsumed = IsWidgetBackNavigationConsumed();
	});
	Check(backConsumed);

	ActionListState actionState{ .selected = 1, .requestFocus = true };
	const std::array actions{
		ActionListEntry{ .id = "first", .label = "First" },
		ActionListEntry{ .id = "second", .label = "Second" },
	};
	const ShellLayoutContext layout = MakeShellLayout({ 800.0f, 600.0f });
	const UnitRect panel{ 20.0f, 20.0f, 500.0f, 320.0f };
	RunImGuiFrame({}, {}, [&] {
		(void)DrawActionList(layout, ImGui::GetWindowDrawList(), panel,
			20.0f, actionState, actions);
	});
	std::optional<std::size_t> activatedAction;
#if IMGUI_VERSION_NUM >= 19140
	ImDrawList* actionListDrawList = nullptr;
	int actionListFirstVertex = 0;
	int actionListLastVertex = 0;
	bool actionListReceivedFocus = false;
#endif
	RunImGuiFrame({ .accept = true }, {}, [&] {
#if IMGUI_VERSION_NUM >= 19140
		ImGui::SetNavCursorVisible(true);
		ImGui::GetStyle().Colors[ImGuiCol_NavCursor] =
			ImGui::ColorConvertU32ToFloat4(unexpectedNavCursor);
		actionListDrawList = ImGui::GetWindowDrawList();
		actionListFirstVertex = actionListDrawList->VtxBuffer.Size;
#endif
		activatedAction = DrawActionList(layout, ImGui::GetWindowDrawList(), panel,
			20.0f, actionState, actions);
#if IMGUI_VERSION_NUM >= 19140
		actionListLastVertex = actionListDrawList->VtxBuffer.Size;
		actionListReceivedFocus = ImGui::IsItemFocused();
#endif
	});
	Check(activatedAction == 1);
#if IMGUI_VERSION_NUM >= 19140
	bool actionListBuiltInCursor = false;
	bool actionListCustomFocus = false;
	Check(actionListReceivedFocus);
	Check(actionListDrawList != nullptr);
	for (int vertexIndex = actionListFirstVertex;
		vertexIndex < actionListLastVertex; ++vertexIndex)
	{
		const ImU32 color = actionListDrawList->VtxBuffer[vertexIndex].col;
		actionListBuiltInCursor |= color == unexpectedNavCursor;
		actionListCustomFocus |= color == expectedCustomFocusColor;
	}
	Check(!actionListBuiltInCursor);
	Check(actionListCustomFocus);
#endif

	TitleOptionsState titleOptionsState;
	TitleOptionsModel titleOptionsModel;
	titleOptionsModel.title = "Title options";
	titleOptionsModel.actions = MakeDefaultTitleOptionActions(false);
	TitleOptionsResult titleOptionsResult;
	bool titleBackConsumed = false;
	RunImGuiFrame({ .back = true }, {}, [&] {
		titleOptionsResult = DrawTitleOptionsView(layout,
			ImGui::GetWindowDrawList(), { 10.0f, 10.0f, 780.0f, 570.0f },
			titleOptionsState, titleOptionsModel);
		titleBackConsumed = IsWidgetBackNavigationConsumed();
	});
	Check(titleOptionsResult.closeRequested);
	Check(titleBackConsumed);

	// Pending table focus must ignore the previously focused row.
	io.BackendFlags |= ImGuiBackendFlags_HasGamepad;
	std::vector<LibraryItem> libraryItems;
	for (std::uint64_t index = 0; index < 12; ++index)
		libraryItems.push_back({ .id = 101 + index,
			.name = "Game " + std::to_string(index + 1) });
	LibraryView libraryView;
	libraryView.Select(0, libraryItems.size());
	WidgetInputState previousLibraryInput{};
	const auto drawLibraryFrame = [&](const WidgetInputState& input) {
		io.AddKeyEvent(ImGuiKey_GamepadDpadUp, input.up);
		io.AddKeyEvent(ImGuiKey_GamepadDpadDown, input.down);
		io.AddKeyEvent(ImGuiKey_GamepadDpadLeft, input.left);
		io.AddKeyEvent(ImGuiKey_GamepadDpadRight, input.right);
		RunImGuiFrame(input, previousLibraryInput, [&] {
			(void)libraryView.Draw({ .items = libraryItems }, {}, { 760.0f, 250.0f });
		});
		previousLibraryInput = input;
	};
	const auto releaseLibraryInput = [&] { drawLibraryFrame({}); };
	drawLibraryFrame({});
	drawLibraryFrame({});
	for (std::size_t expected = 1; expected < libraryItems.size(); ++expected)
	{
		drawLibraryFrame({ .down = true });
		Check(libraryView.SelectedIndex() == expected);
		releaseLibraryInput();
	}
	for (std::size_t expected = libraryItems.size() - 1; expected > 0; --expected)
	{
		drawLibraryFrame({ .up = true });
		Check(libraryView.SelectedIndex() == expected - 1);
		releaseLibraryInput();
	}
	// Verify first-row Up returns to the toolbar and Down re-enters without skipping.
	drawLibraryFrame({ .up = true });
	releaseLibraryInput();
	drawLibraryFrame({ .right = true });
	releaseLibraryInput();
	drawLibraryFrame({ .down = true });
	Check(libraryView.SelectedIndex() == 0);
	releaseLibraryInput();
	drawLibraryFrame({ .down = true });
	Check(libraryView.SelectedIndex() == 1);
	releaseLibraryInput();

	const auto checkScrollablePaneClipping = [](const char* paneId,
		auto&& drawControl) {
		bool inspectedDrawCommand = false;
		RunImGuiFrame({}, {}, [&] {
			(void)BeginShellPane(paneId, { 320.0f, 84.0f },
				ImGuiWindowFlags_NoSavedSettings, 12.0f, 0.82f,
				{ 8.0f, 8.0f });
			const ImRect paneClip = ImGui::GetCurrentWindow()->ClipRect;
			ImGui::SetCursorPosY(ImGui::GetCursorPosY() + 60.0f);
			ImDrawList* draw = ImGui::GetWindowDrawList();
			const int firstCommand = std::max(0, draw->CmdBuffer.Size - 1);
			drawControl();
			for (int index = firstCommand; index < draw->CmdBuffer.Size; ++index)
			{
				const ImDrawCmd& command = draw->CmdBuffer[index];
				if (command.ElemCount == 0)
					continue;
				inspectedDrawCommand = true;
				Check(command.ClipRect.x >= paneClip.Min.x - 0.01f);
				Check(command.ClipRect.y >= paneClip.Min.y - 0.01f);
				Check(command.ClipRect.z <= paneClip.Max.x + 0.01f);
				Check(command.ClipRect.w <= paneClip.Max.y + 0.01f);
			}
			EndShellPane();
		});
		Check(inspectedDrawCommand);
	};

	bool clippedCheck = false;
	int clippedSlider = 50;
	int clippedStepper = 4;
	checkScrollablePaneClipping("button clip pane", [] {
		(void)ShellButton("Clipped button");
	});
	checkScrollablePaneClipping("selectable clip pane", [] {
		(void)ShellSelectable("Clipped selectable", false);
	});
	checkScrollablePaneClipping("checkbox clip pane", [&] {
		(void)ShellCheckbox("Clipped checkbox", &clippedCheck);
	});
	checkScrollablePaneClipping("slider clip pane", [&] {
		(void)ShellSliderInt("Clipped slider", &clippedSlider, 0, 100);
	});
	checkScrollablePaneClipping("stepper clip pane", [&] {
		(void)ShellIntegerStepper("Clipped stepper", &clippedStepper, 1, 8);
	});

	ImGui::DestroyContext();
}

void TestSetupWizardAndModalOwnership()
{
	SetupWizard wizard;
	Check(wizard.State().step == SetupStep::Welcome);
	Check(wizard.Advance());
	Check(wizard.State().step == SetupStep::ProdKeys);
	Check(wizard.Advance());
	Check(wizard.State().operation == SetupOperationState::WaitingForPicker);
	const auto keysRequest = wizard.TakePickerRequest();
	Check(keysRequest.has_value());
	Check(keysRequest->kind == SetupPickerKind::ProdKeys);

	// Picker cancellation is a no-op from the user's perspective: it must not
	// create progress/error UI or advance the wizard.
	wizard.CompletePicker(nullptr);
	Check(wizard.State().step == SetupStep::ProdKeys);
	Check(wizard.State().operation == SetupOperationState::Idle);
	Check(!wizard.Popups().IsOpen());

	Check(wizard.Advance());
	Check(wizard.TakePickerRequest().has_value());
	auto chosen = std::make_unique<PickedItem>();
	chosen->folder = true;
	chosen->path = "D:/keys";
	wizard.CompletePicker(std::move(chosen));
	Check(wizard.State().operation == SetupOperationState::Processing);
	Check(wizard.Popups().IsOpen());
	Check(wizard.Popups().Type() == PopupType::Progress);
	auto selectedItem = wizard.TakeSelectedItem();
	Check(selectedItem && selectedItem->path == std::filesystem::path("D:/keys"));
	// A second action while a picker/operation owns focus is rejected.
	Check(!wizard.Advance());
	wizard.ReportProgress(0.75f, "Importing keys...");
	Check(wizard.State().progress == 0.75f);
	wizard.CompleteOperation(true, "Keys installed");
	Check(!wizard.Popups().IsOpen());
	Check(wizard.Popups().RequestFocusRestore());
	Check(wizard.ConsumeFocusRestore());
	wizard.Tick(1.1f);
	Check(wizard.State().step == SetupStep::Firmware);
	Check(wizard.State().operation == SetupOperationState::Idle);

	PopupManager popups;
	Check(popups.Open(PopupType::Progress, "progress", "origin"));
	Check(!popups.Open(PopupType::Error, "error", "other"));
	Check(popups.Open(PopupType::Progress, "progress", "origin"));
	popups.Close();
	Check(popups.RequestFocusRestore());
}

void TestUiInputDebounce()
{
	UiInputTracker input;
	Check(input.Update({ .accept = true }).confirmPressed);
	Check(!input.Update({ .accept = true }).confirmPressed);
	input.BlockFor(0.2f);
	Check(!input.Update({ .accept = true, .deltaSeconds = 0.1f }).confirmPressed);
	Check(!input.Update({ .accept = false, .deltaSeconds = 0.1f }).confirmPressed);
	Check(input.Update({ .accept = true }).confirmPressed);
	Check(input.Update({ .right = true }).rightPressed);
	for (int index = 0; index < 2; ++index)
		Check(!input.Update({ .right = true, .deltaSeconds = 0.1f }).rightPressed);
	Check(input.Update({ .right = true, .deltaSeconds = 0.11f }).rightPressed);
}

void TestRyuboxSettingsCategories()
{
	RyuboxSettingsModel model = MakeDefaultRyuboxSettingsModel();
	const auto pages = BuildRyuboxSettingsExtensions(model, {});
	const std::array<std::string_view, 9> expected{
		"Geral", "Graficos", "Sistema", "Audio", "Controles", "Interface",
		"Armazenamento", "Avancado", "Sobre",
	};
	Check(pages.size() == expected.size());
	for (std::size_t index = 0; index < expected.size(); ++index)
		Check(pages[index].label == expected[index]);
}

class TestInputValues final : public IInputValueSource
{
public:
	float ReadInput(std::string_view reference) const override
	{
		const auto it = values.find(std::string(reference));
		return it == values.end() ? 0.0f : it->second;
	}

	std::unordered_map<std::string, float> values;
};

class TestInputMappingHost final : public IInputMappingHost
{
public:
	std::vector<InputDeviceDescriptor> EnumerateInputDevices() override
	{
		return { { .id = "wgi:0", .label = "Xbox Controller", .api = "WGI",
			.connected = true, .supportsRumble = true, .supportsCalibration = true,
			.controls = { { "A", "A", InputControlKind::Button } } } };
	}

	std::vector<DsuServerModel> EnumerateDsuServers() override
	{
		return { { .id = "phone", .name = "Phone", .host = "192.168.1.10",
			.port = 26760, .enabled = true } };
	}

	std::vector<EmulatedControllerDescriptor> EnumerateEmulatedControllerTypes(
		std::uint8_t) override
	{
		return { { "gamepad", "GamePad" }, { "remote", "Remote" } };
	}

	TextureHandle GetControllerPreviewTexture(std::string_view) override
	{
		previewRequests++;
		return { 0xCAFEu, 400, 200 };
	}

	std::vector<EmulatedControlDescriptor> EnumerateEmulatedControls(
		std::uint8_t, std::string_view) override
	{
		return {
			{ "1", "A", "Buttons", InputControlKind::Button },
			{ "65536", "Accelerometer X", "Motion", InputControlKind::Accelerometer },
			{ "65544", "Pointer press", "Pointer", InputControlKind::Touch },
		};
	}

	std::vector<InputSettingDraft> CreateControllerSettings(
		std::uint8_t, std::string_view) override
	{
		return { { .id = "toggle", .label = "Toggle", .group = "Options",
			.kind = InputSettingKind::Toggle } };
	}

	InputDeviceSettingsDraft CreateInputDeviceSettings(std::string_view deviceId) override
	{
		return { std::string(deviceId), {
			{ .id = "deadzone", .label = "Deadzone", .group = "Stick",
				.kind = InputSettingKind::Number, .numberValue = 0.2f,
				.minimum = 0.0f, .maximum = 1.0f, .step = 0.05f } } };
	}

	std::vector<std::string> EnumerateProfileNames(std::uint8_t) override
	{
		return profiles;
	}

	ControllerProfileDraft LoadProfileDraft(std::uint8_t player,
		std::optional<std::uint64_t> titleId) override
	{
		auto draft = MakeDraft(player, "base", titleId);
		if (loadWithDefaultDevice)
		{
			draft.defaultDeviceId = "wgi:0";
			draft.attachedDeviceIds = { "wgi:0" };
		}
		return draft;
	}

	ControllerProfileDraft LoadNamedProfileDraft(std::uint8_t player,
		std::string_view profileName, std::optional<std::uint64_t> titleId) override
	{
		loadedProfile = std::string(profileName);
		return MakeDraft(player, profileName, titleId);
	}

	ControllerProfileDraft CreateDefaultProfile(std::uint8_t player,
		std::optional<std::uint64_t> titleId, std::string_view controllerTypeId,
		std::string_view preferredDeviceId) override
	{
		defaultProfileLoads++;
		auto draft = MakeDraft(player, "base", titleId);
		if (!controllerTypeId.empty())
			draft.emulatedControllerTypeId = std::string(controllerTypeId);
		draft.defaultDeviceId = preferredDeviceId.empty() ? "wgi:0" :
			std::string(preferredDeviceId);
		draft.attachedDeviceIds = { draft.defaultDeviceId };
		draft.mappings.front().expression =
			draft.emulatedControllerTypeId == "remote" ? "`wgi:0/B`" : "`wgi:0/A`";
		draft.controllerSettings.front().boolValue = true;
		draft.deviceSettings = { CreateInputDeviceSettings(draft.defaultDeviceId) };
		draft.deviceSettings.front().settings.front().numberValue = 0.35f;
		return draft;
	}

	MappingSaveResult SaveProfileDraft(const ControllerProfileDraft& draft) override
	{
		savedDraft = draft;
		savedDrafts.push_back(draft);
		return { true, {} };
	}

	MappingSaveResult SaveProfileDrafts(
		std::span<const ControllerProfileDraft> drafts) override
	{
		batchSaveCount++;
		for (const ControllerProfileDraft& draft : drafts)
		{
			const MappingSaveResult result = SaveProfileDraft(draft);
			if (!result.succeeded)
				return result;
		}
		return { true, {} };
	}

	std::vector<InputCaptureSample> ReadLiveInputs() override { return {}; }
	void TestRumble(std::string_view, float, std::chrono::milliseconds) override {}
	void ResetMappingRuntime() override { resetCount++; }

	static ControllerProfileDraft MakeDraft(std::uint8_t player,
		std::string_view source, std::optional<std::uint64_t> titleId)
	{
		ControllerProfileDraft draft;
		draft.name = titleId ? "title-profile" : std::string(source);
		draft.sourceProfileName = std::string(source);
		draft.player = player;
		draft.titleId = titleId;
		draft.emulatedControllerTypeId = "gamepad";
		draft.controllerSettings = {
			{ .id = "toggle", .label = "Toggle", .group = "Options",
				.kind = InputSettingKind::Toggle }
		};
		draft.mappings.push_back({ "1", "`wgi:0/Original`" });
		draft.mappings.push_back({ "65536", {} });
		draft.mappings.push_back({ "65544", {} });
		draft.dsuRoute = DsuPlayerRoute{ .player = player, .serverId = "phone",
			.remoteSlot = player };
		return draft;
	}

	std::string loadedProfile;
	std::optional<ControllerProfileDraft> savedDraft;
	std::vector<ControllerProfileDraft> savedDrafts;
	std::vector<std::string> profiles{ "base", "alternate" };
	int defaultProfileLoads = 0;
	int resetCount = 0;
	int batchSaveCount = 0;
	int previewRequests = 0;
	bool loadWithDefaultDevice = false;
};

void TestInputMapperControllerFlow()
{
	ImGuiIO& io = CreateTestImGuiContext();
	io.DisplaySize = { 1280.0f, 720.0f };
	unsigned char* pixels = nullptr;
	int width = 0;
	int height = 0;
	io.Fonts->GetTexDataAsRGBA32(&pixels, &width, &height);

	TestInputMappingHost host;
	InputMapperView mapper;
	mapper.Open(host, 0x1234, 0);
	const ShellLayoutContext context{
		.viewportPx = { 1280.0f, 720.0f }, .uiScale = 1.0f,
		.layoutUnitsW = 1280.0f, .layoutUnitsH = 720.0f,
	};
	auto frame = [&](const FrameInput& input) {
		io.DeltaTime = 1.0f / 60.0f;
		ImGui::NewFrame();
		ImGui::SetNextWindowPos({ 0.0f, 0.0f });
		ImGui::SetNextWindowSize(io.DisplaySize);
		ImGui::Begin("MapperTest", nullptr, ImGuiWindowFlags_NoDecoration);
		const InputMapperResult result = mapper.Draw(context,
			ImGui::GetWindowDrawList(), input);
		ImGui::End();
		ImGui::Render();
		return result;
	};
	auto click = [&](float x, float y) {
		io.AddMousePosEvent(x, y);
		frame({});
		io.AddMouseButtonEvent(ImGuiMouseButton_Left, true);
		const InputMapperResult result = frame({});
		io.AddMouseButtonEvent(ImGuiMouseButton_Left, false);
		frame({});
		return result;
	};

	frame({});
	Check(host.previewRequests > 0);
	bool foundPreview = false;
	const ImTextureID previewTexture = reinterpret_cast<ImTextureID>(0xCAFEu);
	if (const ImDrawData* data = ImGui::GetDrawData())
	{
		for (int listIndex = 0; listIndex < data->CmdListsCount; ++listIndex)
		{
			const ImDrawList* list = data->CmdLists[listIndex];
			for (const ImDrawCmd& command : list->CmdBuffer)
			{
				if (command.GetTexID() != previewTexture || command.ElemCount == 0)
					continue;
				ImVec2 minimum{ std::numeric_limits<float>::max(),
					std::numeric_limits<float>::max() };
				ImVec2 maximum{ std::numeric_limits<float>::lowest(),
					std::numeric_limits<float>::lowest() };
				for (unsigned int index = 0; index < command.ElemCount; ++index)
				{
					const ImDrawIdx vertexIndex =
						list->IdxBuffer[command.IdxOffset + index];
					const ImVec2 position =
						list->VtxBuffer[command.VtxOffset + vertexIndex].pos;
					minimum.x = std::min(minimum.x, position.x);
					minimum.y = std::min(minimum.y, position.y);
					maximum.x = std::max(maximum.x, position.x);
					maximum.y = std::max(maximum.y, position.y);
				}
				const float renderedHeight = maximum.y - minimum.y;
				Check(renderedHeight > 0.0f);
				Check(std::abs((maximum.x - minimum.x) / renderedHeight - 2.0f) <
					0.01f);
				foundPreview = true;
			}
		}
	}
	Check(foundPreview);

	// Header controls use flat spatial navigation.
	frame({ .right = true }); // Input device.
	frame({ .right = true }); // Profile.
	frame({ .accept = true }); // Cycle base -> alternate profile.
	Check(host.loadedProfile == "alternate");
	frame({ .left = true }); // Input device.
	frame({ .accept = true }); // Select and attach WGI.

	// Input-source navigation reaches the DSU route and mode controls.
	frame({ .down = true }); // Motion & Pointer page tab.
	frame({ .right = true }); // Input Sources page tab.
	frame({ .accept = true });
	frame({ .down = true }); // Device setting nearest this tab.
	frame({ .right = true }); // DSU server column.
	frame({ .down = true }); // Remote slot.
	frame({ .accept = true }); // Slot 0 -> 1.
	frame({ .down = true }); // Full Controller / Merge.
	frame({ .accept = true }); // Full Controller -> Merge.
	frame({ .down = true }); // Gamepad input.
	frame({ .accept = true }); // Enabled -> disabled.
	const auto saved = click(945.0f, 140.0f); // Save.
	Check(saved.saved);
	Check(host.savedDraft.has_value());
	Check(host.savedDraft->titleId == 0x1234);
	Check(host.savedDraft->sourceProfileName == "alternate");
	Check(host.savedDraft->name == "title-profile");
	Check(host.savedDraft->attachedDeviceIds == std::vector<std::string>{ "wgi:0" });
	Check(host.savedDraft->defaultDeviceId == "wgi:0");
	Check(host.savedDraft->dsuRoute.has_value());
	Check(host.savedDraft->dsuRoute->serverId == "phone");
	Check(host.savedDraft->dsuRoute->remoteSlot == 1);
	Check(host.savedDraft->dsuRoute->mode == DsuRouteMode::Merge);
	Check(!host.savedDraft->dsuRoute->gamepad);
	Check(host.resetCount == 1);

	// Disconnected motion inputs remain configurable.
	host.loadWithDefaultDevice = true;
	mapper.Open(host, 0x1234, 0);
	click(490.0f, 188.0f); // Motion & Pointer page.
	io.AddMousePosEvent(300.0f, 273.0f); // Focus Accelerometer X without activating it.
	frame({});
	frame({ .alternate = true, .controllerInputHeld = true });
	Check(!mapper.IsCapturing()); // Only A or a pointer click starts capture.
	frame({});
	frame({ .accept = true, .controllerInputHeld = true });
	Check(mapper.IsCapturing());
	frame({ .back = true, .controllerInputHeld = true });
	Check(mapper.IsCapturing()); // B is a valid binding and cannot cancel capture.
	mapper.Close();

	// Defaults remain available without named profiles.
	host.profiles.clear();
	mapper.Open(host, 0x1234, 0);
	click(1090.0f, 188.0f); // Profiles & Advanced page.
	click(245.0f, 270.0f); // Recommended defaults.
	Check(host.defaultProfileLoads == 1);
	frame({ .back = true });

	// Global profiles do not receive a title ID.
	host.savedDraft.reset();
	mapper.Open(host, std::nullopt, 0);
	const auto globalSaved = click(945.0f, 140.0f);
	Check(globalSaved.saved);
	Check(host.savedDraft.has_value());
	Check(!host.savedDraft->titleId.has_value());
	Check(host.savedDraft->name == "base");

	// X must not clear mappings.
	host.savedDraft.reset();
	mapper.Open(host, std::nullopt, 0);
	io.AddMousePosEvent(1000.0f, 270.0f);
	frame({});
	frame({ .context = true });
	click(945.0f, 140.0f);
	Check(host.savedDraft.has_value());
	Check(host.savedDraft->mappings.front().expression == "`wgi:0/Original`");

	// Controller-specific drafts survive type changes.
	host.savedDraft.reset();
	mapper.Open(host, std::nullopt, 0);
	io.AddMousePosEvent(-100.0f, -100.0f);
	frame({});
	frame({ .accept = true }); // GamePad -> Remote.
	frame({});
	click(945.0f, 140.0f);
	Check(host.savedDraft.has_value());
	Check(host.savedDraft->emulatedControllerTypeId == "remote");
	Check(host.savedDraft->mappings.front().expression == "`wgi:0/B`");

	host.savedDraft.reset();
	mapper.Open(host, std::nullopt, 0);
	io.AddMousePosEvent(-100.0f, -100.0f);
	frame({});
	frame({ .accept = true }); // GamePad -> Remote.
	frame({});
	frame({ .accept = true }); // Remote -> GamePad.
	frame({});
	click(945.0f, 140.0f);
	Check(host.savedDraft.has_value());
	Check(host.savedDraft->emulatedControllerTypeId == "gamepad");
	Check(host.savedDraft->mappings.front().expression == "`wgi:0/Original`");
	Check(!host.savedDraft->dirty);

	// Reset affects only the selected controller draft.
	host.savedDraft.reset();
	mapper.Open(host, std::nullopt, 0);
	click(1030.0f, 140.0f); // Reset.
	click(945.0f, 140.0f); // Save.
	Check(host.savedDraft.has_value());
	Check(host.savedDraft->name == "base");
	Check(host.savedDraft->emulatedControllerTypeId == "gamepad");
	Check(host.savedDraft->mappings.front().expression == "`wgi:0/A`");
	Check(host.savedDraft->controllerSettings.front().boolValue);
	Check(host.savedDraft->deviceSettings.size() == 1);
	Check(std::abs(host.savedDraft->deviceSettings.front().settings.front().numberValue -
		0.35f) < 0.0001f);

	// Player drafts survive navigation without becoming dirty.
	host.savedDrafts.clear();
	mapper.Open(host, std::nullopt, 0);
	io.AddMousePosEvent(-100.0f, -100.0f);
	frame({});
	frame({ .rightShoulder = true });
	frame({});
	frame({ .leftShoulder = true });
	frame({});
	click(945.0f, 140.0f);
	Check(host.savedDrafts.size() == 1);
	Check(host.savedDrafts.front().player == 0);
	Check(std::ranges::all_of(host.savedDrafts,
		[](const ControllerProfileDraft& draft) { return !draft.dirty; }));
	Check(host.batchSaveCount > 0);

	ImGui::DestroyContext();
}

void TestControllerProfilePersistenceAndDsuRoutes()
{
	const auto nonce = std::chrono::steady_clock::now().time_since_epoch().count();
	const auto directory = std::filesystem::temp_directory_path() /
		("uwp_imgui_frontend_profiles_" + std::to_string(nonce));
	const auto path = directory / "controller-profiles.json";

	ControllerProfileDraft active;
	active.name = "controller2";
	active.sourceProfileName = "Motion only";
	active.player = 2;
	active.emulatedControllerTypeId = "screen-controller";
	active.defaultDeviceId = "wgi:0";
	active.attachedDeviceIds = { "wgi:0", "dsu:phone:11" };
	active.controllerSettings.push_back({
		.id = "gyro_sensitivity", .label = "Gyroscope sensitivity",
		.group = "Motion", .description = "Applied before mapped output",
		.kind = InputSettingKind::Number, .numberValue = 125.0f,
		.minimum = 25.0f, .maximum = 300.0f, .step = 5.0f,
		.suffix = "%" });
	active.deviceSettings.push_back({ "wgi:0", { {
		.id = "rumble", .label = "Rumble strength", .group = "General",
		.kind = InputSettingKind::Number, .numberValue = 65.0f,
		.minimum = 0.0f, .maximum = 100.0f, .step = 5.0f,
		.suffix = "%" } } });
	active.mappings.push_back({ "gyro_x", "`dsu:phone:11/Gyro X`" });
	active.dsuRoute = DsuPlayerRoute{ .player = 2, .serverId = "phone",
		.remoteSlot = 11, .mode = DsuRouteMode::Merge, .gamepad = false,
		.motion = true, .touch = false };

	ControllerProfileDraft named = active;
	named.name = "Motion only";
	named.sourceProfileName = "Motion only";
	ControllerProfileStoreSnapshot snapshot{
		.activeProfiles = { active }, .namedProfiles = { named }
	};
	std::string error;
	Check(SaveControllerProfileStoreAtomic(path, snapshot, &error));
	Check(error.empty());

	ControllerProfileStoreSnapshot loaded;
	Check(LoadControllerProfileStore(path, loaded, &error));
	Check(error.empty());
	Check(loaded.activeProfiles.size() == 1);
	Check(loaded.namedProfiles.size() == 1);
	const ControllerProfileDraft& restored = loaded.activeProfiles.front();
	Check(restored.player == 2);
	Check(restored.sourceProfileName == "Motion only");
	Check(restored.attachedDeviceIds == active.attachedDeviceIds);
	Check(restored.controllerSettings.size() == 1);
	Check(restored.deviceSettings.size() == 1);
	Check(restored.mappings.size() == 1);
	Check(restored.mappings.front().expression == "`dsu:phone:11/Gyro X`");
	Check(restored.dsuRoute.has_value());
	Check(restored.dsuRoute->remoteSlot == 11);
	Check(!restored.dsuRoute->gamepad);
	Check(restored.dsuRoute->motion);
	Check(!restored.dsuRoute->touch);
	Check(loaded.namedProfiles.front().name == "Motion only");

	DsuRouteTable routes;
	Check(routes.Apply(loaded.activeProfiles).succeeded);
	Check(routes.RouteForPlayer(2) != nullptr);
	Check(routes.RouteForPlayer(2)->remoteSlot == 11);
	Check(routes.UsesAttachedDevices(2));
	Check(routes.Accepts(2, "phone", 11, DsuInputFeature::Motion));
	Check(!routes.Accepts(2, "phone", 11, DsuInputFeature::Gamepad));
	Check(!routes.Accepts(2, "phone", 10, DsuInputFeature::Motion));
	Check(!routes.Accepts(2, "other", 11, DsuInputFeature::Motion));

	ControllerProfileDraft invalid = restored;
	invalid.dsuRoute->mode = DsuRouteMode::FullController;
	const std::array invalidProfiles{ invalid };
	DsuRouteTable invalidRoutes;
	Check(!invalidRoutes.Apply(invalidProfiles).succeeded);

	ControllerProfileDraft duplicate = restored;
	duplicate.player = 3;
	duplicate.dsuRoute->player = 3;
	const std::array duplicateProfiles{ restored, duplicate };
	DsuRouteTable duplicateRoutes;
	Check(!duplicateRoutes.Apply(duplicateProfiles).succeeded);

	snapshot.activeProfiles.front().mappings.front().expression =
		"`dsu:phone:11/Gyro Y`";
	Check(SaveControllerProfileStoreAtomic(path, snapshot, &error));
	Check(!std::filesystem::exists(path.string() + ".tmp"));
	Check(!std::filesystem::exists(path.string() + ".bak"));
	Check(LoadControllerProfileStore(path, loaded, &error));
	Check(loaded.activeProfiles.front().mappings.front().expression ==
		"`dsu:phone:11/Gyro Y`");

	std::error_code filesystemError;
	std::filesystem::remove_all(directory, filesystemError);
}

void CheckNear(float actual, float expected, float tolerance = 0.0001f,
	const std::source_location& location = std::source_location::current())
{
	Check(std::abs(actual - expected) <= tolerance, location);
}

void TestInputMappingPresentation()
{
	const EmulatedControllerDescriptor xbox =
		MakeXboxOneSeriesControllerDescriptor();
	Check(xbox.id == "xbox-one-series");
	Check(xbox.label == "Xbox One / Series Controller");
	Check(xbox.previewLayout == ControllerPreviewLayout::XboxOneSeries);
	Check(xbox.faceButtonLabels ==
		std::array<std::string, 4>{ "Y", "B", "A", "X" });

	const std::array devices{
		InputDeviceDescriptor{
			.id = "WGIGamepad:controller-1",
			.label = "Xbox Wireless Controller",
			.api = "WGI Gamepad",
			.controls = {
				{ "6", "Left Stick Click" },
				{ "8", "Left Bumper (LB)" },
				{ "12", "A" },
				{ "13", "B" },
				{ "20", "Left Stick Right", InputControlKind::Axis },
			},
		},
		InputDeviceDescriptor{
			.id = "Keyboard:primary",
			.label = "Keyboard",
			.api = "Keyboard",
			.controls = {
				{ "32", "Space" },
			},
		},
	};
	Check(FormatInputExpressionForDisplay("`12`", devices,
		"WGIGamepad:controller-1", "Wii U A") == "A");
	Check(FormatInputExpressionForDisplay(
		"`WGIGamepad:controller-1/20` & `Keyboard:primary/32`", devices,
		"WGIGamepad:controller-1") == "Left Stick Right & Space");
	Check(FormatInputExpressionForDisplay(
		"deadzone(`WGIGamepad:controller-1/20`, 0.15)", devices,
		"WGIGamepad:controller-1") == "deadzone(Left Stick Right, 0.15)");
	Check(FormatInputExpressionForDisplay(
		"`WGIGamepad:disconnected/12`", devices, {}, "Wii U A") == "A");
	Check(FormatInputExpressionForDisplay(
		"`WGI Gamepad:legacy/6` & `WGIGamepad:legacy/8`", devices) ==
		"Left Stick Click & Left Bumper (LB)");
	const std::string disconnectedChord = FormatInputExpressionForDisplay(
		"`WGIGamepad:disconnected/12` | `WGIGamepad:disconnected/13`",
		devices);
	Check(disconnectedChord == "A | B");
	Check(disconnectedChord.find("WGI") == std::string::npos);
}

void TestMappingExpressions()
{
	TestInputValues values;
	values.values = {
		{ "A", 1.0f }, { "B", 0.25f }, { "Pad One/Left X", -0.75f },
	};
	const std::string unsafeDeviceId = "WGIGamepad:stable:{wgi/id/a`b\\c}";
	const std::string safeDeviceId = "WGIGamepad:stable-v1:776769";
	values.values[unsafeDeviceId + "/13"] = 1.0f;
	const std::string escapedReference =
		EscapeInputControlReference(unsafeDeviceId + "/13");
	MappingExpression escapedControl(escapedReference);
	Check(escapedControl.IsValid());
	CheckNear(escapedControl.Evaluate(values, 0.0), 1.0f);
	const std::array aliases{
		InputDeviceIdAlias{ unsafeDeviceId, safeDeviceId },
	};
	const auto rewritten = RewriteInputControlDeviceIds(escapedReference, aliases);
	Check(rewritten.has_value());
	Check(*rewritten == EscapeInputControlReference(safeDeviceId + "/13"));
	std::string truncatedReference = EscapeInputControlReference(unsafeDeviceId);
	truncatedReference.pop_back();
	Check(!RewriteInputControlDeviceIds(truncatedReference, aliases).has_value());

	MappingExpression arithmetic("1 + 2 * 3");
	Check(arithmetic.IsValid());
	CheckNear(arithmetic.Evaluate(values, 0.0), 7.0f);

	MappingExpression controls("`A` & (`B` + abs(`Pad One/Left X`))");
	Check(controls.IsValid());
	CheckNear(controls.Evaluate(values, 0.0), 1.0f);

	MappingExpression logic("`A` > 0.5 && `B` < 0.5 ? 0.8 : 0.1");
	CheckNear(logic.Evaluate(values, 0.0), 0.8f);

	MappingExpression functions("clamp(pow(2, 3) / 10, 0, 0.6)");
	CheckNear(functions.Evaluate(values, 0.0), 0.6f);
	MappingExpression functionSet(
		"if(not(0), min(max(sqrt(9), 2), 4) + abs(-2), 0)");
	CheckNear(functionSet.Evaluate(values, 0.0), 5.0f);
	MappingExpression trigonometry(
		"sin(0) + cos(0) + tan(0) + asin(0) + acos(1) + atan(0) + atan2(0, 1)");
	CheckNear(trigonometry.Evaluate(values, 0.0), 1.0f);

	MappingExpression operators(
		"(5 % 2) + (3 >= 3) + (2 <= 1) + (1 == 1) + (1 != 2) + "
		"(0.75 | 0.25) + (0.75 & 0.25) + (1 ^ 0) + (!0) + (~1)");
	CheckNear(operators.Evaluate(values, 0.0), 7.0f);
	MappingExpression analogOr("-0.8 | 0.2");
	CheckNear(analogOr.Evaluate(values, 0.0), -0.8f);
	MappingExpression shortCircuit("0 && unknown_reference || 1");
	CheckNear(shortCircuit.Evaluate(values, 0.0), 1.0f);

	MappingExpression deadzone("deadzone(`Pad One/Left X`, 0.25)");
	CheckNear(deadzone.Evaluate(values, 0.0), -2.0f / 3.0f);

	MappingExpression assignment("$gain += 0.25");
	CheckNear(assignment.Evaluate(values, 0.0), 0.25f);
	CheckNear(assignment.Evaluate(values, 0.1), 0.5f);
	assignment.Reset();
	CheckNear(assignment.Evaluate(values, 0.2), 0.25f);
	MappingExpression compound("$value = 8");
	CheckNear(compound.Evaluate(values, 0.0), 8.0f);
	Check(compound.Compile("$value -= 3"));
	CheckNear(compound.Evaluate(values, 0.1), -3.0f);
	Check(compound.Compile("$value *= 2"));
	CheckNear(compound.Evaluate(values, 0.2), 0.0f);
	Check(compound.Compile("$value /= 0"));
	CheckNear(compound.Evaluate(values, 0.3), 0.0f);

	MappingExpression toggle("toggle(`A`)");
	CheckNear(toggle.Evaluate(values, 0.0), 1.0f);
	CheckNear(toggle.Evaluate(values, 0.1), 1.0f);
	values.values["A"] = 0.0f;
	CheckNear(toggle.Evaluate(values, 0.2), 1.0f);
	values.values["A"] = 1.0f;
	CheckNear(toggle.Evaluate(values, 0.3), 0.0f);

	MappingExpression hold("hold(`A`, 0.2)");
	CheckNear(hold.Evaluate(values, 1.0), 0.0f);
	CheckNear(hold.Evaluate(values, 1.3), 1.0f);

	MappingExpression timer("timer(`A`)");
	CheckNear(timer.Evaluate(values, 3.0), 0.0f);
	CheckNear(timer.Evaluate(values, 3.4), 0.4f);
	values.values["A"] = 0.0f;
	CheckNear(timer.Evaluate(values, 3.5), 0.0f);

	values.values["A"] = 1.0f;
	MappingExpression tap("tap(`A`, 0.25)");
	CheckNear(tap.Evaluate(values, 4.0), 0.0f);
	values.values["A"] = 0.0f;
	CheckNear(tap.Evaluate(values, 4.2), 1.0f);
	CheckNear(tap.Evaluate(values, 4.3), 0.0f);

	values.values["Axis"] = 0.0f;
	MappingExpression smooth("smooth(`Axis`, 1)");
	CheckNear(smooth.Evaluate(values, 5.0), 0.0f);
	values.values["Axis"] = 1.0f;
	CheckNear(smooth.Evaluate(values, 6.0), 1.0f - std::exp(-0.25f));

	MappingExpression relative("relative(`Axis`, 2)");
	CheckNear(relative.Evaluate(values, 7.0), 0.0f);
	CheckNear(relative.Evaluate(values, 7.5), 0.5f);
	relative.Reset();
	CheckNear(relative.Evaluate(values, 8.0), 0.0f);

	values.values["A"] = 1.0f;
	MappingExpression pulse("pulse(`A`, 0.1)");
	CheckNear(pulse.Evaluate(values, 2.0), 1.0f);
	CheckNear(pulse.Evaluate(values, 2.2), 0.0f);

	MappingExpression divideByZero("1 / 0");
	CheckNear(divideByZero.Evaluate(values, 0.0), 0.0f);
	MappingExpression nan("pow(-1, 0.5)");
	CheckNear(nan.Evaluate(values, 0.0), 0.0f);
	MappingExpression disconnected("`Disconnected Pad/A`");
	CheckNear(disconnected.Evaluate(values, 0.0), 0.0f);
	MappingExpression invalid("unknown(`A`)");
	Check(!invalid.IsValid());
	Check(!invalid.Error().empty());
	CheckNear(invalid.Evaluate(values, 0.0), 0.0f);
	MappingExpression tooDeep("((((1))))", { .maximumNesting = 2 });
	Check(!tooDeep.IsValid());
	MappingExpression tooLong(std::string(32, '1'), { .maximumLength = 16 });
	Check(!tooLong.IsValid());
	MappingExpression tooManyTokens("1 + 2 + 3 + 4", { .maximumTokens = 4 });
	Check(!tooManyTokens.IsValid());
	MappingExpression tooMuchState("toggle(1) + pulse(1)",
		{ .maximumStatefulNodes = 1 });
	Check(!tooMuchState.IsValid());
}

}

int main()
{
	TestLayout();
	TestShellState();
	TestReusableViewState();
	TestNotificationCenter();
	TestStorageLocationMatching();
	TestEsDeMediaLayout();
	TestSettingsPageComposition();
	TestGraphicsViewSelection();
	TestLibraryViewSelection();
	TestScreenScraperArtworkMappings();
	TestMetadataEditorWorkflow();
	TestScreenScraperApiValueNormalization();
	TestScreenScraperMediaManifestLoading();
	TestGameMetadataRecordPersistence();
	TestShellArtworkAndBackgroundOverrides();
	TestFrontendNavigationAndCompanionLaunch();
	TestPremiumCarouselNavigation();
	TestPremiumFrontendInterfaceSettingsBinding();
	TestAnimatedLaunchCommit();
	TestRuntimeLaunchAnimationSetting();
	TestLocalLaunchReservationAndFailure();
	TestCompanionLocalLaunchLifecycle();
	TestCompanionIndependentScreenStreams();
	TestInputMapperControllerFlow();
	TestControllerProfilePersistenceAndDsuRoutes();
	TestInputMappingPresentation();
	TestMappingExpressions();
	TestWidgetGamepadNavigation();
	TestSetupWizardAndModalOwnership();
	TestUiInputDebounce();
	TestRyuboxSettingsCategories();
	return 0;
}
