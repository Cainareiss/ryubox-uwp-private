#pragma once

#include "GraphicsView.h"
#include "Settings.h"
#include "Types.h"

#include <array>
#include <functional>
#include <string>
#include <vector>

namespace UwpImGuiFrontend
{
#pragma push_macro("interface")
#undef interface

class Frontend;
struct ShellPresentation;

struct RyuboxChoiceSetting
{
	std::string label;
	std::vector<ChoiceItem> choices;
	std::size_t selected = 0;
	bool supported = true;
	bool applyImmediately = false;
};

struct RyuboxToggleSetting
{
	std::string label;
	bool value = false;
	bool supported = true;
	bool applyImmediately = false;
};

struct RyuboxFloatSetting
{
	std::string label;
	float value = 0.0f;
	float minimum = 0.0f;
	float maximum = 1.0f;
	const char* format = "%.2f";
	bool supported = true;
	bool applyImmediately = false;
};

struct RyuboxIntSetting
{
	std::string label;
	int value = 0;
	int minimum = 0;
	int maximum = 100;
	const char* format = "%d";
	bool supported = true;
	bool applyImmediately = false;
};

struct RyuboxSetupSettings
{
	std::string firmwareVersion = "Not installed";
	std::string firmwareStatus = "Unknown";
	std::string prodKeysStatus = "Not loaded";
	std::string titleKeysStatus = "Not loaded";
	std::vector<std::string> gameDirectories;
};

struct RyuboxSystemSettings
{
	RyuboxChoiceSetting consoleMode;
	RyuboxChoiceSetting language;
	RyuboxChoiceSetting region;
	RyuboxChoiceSetting timeZone;
	RyuboxIntSetting systemTimeOffsetMinutes;
	RyuboxToggleSetting ignoreMissingServices;
	RyuboxToggleSetting fsIntegrityChecks;
	RyuboxToggleSetting expandedDram;
};

struct RyuboxCpuSettings
{
	RyuboxToggleSetting ptc;
	RyuboxChoiceSetting memoryManagerMode;
	RyuboxToggleSetting hypervisor;
	RyuboxToggleSetting fastMemory;
};

struct RyuboxGraphicsSettings
{
	RyuboxChoiceSetting backend;
	RyuboxChoiceSetting preferredGpu;
	RyuboxChoiceSetting resolutionScale;
	RyuboxFloatSetting customResolutionScale;
	RyuboxChoiceSetting aspectRatio;
	RyuboxToggleSetting vsync;
	RyuboxChoiceSetting anisotropicFiltering;
	RyuboxChoiceSetting antiAliasing;
	RyuboxChoiceSetting scalingFilter;
	RyuboxFloatSetting scalingSharpness;
	RyuboxToggleSetting textureRecompression;
	RyuboxToggleSetting shaderCache;
	RyuboxToggleSetting gpuThreading;
	RyuboxToggleSetting macroHle;
	RyuboxToggleSetting colorSpacePassthrough;
	RyuboxToggleSetting showFpsCounter;
	RyuboxIntSetting fpsLimit;

	// Diagnostic state. These are deliberately read-only in the UI.
	bool nativeCropNormalization = true;
	bool subresourceHazards = true;
	bool gpuOnlyPresentation = false;
	bool persistentShaderDiskCache = true;
	bool textureViewDiagnostics = false;
	bool shaderCompileTelemetry = false;

	TextureHandle previewTexture{};
	float estimatedFps = 0.0f;
	float gpuLoadPercent = 0.0f;
	float memoryUsageGiB = 0.0f;
};

struct RyuboxAudioSettings
{
	RyuboxChoiceSetting backend;
	RyuboxFloatSetting masterVolume;
	RyuboxToggleSetting mute;
	RyuboxToggleSetting stretching;
	RyuboxIntSetting latencyMs;
};

struct RyuboxButtonBinding
{
	std::string guestButton;
	std::string hostInput;
};

struct RyuboxPlayerInputSettings
{
	bool enabled = true;
	RyuboxChoiceSetting device;
	RyuboxChoiceSetting emulatedController;
	RyuboxChoiceSetting profile;
	// Optional for upstream hosts. Ryubox uses responsive ImDrawList line-art instead of a controller photo.
	TextureHandle controllerPreview{};
	std::string connectionStatus = "Controller connected";
	std::string batteryText;
	RyuboxFloatSetting leftDeadzone;
	RyuboxFloatSetting rightDeadzone;
	RyuboxFloatSetting stickRange;
	RyuboxFloatSetting triggerThreshold;
	RyuboxToggleSetting rumble;
	RyuboxFloatSetting rumbleStrength;
	RyuboxToggleSetting motion;
	RyuboxFloatSetting gyroSensitivity;
	RyuboxFloatSetting gyroDeadzone;
	RyuboxToggleSetting invertY;
	RyuboxChoiceSetting dsuServer;
	RyuboxIntSetting dsuSlot;
	std::vector<RyuboxButtonBinding> bindings;
};

struct RyuboxControllerSettings
{
	std::array<RyuboxPlayerInputSettings, 8> players;
	std::size_t selectedPlayer = 0;
	bool perGameProfile = false;
	std::string activeTitleId;
};

struct RyuboxNetworkSettings
{
	RyuboxToggleSetting guestInternetAccess;
	RyuboxChoiceSetting multiplayerMode;
	RyuboxChoiceSetting networkInterface;
};

struct RyuboxLogSettings
{
	RyuboxToggleSetting fileLogging;
	RyuboxToggleSetting trace;
	RyuboxToggleSetting debug;
	RyuboxToggleSetting information;
	RyuboxToggleSetting warning;
	RyuboxToggleSetting error;
	RyuboxToggleSetting stub;
	RyuboxToggleSetting guestLogs;
	RyuboxToggleSetting fsAccessLogs;
	RyuboxToggleSetting graphicsDebug;
	RyuboxToggleSetting shaderTelemetry;
	RyuboxToggleSetting performanceCsv;
	RyuboxToggleSetting textureViewFailures;
	RyuboxToggleSetting swizzleUpload;
	RyuboxToggleSetting presentationLogs;
	RyuboxToggleSetting syncStalls;
};

struct RyuboxInterfaceSettings
{
	RyuboxChoiceSetting catalogueLayout;
	RyuboxToggleSetting carouselReflections;
	RyuboxToggleSetting carouselGlow;
	RyuboxFloatSetting carouselDepth;
	RyuboxToggleSetting dynamicBackground;
	RyuboxToggleSetting launchAnimation;
	RyuboxToggleSetting controllerHints;
	RyuboxToggleSetting uiSoundEffects;
	RyuboxChoiceSetting language;
	RyuboxChoiceSetting searchBehavior;
	RyuboxToggleSetting confirmExit;
};

struct RyuboxSettingsModel
{
	RyuboxSetupSettings setup;
	RyuboxSystemSettings system;
	RyuboxCpuSettings cpu;
	RyuboxGraphicsSettings graphics;
	RyuboxAudioSettings audio;
	RyuboxControllerSettings controllers;
	RyuboxNetworkSettings network;
	RyuboxLogSettings logs;
	RyuboxInterfaceSettings interface;
};

enum class RyuboxSettingsAction
{
	AddGameDirectory,
	RemoveGameDirectory,
	RefreshLibrary,
	InstallFirmware,
	ImportProdKeys,
	ImportTitleKeys,
	ClearKeys,
	SaveControllerProfile,
	LoadControllerProfile,
	DuplicateControllerProfile,
	DeleteControllerProfile,
	RestoreControllerDefaults,
	TestRumble,
	ApplyGraphics,
	RestoreGraphicsDefaults,
	ClearShaderCache,
	PickCoversFolder,
};

struct RyuboxSettingsCallbacks
{
	std::function<void(RyuboxSettingsAction)> action;
	std::function<void(std::string_view)> changed;
	std::function<void(std::size_t, std::string_view)> captureBinding;
	// When strictRuntimeBinding is enabled, a setting is interactive only if the host
	// explicitly reports a real Ryubox/Core binding for its id. This prevents a pretty
	// LocalSettings-only toggle from pretending to change emulator behavior.
	std::function<bool(std::string_view)> isBound;
	bool strictRuntimeBinding = false;
};

class IRyuboxSettingsRuntime
{
public:
	virtual ~IRyuboxSettingsRuntime() = default;

	// Refreshes the UI model from the real ConfigurationState/CoreHost/frontend state.
	// Return false if the runtime is not ready yet.
	virtual bool RefreshModel(RyuboxSettingsModel& model) = 0;
	// Applies exactly one changed setting to the real runtime or its next-launch config.
	virtual bool ApplySetting(std::string_view id, const RyuboxSettingsModel& model) = 0;
	// The UI will disable any id that is not explicitly bound when strict mode is used.
	[[nodiscard]] virtual bool IsSettingBound(std::string_view id) const = 0;
	virtual void ExecuteSettingsAction(RyuboxSettingsAction action,
		const RyuboxSettingsModel& model) = 0;
	virtual void CaptureControllerBinding(std::size_t player, std::string_view guestButton) = 0;
};

// Creates callbacks that never silently persist fake emulator settings. Hosts should use
// this adapter for the Ryubox build and implement IRyuboxSettingsRuntime against the
// actual C#/C++ bridge.
[[nodiscard]] RyuboxSettingsCallbacks MakeRyuboxRuntimeCallbacks(
	IRyuboxSettingsRuntime& runtime, RyuboxSettingsModel& model);

[[nodiscard]] bool RefreshRyuboxSettingsFromRuntime(
	IRyuboxSettingsRuntime& runtime, RyuboxSettingsModel& model);

// Frontend-local settings can be wired without touching the emulator core. These
// helpers keep the UI model and the real FrontendConfiguration synchronized.
[[nodiscard]] bool ApplyRyuboxFrontendInterfaceSetting(Frontend& frontend,
	std::string_view id, const RyuboxInterfaceSettings& settings);
void SyncRyuboxFrontendInterfaceSettings(const Frontend& frontend,
	RyuboxInterfaceSettings& settings);
[[nodiscard]] bool ApplyRyuboxShellPresentationSetting(ShellPresentation& presentation,
	std::string_view id, const RyuboxInterfaceSettings& settings);
void SyncRyuboxShellPresentationSettings(const ShellPresentation& presentation,
	RyuboxInterfaceSettings& settings);

struct RyuboxQuickViewResult
{
	bool closeRequested = false;
	bool applyRequested = false;
	bool resetRequested = false;
};

[[nodiscard]] RyuboxSettingsModel MakeDefaultRyuboxSettingsModel();

[[nodiscard]] RyuboxQuickViewResult DrawRyuboxControllerQuickView(
	RyuboxControllerSettings& model, const RyuboxSettingsCallbacks& callbacks,
	ImVec2 size = ImVec2(0.0f, 0.0f));

[[nodiscard]] RyuboxQuickViewResult DrawRyuboxGraphicsQuickView(
	RyuboxGraphicsSettings& model, const RyuboxSettingsCallbacks& callbacks,
	ImVec2 size = ImVec2(0.0f, 0.0f));

[[nodiscard]] std::vector<SettingsExtensionPage> BuildRyuboxSettingsExtensions(
	RyuboxSettingsModel& model, const RyuboxSettingsCallbacks& callbacks);

void DrawRyuboxInterfaceSettings(RyuboxInterfaceSettings& model,
	const RyuboxSettingsCallbacks& callbacks);

#pragma pop_macro("interface")
}
