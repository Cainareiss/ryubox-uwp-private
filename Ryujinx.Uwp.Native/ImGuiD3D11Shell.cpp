#include "ImGuiD3D11Shell.h"
#include "RyuboxFrontendHost.h"

#include "third_party/imgui/imgui.h"
#include "../UWP-ImGuiFrontend/include/UwpImGuiFrontend/Frontend.h"
#include "../UWP-ImGuiFrontend/include/UwpImGuiFrontend/ShellRenderer.h"
#include "../UWP-ImGuiFrontend/include/UwpImGuiFrontend/RyuboxSettings.h"

#include <d3dcompiler.h>
#include <xaudio2.h>
#include <algorithm>
#include <array>
#include <cstdio>
#include <cstring>
#include <cmath>
#include <sstream>
#include <utility>
#include <filesystem>
#include <mutex>
#include <atomic>

namespace Ryujinx::Uwp::Native
{
extern std::unique_ptr<UwpImGuiFrontend::Frontend> g_frontend;
extern std::unique_ptr<UwpImGuiFrontend::ShellRenderer> g_shellRenderer;
extern UwpImGuiFrontend::RyuboxSettingsModel g_settingsModel;
extern RyuboxFrontendHost g_ryuboxHost;

extern std::atomic<bool> g_showSettings;
extern UwpImGuiFrontend::SettingsView g_settingsView;
extern std::vector<UwpImGuiFrontend::SettingsExtensionPage> g_settingsExtensions;
extern UwpImGuiFrontend::SettingsViewModel g_settingsViewModel;
extern UwpImGuiFrontend::SettingsViewServices g_settingsServices;
extern UwpImGuiFrontend::SettingsViewCallbacks g_settingsViewCallbacks;
extern std::mutex g_settingsModelMutex;
extern UwpImGuiFrontend::RyuboxSettingsCallbacks g_ryuboxSettingsCallbacks;
extern std::atomic<bool> g_showFpsCounter;
extern std::atomic<int> g_pendingSettingsPage;

namespace
{
using Microsoft::WRL::ComPtr;

enum class UiSfxKind : std::size_t { Navigate, Confirm, Back, Error, Count };

class UiSfxPlayer final
{
public:
	~UiSfxPlayer()
	{
		if (m_source) m_source->DestroyVoice();
		if (m_master) m_master->DestroyVoice();
	}

	void Play(UiSfxKind kind)
	{
		if (!EnsureInitialized()) return;
		const auto& samples = m_samples[static_cast<std::size_t>(kind)];
		if (samples.empty()) return;
		m_source->Stop(0);
		m_source->FlushSourceBuffers();
		XAUDIO2_BUFFER buffer{};
		buffer.AudioBytes = static_cast<UINT32>(samples.size() * sizeof(std::int16_t));
		buffer.pAudioData = reinterpret_cast<const BYTE*>(samples.data());
		buffer.Flags = XAUDIO2_END_OF_STREAM;
		if (SUCCEEDED(m_source->SubmitSourceBuffer(&buffer))) m_source->Start(0);
	}

private:
	static std::vector<std::int16_t> Tone(float startHz, float endHz, float milliseconds, float gain)
	{
		constexpr float sampleRate = 48000.0f;
		constexpr float pi = 3.14159265358979323846f;
		const std::size_t count = static_cast<std::size_t>(sampleRate * milliseconds / 1000.0f);
		std::vector<std::int16_t> result(count);
		float phase = 0.0f;
		for (std::size_t i = 0; i < count; ++i)
		{
			const float t = count > 1 ? static_cast<float>(i) / static_cast<float>(count - 1) : 0.0f;
			const float hz = startHz + (endHz - startHz) * t;
			phase += 2.0f * pi * hz / sampleRate;
			const float attack = std::min(1.0f, t * 14.0f);
			const float release = std::min(1.0f, (1.0f - t) * 7.0f);
			const float value = std::sin(phase) * attack * release * gain;
			result[i] = static_cast<std::int16_t>(std::clamp(value, -1.0f, 1.0f) * 32767.0f);
		}
		return result;
	}

	bool EnsureInitialized()
	{
		if (m_source) return true;
		m_samples[0] = Tone(760.0f, 900.0f, 42.0f, 0.12f);
		m_samples[1] = Tone(620.0f, 1080.0f, 82.0f, 0.16f);
		m_samples[2] = Tone(560.0f, 330.0f, 70.0f, 0.13f);
		m_samples[3] = Tone(220.0f, 145.0f, 150.0f, 0.18f);
		if (FAILED(XAudio2Create(&m_engine, 0, XAUDIO2_DEFAULT_PROCESSOR))) return false;
		if (FAILED(m_engine->CreateMasteringVoice(&m_master))) return false;
		WAVEFORMATEX format{};
		format.wFormatTag = WAVE_FORMAT_PCM;
		format.nChannels = 1;
		format.nSamplesPerSec = 48000;
		format.wBitsPerSample = 16;
		format.nBlockAlign = 2;
		format.nAvgBytesPerSec = format.nSamplesPerSec * format.nBlockAlign;
		return SUCCEEDED(m_engine->CreateSourceVoice(&m_source, &format));
	}

	ComPtr<IXAudio2> m_engine;
	IXAudio2MasteringVoice* m_master = nullptr;
	IXAudio2SourceVoice* m_source = nullptr;
	std::array<std::vector<std::int16_t>, static_cast<std::size_t>(UiSfxKind::Count)> m_samples;
};

UiSfxPlayer g_uiSfx;

constexpr std::uint32_t ButtonUp = 1u << 0;
constexpr std::uint32_t ButtonDown = 1u << 1;
constexpr std::uint32_t ButtonLeft = 1u << 2;
constexpr std::uint32_t ButtonRight = 1u << 3;
constexpr std::uint32_t ButtonAccept = 1u << 4;
constexpr std::uint32_t ButtonBack = 1u << 5;
constexpr std::uint32_t ButtonAlternate = 1u << 6;
constexpr std::uint32_t ButtonMenu = 1u << 8;
constexpr std::uint32_t ButtonView = 1u << 9;
constexpr std::uint32_t ButtonLeftShoulder = 1u << 10;
	constexpr std::uint32_t ButtonRightShoulder = 1u << 11;

	const ImVec4 Background = ImVec4(0.035f, 0.027f, 0.051f, 1.0f);
	const ImVec4 Surface = ImVec4(0.075f, 0.064f, 0.094f, 1.0f);
	const ImVec4 SurfaceStrong = ImVec4(0.12f, 0.085f, 0.15f, 1.0f);
	const ImVec4 Pink = ImVec4(1.0f, 0.0f, 0.50f, 1.0f);
	const ImVec4 Cyan = ImVec4(0.0f, 0.90f, 1.0f, 1.0f);
	const ImVec4 Text = ImVec4(0.98f, 0.97f, 1.0f, 1.0f);
	const ImVec4 Muted = ImVec4(0.65f, 0.61f, 0.69f, 1.0f);

	ImU32 UiColor(const ImVec4& color)
	{
		return ImGui::GetColorU32(color);
	}

	ImVec4 CardAccent(int index)
	{
		static const ImVec4 accents[] =
		{
			ImVec4(0.99f, 0.0f, 0.40f, 1.0f),
			ImVec4(0.0f, 0.68f, 0.95f, 1.0f),
			ImVec4(0.38f, 0.95f, 0.40f, 1.0f),
			ImVec4(0.98f, 0.55f, 0.12f, 1.0f),
			ImVec4(0.64f, 0.35f, 1.0f, 1.0f),
		};
		return accents[index % (sizeof(accents) / sizeof(accents[0]))];
	}

const char VertexShader[] = R"(
cbuffer Projection : register(b0) { float4x4 projection; };
struct VS_INPUT { float2 pos : POSITION; float4 color : COLOR0; float2 uv : TEXCOORD0; };
struct PS_INPUT { float4 pos : SV_POSITION; float4 color : COLOR0; float2 uv : TEXCOORD0; };
PS_INPUT main(VS_INPUT input)
{
    PS_INPUT output;
    output.pos = mul(projection, float4(input.pos.xy, 0.0, 1.0));
    output.color = input.color;
    output.uv = input.uv;
    return output;
}
)";

const char PixelShader[] = R"(
Texture2D texture0 : register(t0);
SamplerState sampler0 : register(s0);
struct PS_INPUT { float4 pos : SV_POSITION; float4 color : COLOR0; float2 uv : TEXCOORD0; };
float4 main(PS_INPUT input) : SV_TARGET { return input.color * texture0.Sample(sampler0, input.uv); }
)";

bool IsPressed(std::uint32_t current, std::uint32_t previous, std::uint32_t button)
{
	return (current & button) != 0 && (previous & button) == 0;
}

ImVec4 Color(unsigned char r, unsigned char g, unsigned char b, unsigned char a = 255)
{
	return ImVec4(r / 255.0f, g / 255.0f, b / 255.0f, a / 255.0f);
}

std::string Utf8(const std::wstring& value)
{
	if (value.empty())
	{
		return {};
	}
	const int bytes = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()),
		nullptr, 0, nullptr, nullptr);
	if (bytes <= 0)
	{
		return {};
	}
	std::string result(static_cast<std::size_t>(bytes), '\0');
	WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), result.data(),
		bytes, nullptr, nullptr);
	return result;
}

std::wstring DisplayTitle(const std::wstring& value)
{
	const std::size_t bracket = value.find(L"[");
	std::wstring title = value.substr(0, bracket == std::wstring::npos ? value.size() : bracket);
	const std::size_t version = title.find(L" (v");
	if (version != std::wstring::npos)
	{
		title.resize(version);
	}
	const std::size_t first = title.find_first_not_of(L" \t");
	const std::size_t last = title.find_last_not_of(L" \t");
	return first == std::wstring::npos ? std::wstring{} : title.substr(first, last - first + 1);
}

} // namespace

HRESULT ImGuiD3D11Shell::Initialize(ID3D11Device* device, ID3D11DeviceContext* context,
	ID3D11RenderTargetView* renderTarget, std::uint32_t width, std::uint32_t height)
{
	if (device == nullptr || context == nullptr || renderTarget == nullptr)
	{
		return E_INVALIDARG;
	}

	Shutdown();
	m_device = device;
	m_context = context;
	m_renderTarget = renderTarget;
	m_width = std::max<std::uint32_t>(1, width);
	m_height = std::max<std::uint32_t>(1, height);

	return InitializeUi();
}

HRESULT ImGuiD3D11Shell::InitializeD3D12(void* context, std::uint32_t width, std::uint32_t height)
{
	if (!context || !width || !height) return E_INVALIDARG;
	Shutdown();
	m_d12 = std::make_unique<ImGuiD3D12Renderer>(context);
	m_width = width; m_height = height;
	HRESULT hr = InitializeUi();
	if (FAILED(hr)) Shutdown();
	return hr;
}

HRESULT ImGuiD3D11Shell::ResizeD3D12(std::uint32_t width, std::uint32_t height)
{
	if (!m_initialized || !m_d12 || !width || !height) return E_INVALIDARG;
	m_width = width; m_height = height;
	ImGui::GetIO().DisplaySize = ImVec2(float(width), float(height));
	return S_OK;
}

HRESULT ImGuiD3D11Shell::InitializeUi()
{
	IMGUI_CHECKVERSION();
	ImGui::CreateContext();
	ImGuiIO& io = ImGui::GetIO();
	io.IniFilename = nullptr;
	io.LogFilename = nullptr;
	io.ConfigFlags |= ImGuiConfigFlags_NavEnableGamepad;
	io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard;
	io.DisplaySize = ImVec2(static_cast<float>(m_width), static_cast<float>(m_height));
	wchar_t executablePath[MAX_PATH]{};
	GetModuleFileNameW(nullptr, executablePath, MAX_PATH);
	const std::filesystem::path fontPath = std::filesystem::path(executablePath).parent_path() /
		L"resources" / L"fonts" / L"Lato-Medium.ttf";
	const std::string fontUtf8 = Utf8(fontPath.native());
	if (fontUtf8.empty() || !std::filesystem::exists(fontPath) ||
		io.Fonts->AddFontFromFileTTF(fontUtf8.c_str(), 20.0f) == nullptr)
	{
		io.Fonts->AddFontDefault();
	}

	ImGuiStyle& style = ImGui::GetStyle();
	style.WindowRounding = 14.0f;
	style.ChildRounding = 10.0f;
	style.FrameRounding = 8.0f;
	style.ScrollbarRounding = 8.0f;
	style.WindowBorderSize = 0.0f;
	style.FrameBorderSize = 1.0f;
	style.WindowPadding = ImVec2(24.0f, 20.0f);
	style.ItemSpacing = ImVec2(12.0f, 12.0f);
	style.Colors[ImGuiCol_WindowBg] = Color(9, 7, 13);
	style.Colors[ImGuiCol_ChildBg] = Color(13, 13, 18);
	style.Colors[ImGuiCol_PopupBg] = Color(13, 13, 18);
	style.Colors[ImGuiCol_FrameBg] = Color(23, 18, 29);
	style.Colors[ImGuiCol_FrameBgHovered] = Color(42, 28, 49);
	style.Colors[ImGuiCol_FrameBgActive] = Color(55, 28, 53);
	style.Colors[ImGuiCol_Button] = Color(23, 18, 29);
	style.Colors[ImGuiCol_ButtonHovered] = Color(255, 0, 128);
	style.Colors[ImGuiCol_ButtonActive] = Color(0, 229, 255);
	style.Colors[ImGuiCol_Header] = Color(255, 0, 128);
	style.Colors[ImGuiCol_HeaderHovered] = Color(255, 0, 128);
	style.Colors[ImGuiCol_HeaderActive] = Color(0, 229, 255);
	style.Colors[ImGuiCol_Text] = Color(250, 248, 255);
	style.Colors[ImGuiCol_TextDisabled] = Color(166, 155, 175);
	style.Colors[ImGuiCol_Border] = Color(85, 74, 96);

	m_initialized = true;
	return CreateDeviceObjects();
}

void ImGuiD3D11Shell::Shutdown() noexcept
{
	if (ImGui::GetCurrentContext() != nullptr)
	{
		DestroyDeviceObjects();
		ImGui::DestroyContext();
	}
	m_renderTarget.Reset();
	m_overlayContext.Reset();
	m_context.Reset();
	m_device.Reset();
	m_initialized = false;
	m_pendingAction = -1;
	m_actionReleaseRequired = false;
	m_previousButtons = 0;
	m_previousLeftX = 0.0f;
	m_previousLeftY = 0.0f;
}

HRESULT ImGuiD3D11Shell::Resize(ID3D11RenderTargetView* renderTarget,
	std::uint32_t width, std::uint32_t height)
{
	if (!m_initialized || renderTarget == nullptr)
	{
		return E_UNEXPECTED;
	}

	m_renderTarget = renderTarget;
	m_width = std::max<std::uint32_t>(1, width);
	m_height = std::max<std::uint32_t>(1, height);
	ImGui::GetIO().DisplaySize = ImVec2(static_cast<float>(m_width), static_cast<float>(m_height));
	return S_OK;
}

void ImGuiD3D11Shell::SetInput(const ImGuiShellInput& input) noexcept
{
	m_input = input;
	if ((m_input.buttons & ButtonAccept) == 0 && !m_pointer.leftDown)
		m_actionReleaseRequired = false;
}

void ImGuiD3D11Shell::SetPointer(const ImGuiShellPointer& pointer) noexcept
{
	// Pointer input is telemetry only. The Xbox frontend is controller-first;
	// forwarding a stale/invalid XAML pointer button into ImGui was activating
	// retained widgets outside their frame and was the last event before the
	// native process terminated. Keep the position finite and in a harmless
	// range, but do not turn pointer presses into frontend commands.
	m_pointer = pointer;
	if (!std::isfinite(m_pointer.x) || !std::isfinite(m_pointer.y))
	{
		m_pointer.x = -1.0f;
		m_pointer.y = -1.0f;
	}
	if ((m_input.buttons & ButtonAccept) == 0 && !m_pointer.leftDown)
		m_actionReleaseRequired = false;
}

void ImGuiD3D11Shell::SetCatalogue(const wchar_t* newlineSeparatedTitles)
{
	m_titles.clear();
	m_coverTextures.clear();
	if (m_d12) for (auto id : m_d12Covers) m_d12->ReleaseTexture(id);
	m_d12Covers.clear();
	if (newlineSeparatedTitles == nullptr)
	{
		return;
	}

	std::wstringstream stream(newlineSeparatedTitles);
	std::wstring title;
	while (std::getline(stream, title))
	{
		if (!title.empty())
		{
			m_titles.push_back(title);
		}
	}
	// The supplied Eden reference opens on Sonic Mania. Keep that pleasant
	// landing state when the catalogue is first populated, while preserving
	// the user's current selection on subsequent refreshes.
	if (m_selectedIndex == 0)
	{
		for (std::size_t index = 0; index < m_titles.size(); index++)
		{
			if (m_titles[index].find(L"Sonic Mania") != std::wstring::npos)
			{
				m_selectedIndex = static_cast<int>(index);
				break;
			}
		}
	}
	m_selectedIndex = std::clamp(m_selectedIndex, 0, std::max(0, static_cast<int>(m_titles.size()) - 1));
	m_coverTextures.resize(m_titles.size());
	m_d12Covers.resize(m_titles.size());
	g_ryuboxHost.ReplaceCatalogue(m_titles);
	if (g_frontend)
		g_frontend->RefreshCatalogue();
	if (g_shellRenderer)
		g_shellRenderer->ClearTextures();
}

HRESULT ImGuiD3D11Shell::SetCover(int index, const void* rgbaPixels,
	std::uint32_t width, std::uint32_t height, std::uint32_t rowPitch)
{
	if (!m_initialized || rgbaPixels == nullptr || width == 0 || height == 0
		|| rowPitch < width * 4u || index < 0
		|| index >= static_cast<int>(m_titles.size()))
	{
		return E_INVALIDARG;
	}

	if (m_d12)
	{
		UINT64 texture = 0;
		HRESULT hr = m_d12->CreateTexture(rgbaPixels, width, height, rowPitch, &texture);
		if (FAILED(hr)) return hr;
		m_d12->ReleaseTexture(m_d12Covers[index]);
		m_d12Covers[index] = texture;
		g_ryuboxHost.SetCatalogueTexture(index, { static_cast<std::uintptr_t>(texture), width, height });
		if (g_shellRenderer) g_shellRenderer->ClearTextures();
		return S_OK;
	}

	D3D11_TEXTURE2D_DESC description{};
	description.Width = width;
	description.Height = height;
	description.MipLevels = 1;
	description.ArraySize = 1;
	description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
	description.SampleDesc.Count = 1;
	description.Usage = D3D11_USAGE_DEFAULT;
	description.BindFlags = D3D11_BIND_SHADER_RESOURCE;
	D3D11_SUBRESOURCE_DATA data{};
	data.pSysMem = rgbaPixels;
	data.SysMemPitch = rowPitch;

	ComPtr<ID3D11Texture2D> texture;
	HRESULT result = m_device->CreateTexture2D(&description, &data, &texture);
	if (FAILED(result))
	{
		return result;
	}

	ComPtr<ID3D11ShaderResourceView> view;
	result = m_device->CreateShaderResourceView(texture.Get(), nullptr, &view);
	if (FAILED(result))
	{
		return result;
	}
	m_coverTextures[static_cast<std::size_t>(index)] = std::move(view);
	g_ryuboxHost.SetCatalogueTexture(static_cast<std::size_t>(index), {
		reinterpret_cast<std::uintptr_t>(m_coverTextures[static_cast<std::size_t>(index)].Get()),
		width, height });
	if (g_shellRenderer)
		g_shellRenderer->ClearTextures();
	return S_OK;
}

void ImGuiD3D11Shell::SetStatus(const wchar_t* keysStatus, const wchar_t* firmwareStatus) noexcept
{
	std::scoped_lock settingsModelLock(g_settingsModelMutex);
	m_keysStatus = keysStatus != nullptr ? keysStatus : L"nao configuradas";
	m_firmwareStatus = firmwareStatus != nullptr ? firmwareStatus : L"nao instalado";
	g_settingsModel.setup.prodKeysStatus = Utf8(m_keysStatus);
	g_settingsModel.setup.titleKeysStatus = Utf8(m_keysStatus);
	g_settingsModel.setup.firmwareStatus = Utf8(m_firmwareStatus);
	g_settingsModel.setup.firmwareVersion = m_firmwareStatus == L"instalado" ?
		"Instalado no sandbox" : "Nao instalado";
}

void ImGuiD3D11Shell::SetOperation(const wchar_t* message, float progress, int state) noexcept
{
	m_operationMessage = message != nullptr ? message : L"";
	m_operationProgress = std::clamp(progress, -1.0f, 1.0f);
	m_operationState = std::clamp(state, 0, 3);
}

void ImGuiD3D11Shell::SetOnboardingState(bool completed, int language, int theme,
	bool keysReady, bool firmwareReady, bool gamesReady) noexcept
{
	m_onboardingCompleted = completed;
	m_onboardingLanguage = language == 1 ? 1 : 0;
	m_onboardingTheme = theme == 1 ? 1 : 0;
	m_onboardingKeysReady = keysReady;
	m_onboardingFirmwareReady = firmwareReady;
	m_onboardingGamesReady = gamesReady;
	if (g_frontend)
		g_frontend->SetInterfaceLanguage(m_onboardingLanguage == 1);
	if (completed)
	{
		m_onboardingPage = 0;
		m_onboardingFirmwarePopup = false;
	}
}

int ImGuiD3D11Shell::ConsumeAction() noexcept
{
	const int action = m_pendingAction;
	m_pendingAction = -1;
	return action;
}

void ImGuiD3D11Shell::SetPendingAction(int action) noexcept
{
	if (action == -1)
	{
		m_pendingAction = -1;
		m_actionReleaseRequired = false;
		return;
	}

	// Host actions are edge-triggered. Several retained controls can observe
	// the same ImGui navigation activation in one frame; keep exactly one
	// command queued until A/the pointer has physically returned to neutral.
	if (m_pendingAction == -1 && !m_actionReleaseRequired)
	{
		m_pendingAction = action;
		m_actionReleaseRequired = true;
	}
}

void ImGuiD3D11Shell::UpdateInputState()
{
	ImGuiIO& io = ImGui::GetIO();
	// The UWP host supplies both Xbox and translated keyboard navigation as
	// ImGui gamepad key events. Without HasGamepad, ImGui accepts the events
	// into its queue but does not run spatial navigation inside settings
	// content, leaving focus stranded on the first action.
	io.BackendFlags |= ImGuiBackendFlags_HasGamepad;
	const std::uint32_t buttons = m_input.buttons;
	const bool up = (buttons & ButtonUp) != 0 || m_input.leftY > 0.60f;
	const bool down = (buttons & ButtonDown) != 0 || m_input.leftY < -0.60f;
	const bool left = (buttons & ButtonLeft) != 0 || m_input.leftX < -0.60f;
	const bool right = (buttons & ButtonRight) != 0 || m_input.leftX > 0.60f;
	const bool previousUp = m_previousLeftY > 0.60f;
	const bool previousDown = m_previousLeftY < -0.60f;
	const bool previousLeft = m_previousLeftX < -0.60f;
	const bool previousRight = m_previousLeftX > 0.60f;
	io.AddKeyEvent(ImGuiKey_GamepadDpadUp, up);
	io.AddKeyEvent(ImGuiKey_GamepadDpadDown, down);
	io.AddKeyEvent(ImGuiKey_GamepadDpadLeft, left);
	io.AddKeyEvent(ImGuiKey_GamepadDpadRight, right);
	io.AddKeyEvent(ImGuiKey_GamepadFaceDown, (buttons & ButtonAccept) != 0);
	io.AddKeyEvent(ImGuiKey_GamepadFaceRight, (buttons & ButtonBack) != 0);
	io.AddKeyEvent(ImGuiKey_GamepadStart, (buttons & ButtonMenu) != 0);
	io.AddKeyEvent(ImGuiKey_GamepadBack, (buttons & ButtonView) != 0);
	io.AddKeyEvent(ImGuiKey_GamepadL1, (buttons & ButtonLeftShoulder) != 0);
	io.AddKeyEvent(ImGuiKey_GamepadR1, (buttons & ButtonRightShoulder) != 0);
	io.AddKeyAnalogEvent(ImGuiKey_GamepadLStickLeft, left, m_input.leftX < 0 ? -m_input.leftX : 0.0f);
	io.AddKeyAnalogEvent(ImGuiKey_GamepadLStickRight, right, m_input.leftX > 0 ? m_input.leftX : 0.0f);
	io.AddKeyAnalogEvent(ImGuiKey_GamepadLStickUp, up, m_input.leftY > 0 ? m_input.leftY : 0.0f);
	io.AddKeyAnalogEvent(ImGuiKey_GamepadLStickDown, down, m_input.leftY < 0 ? -m_input.leftY : 0.0f);
	io.AddMousePosEvent(m_pointer.x, m_pointer.y);
	// Keep controller/keyboard navigation as the primary path, but forward
	// physical mouse buttons on PC as well. Without these events ImGui could
	// draw the rail correctly while every mouse click was ignored.
	io.AddMouseButtonEvent(ImGuiMouseButton_Left, m_pointer.leftDown);
	io.AddMouseButtonEvent(ImGuiMouseButton_Right, m_pointer.rightDown);
	if (m_pointer.wheel != 0.0f)
	{
		io.AddMouseWheelEvent(0.0f, m_pointer.wheel);
		m_pointer.wheel = 0.0f;
	}
	m_previousButtons = buttons;
	m_previousLeftX = m_input.leftX;
	m_previousLeftY = m_input.leftY;
}


void ImGuiD3D11Shell::BuildFrame()
{
	if (g_frontend && g_shellRenderer)
	{
		UwpImGuiFrontend::FrameInput frameInput{};
		if (m_input.buttons & ButtonUp) frameInput.up = true;
		if (m_input.buttons & ButtonDown) frameInput.down = true;
		if (m_input.buttons & ButtonLeft) frameInput.left = true;
		if (m_input.buttons & ButtonRight) frameInput.right = true;
		if (m_input.buttons & ButtonAccept) frameInput.accept = true;
		if (m_input.buttons & ButtonBack) frameInput.back = true;
		if (m_input.buttons & ButtonAlternate) frameInput.alternate = true;
		if (m_input.buttons & (1u << 7)) frameInput.context = true;
		if (m_input.buttons & ButtonMenu) frameInput.menu = true;
		if (m_input.buttons & ButtonView) frameInput.view = true;
		if (m_input.buttons & ButtonLeftShoulder) frameInput.leftShoulder = true;
		if (m_input.buttons & ButtonRightShoulder) frameInput.rightShoulder = true;

		frameInput.leftStickX = m_input.leftX;
		frameInput.leftStickY = m_input.leftY;
		frameInput.rightStickX = m_input.rightX;
		frameInput.rightStickY = m_input.rightY;
		frameInput.deltaSeconds = ImGui::GetIO().DeltaTime;

		if (!m_onboardingCompleted)
		{
			DrawOnboarding();
		}
		else
		{

		// Host commands are raised while Frontend::HandleInput is unwinding.
		// Commit the transition only at the start of a later frame so the
		// settings model cannot be changed from inside its navigation callback.
		const int pendingSettingsPage = g_pendingSettingsPage.exchange(
			0, std::memory_order_acq_rel);
		if (pendingSettingsPage > 0)
			g_showSettings.store(true, std::memory_order_release);
		const bool showingSettings = g_showSettings.load(std::memory_order_acquire);

		static bool s_wasShowingSettings = false;
		if (showingSettings)
		{
			std::scoped_lock settingsLock(g_settingsModelMutex);
			if (pendingSettingsPage == 1)
				g_settingsView.SelectPage("ryubox-setup", g_settingsViewModel,
					g_settingsExtensions);
			else if (pendingSettingsPage == 2)
				g_settingsView.SelectPage("ryubox-controllers", g_settingsViewModel,
					g_settingsExtensions);
			else if (pendingSettingsPage == 3)
				g_settingsView.SelectPage("ryubox-graphics", g_settingsViewModel,
					g_settingsExtensions);
			const bool justOpened = !s_wasShowingSettings;
			s_wasShowingSettings = true;

			UwpImGuiFrontend::WidgetInputState widgetInput{};
			widgetInput.accept = frameInput.accept;
			widgetInput.back = frameInput.back;
			widgetInput.up = frameInput.up || frameInput.leftStickY > 0.60f;
			widgetInput.down = frameInput.down || frameInput.leftStickY < -0.60f;
			widgetInput.left = frameInput.left || frameInput.leftStickX < -0.60f;
			widgetInput.right = frameInput.right || frameInput.leftStickX > 0.60f;
			widgetInput.leftShoulder = frameInput.leftShoulder;
			widgetInput.rightShoulder = frameInput.rightShoulder;

			// If settings was just opened on this frame, consume accept so the
			// button held to open settings does not immediately trigger the first
			// action (e.g. "Instalar firmware") on page 0.
			if (justOpened)
			{
				m_suppressSettingsAcceptUntilRelease = frameInput.accept;
			}
			if (m_suppressSettingsAcceptUntilRelease)
			{
				widgetInput.accept = false;
				if (!frameInput.accept)
					m_suppressSettingsAcceptUntilRelease = false;
			}

			auto& settingsState = g_settingsView.State();
			const bool acceptPressed = widgetInput.accept && !m_previousWidgetInput.accept;
			const bool backPressed = widgetInput.back && !m_previousWidgetInput.back;
			const bool rightPressed = widgetInput.right && !m_previousWidgetInput.right;
			const bool upPressed = widgetInput.up && !m_previousWidgetInput.up;
			const bool downPressed = widgetInput.down && !m_previousWidgetInput.down;
			const bool leftShoulderPressed = widgetInput.leftShoulder && !m_previousWidgetInput.leftShoulder;
			const bool rightShoulderPressed = widgetInput.rightShoulder && !m_previousWidgetInput.rightShoulder;
			// A or Right enters the selected page. Once inside, all four
			// directions belong to ImGui so sliders, choices and vertically
			// stacked actions (including prod.keys) remain reachable. B returns
			// to the tab list; pressing B there closes settings.
			if (settingsState.focusedPane == UwpImGuiFrontend::SplitPane::Navigation &&
				(rightPressed || acceptPressed))
			{
				settingsState.RequestFocus(UwpImGuiFrontend::SplitPane::Content);
				if (acceptPressed)
				{
					widgetInput.accept = false;
					m_suppressSettingsAcceptUntilRelease = true;
				}
			}
			bool backReturnedToNavigation = false;
			if (backPressed &&
				settingsState.focusedPane == UwpImGuiFrontend::SplitPane::Content)
			{
				settingsState.RequestFocus(UwpImGuiFrontend::SplitPane::Navigation);
				widgetInput.back = false;
				backReturnedToNavigation = true;
			}
			const auto settingsTabs = UwpImGuiFrontend::BuildSettingsTabs(
				g_settingsViewModel, g_settingsExtensions);
			// Keep retained settings selection and ImGui navigation focus in sync.
			// MoveSelection alone changes only the model index; RequestFocus also
			// tells SettingsView to attach the navigation cursor to the new tab.
			auto moveSettingsTab = [&](int delta) {
				const std::size_t before = settingsState.selected;
				settingsState.MoveSelection(delta, settingsTabs.size());
				if (settingsState.selected != before)
					settingsState.RequestFocus(UwpImGuiFrontend::SplitPane::Navigation);
			};
			if (settingsState.focusedPane == UwpImGuiFrontend::SplitPane::Navigation)
			{
				if (upPressed)
					moveSettingsTab(-1);
				else if (downPressed)
					moveSettingsTab(1);
			}
			if (leftShoulderPressed)
				moveSettingsTab(-1);
			else if (rightShoulderPressed)
				moveSettingsTab(1);
			const auto& themes = UwpImGuiFrontend::ShellThemes();
			if (!themes.empty())
				UwpImGuiFrontend::BeginWidgetFrame(themes.front(), widgetInput,
					m_previousWidgetInput, true);
			if (backPressed && !backReturnedToNavigation &&
				settingsState.focusedPane == UwpImGuiFrontend::SplitPane::Navigation)
			{
				g_showSettings.store(false, std::memory_order_release);
				frameInput.back = false;
			}
			
			ImGui::SetNextWindowPos(ImVec2(0, 0));
			ImGui::SetNextWindowSize(ImVec2(static_cast<float>(m_width), static_cast<float>(m_height)));
			ImGui::Begin("SettingsFullscreen", nullptr, ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoBackground | ImGuiWindowFlags_NoSavedSettings);
			ImGui::TextColored(ImVec4(1.0f, 0.0f, 0.50f, 1.0f), "RYUBOX  /  CONFIGURACOES");
			ImGui::SameLine();
			ImGui::TextDisabled("   B  voltar   |   D-pad/analogico  navegar   |   A  selecionar");
			ImGui::Separator();
			try
			{
				g_settingsView.Draw(
					g_settingsViewModel,
					g_settingsServices,
					g_settingsViewCallbacks,
					g_settingsExtensions,
					ImGui::GetContentRegionAvail());
			}
			catch (...)
			{
				ImGui::TextColored(ImVec4(1.0f, 0.25f, 0.30f, 1.0f),
					"Falha ao carregar uma pagina de configuracoes.");
			}
			ImGui::End();
			if (!themes.empty())
				UwpImGuiFrontend::EndWidgetFrame();
			m_previousWidgetInput = widgetInput;
		}
		else
		{
			s_wasShowingSettings = false;
			m_suppressSettingsAcceptUntilRelease = false;
			m_previousWidgetInput = {};
			g_frontend->HandleInput(frameInput);
		}

		if (!g_showSettings.load(std::memory_order_acquire))
		{
			UwpImGuiFrontend::ShellPresentation presentation{};
			presentation.themeIndex = m_onboardingTheme == 1 ? 2 : 0;
			presentation.showActionHints = true;
			presentation.controllerStatus = m_d12 ? "Xbox Series  •  D3D12" : "Xbox Series  •  D3D11";
			if (!g_frontend->Catalogue().empty())
			{
				presentation.backgroundItem =
					g_frontend->Catalogue()[g_frontend->State().SelectedIndex()].id;
				presentation.backgroundArtwork = UwpImGuiFrontend::MediaKind::Cover2D;
			}
			g_shellRenderer->Draw(*g_frontend, presentation, ImGui::GetIO().DeltaTime);
		}
		}


		// Onboarding owns its progress presentation. Rendering the global status
		// window at the same time produced the two simultaneous 100% panels that
		// trapped controller focus during keys/firmware/folder setup.
		if (m_onboardingCompleted && m_operationState != 0 && !m_operationMessage.empty())
		{
			const ImVec4 accent = m_operationState == 3
				? ImVec4(1.0f, 0.25f, 0.28f, 1.0f)
				: (m_operationState == 2 ? ImVec4(0.25f, 0.92f, 0.55f, 1.0f)
					: ImVec4(0.0f, 0.86f, 1.0f, 1.0f));
			const float panelWidth = std::min(760.0f, static_cast<float>(m_width) - 48.0f);
			ImGui::SetNextWindowPos(ImVec2((static_cast<float>(m_width) - panelWidth) * 0.5f,
				static_cast<float>(m_height) - 112.0f));
			ImGui::SetNextWindowSize(ImVec2(panelWidth, 82.0f));
			ImGui::PushStyleColor(ImGuiCol_WindowBg, ImVec4(0.055f, 0.035f, 0.075f, 0.97f));
			ImGui::PushStyleColor(ImGuiCol_Border, accent);
			ImGui::PushStyleVar(ImGuiStyleVar_WindowRounding, 12.0f);
			ImGui::PushStyleVar(ImGuiStyleVar_WindowBorderSize, 1.5f);
			ImGui::Begin("##ryubox-operation", nullptr,
				ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoMove |
				ImGuiWindowFlags_NoSavedSettings | ImGuiWindowFlags_NoInputs);
			ImGui::TextColored(accent, "%s", m_operationState == 3 ? "ERRO" :
				(m_operationState == 2 ? "CONCLUIDO" : "PROCESSANDO"));
			ImGui::SameLine();
			ImGui::TextWrapped("%s", Utf8(m_operationMessage).c_str());
			if (m_operationState == 1)
			{
				float fraction = m_operationProgress;
				if (fraction < 0.0f)
					fraction = std::fmod(static_cast<float>(ImGui::GetTime()) * 0.35f, 1.0f);
				ImGui::PushStyleColor(ImGuiCol_PlotHistogram, accent);
				char percentage[16]{};
				std::snprintf(percentage, sizeof(percentage), "%d%%",
					static_cast<int>(std::round(std::clamp(fraction, 0.0f, 1.0f) * 100.0f)));
				ImGui::ProgressBar(fraction, ImVec2(-1.0f, 16.0f), percentage);
				ImGui::PopStyleColor();
			}
			ImGui::End();
			ImGui::PopStyleVar(2);
			ImGui::PopStyleColor(2);
		}
	}
}

void ImGuiD3D11Shell::DrawOnboarding()
{
	const bool english = m_onboardingLanguage == 1;
	const auto& themes = UwpImGuiFrontend::ShellThemes();
	const int themeIndex = m_onboardingTheme == 1 ? 2 : 0;
	const UwpImGuiFrontend::ShellTheme& theme = themes[static_cast<std::size_t>(themeIndex)];
	const ImVec2 viewport{ static_cast<float>(m_width), static_cast<float>(m_height) };
	const float scale = std::max(0.72f, std::min(viewport.x / 1280.0f, viewport.y / 720.0f));

	ImGui::SetNextWindowPos({ 0.0f, 0.0f });
	ImGui::SetNextWindowSize(viewport);
	ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, { 0.0f, 0.0f });
	ImGui::PushStyleColor(ImGuiCol_WindowBg, theme.background);
	ImGui::Begin("##ryubox-onboarding-background", nullptr,
		ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoMove |
		ImGuiWindowFlags_NoSavedSettings | ImGuiWindowFlags_NoBringToFrontOnFocus);
	ImDrawList* draw = ImGui::GetWindowDrawList();
	draw->AddRectFilledMultiColor({ 0.0f, 0.0f }, viewport,
		ImGui::ColorConvertFloat4ToU32(theme.backgroundAccent),
		ImGui::ColorConvertFloat4ToU32(theme.background),
		ImGui::ColorConvertFloat4ToU32(theme.background),
		ImGui::ColorConvertFloat4ToU32(theme.backgroundAccent));
	const ImU32 glow = UwpImGuiFrontend::ColorWithAlpha(theme.cursorNormal, 0.08f);
	draw->AddCircleFilled({ viewport.x * 0.12f, viewport.y * 0.18f }, 180.0f * scale, glow, 48);
	draw->AddCircleFilled({ viewport.x * 0.88f, viewport.y * 0.80f }, 230.0f * scale,
		UwpImGuiFrontend::ColorWithAlpha(theme.pageIndicatorActive, 0.06f), 48);

	const float cardWidth = std::min(900.0f * scale, viewport.x - 48.0f);
	const float cardHeight = std::min(570.0f * scale, viewport.y - 42.0f);
	ImGui::SetCursorPos({ (viewport.x - cardWidth) * 0.5f, (viewport.y - cardHeight) * 0.5f });
	ImGui::PushStyleColor(ImGuiCol_ChildBg, theme.panelBase);
	ImGui::PushStyleColor(ImGuiCol_Border, theme.panelBorder);
	ImGui::PushStyleColor(ImGuiCol_Text, theme.textPrimary);
	ImGui::PushStyleColor(ImGuiCol_Button, theme.panelHighlight);
	ImGui::PushStyleColor(ImGuiCol_ButtonHovered, UwpImGuiFrontend::ColorWithAlpha(theme.cursorNormal, 0.30f));
	ImGui::PushStyleColor(ImGuiCol_ButtonActive, UwpImGuiFrontend::ColorWithAlpha(theme.cursorNormal, 0.48f));
	ImGui::PushStyleColor(ImGuiCol_CheckMark, theme.cursorNormal);
	ImGui::PushStyleVar(ImGuiStyleVar_ChildRounding, 22.0f * scale);
	ImGui::PushStyleVar(ImGuiStyleVar_ChildBorderSize, 1.0f);
	ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, { 44.0f * scale, 32.0f * scale });
	ImGui::BeginChild("##ryubox-onboarding-card", { cardWidth, cardHeight }, true,
		ImGuiWindowFlags_NoSavedSettings);

	ImGui::TextColored(theme.cursorNormal, "RYUBOX");
	ImGui::SameLine();
	ImGui::TextColored(theme.textSecondary, "  %s %d / 3", english ? "SETUP" : "CONFIGURACAO",
		m_onboardingPage + 1);
	ImGui::ProgressBar((m_onboardingPage + 1) / 3.0f, { -1.0f, 7.0f }, "");
	ImGui::Spacing();
	ImGui::Spacing();

	auto statusText = [&](bool ready) -> const char* {
		return ready ? (english ? "Ready" : "Concluido") : (english ? "Pending" : "Pendente");
	};
	auto statusColor = [&](bool ready) -> ImVec4 {
		return ready ? ImVec4(0.20f, 0.90f, 0.54f, 1.0f) : theme.textSecondary;
	};
	auto fullButton = [&](const char* label) {
		return ImGui::Button(label, { -1.0f, 48.0f * scale });
	};
	const bool operationActive = m_operationState == 1;

	if (m_onboardingPage == 0)
	{
		ImGui::SetWindowFontScale(1.42f);
		ImGui::TextUnformatted(english ? "Welcome" : "Boas-vindas");
		ImGui::SetWindowFontScale(1.0f);
		ImGui::TextColored(theme.textSecondary, "%s",
			english ? "Select your language" : "Selecione seu idioma");
		ImGui::Spacing();
		ImGui::Spacing();
		if (ImGui::RadioButton("Portugues", m_onboardingLanguage == 0))
		{
			m_onboardingLanguage = 0;
			SetPendingAction(-1100);
		}
		ImGui::SameLine(0.0f, 28.0f * scale);
		if (ImGui::RadioButton("English", m_onboardingLanguage == 1))
		{
			m_onboardingLanguage = 1;
			SetPendingAction(-1101);
		}
		ImGui::Dummy({ 0.0f, 170.0f * scale });
		if (fullButton(english ? "Next" : "Seguinte"))
			m_onboardingPage = 1;
	}
	else if (m_onboardingPage == 1)
	{
		ImGui::SetWindowFontScale(1.35f);
		ImGui::TextUnformatted(english ? "System files" : "Arquivos do sistema");
		ImGui::SetWindowFontScale(1.0f);
		ImGui::TextColored(theme.textSecondary, "%s",
			english ? "Add your own keys and firmware to prepare emulation."
				: "Adicione suas proprias keys e firmware para preparar a emulacao.");
		ImGui::Spacing();
		ImGui::TextColored(statusColor(m_onboardingKeysReady), "%s  -  %s",
			english ? "Select prod.keys" : "Selecione seus prod.keys",
			statusText(m_onboardingKeysReady));
		ImGui::BeginDisabled(operationActive);
		if (fullButton(english ? "Select keys folder" : "Selecionar pasta das keys"))
			SetPendingAction(-1003);
		ImGui::SetItemDefaultFocus();
		ImGui::EndDisabled();
		ImGui::Spacing();
		ImGui::TextColored(statusColor(m_onboardingFirmwareReady), "%s  -  %s",
			english ? "Install firmware" : "Instalar firmware",
			statusText(m_onboardingFirmwareReady));
		ImGui::BeginDisabled(operationActive);
		if (fullButton(english ? "Select firmware file" : "Selecionar arquivo de firmware"))
			SetPendingAction(-1001);
		ImGui::EndDisabled();
		if (m_operationState == 1)
		{
			const float progress = std::clamp(m_operationProgress, 0.0f, 1.0f);
			char percentage[16]{};
			std::snprintf(percentage, sizeof(percentage), "%d%%",
				static_cast<int>(std::round(progress * 100.0f)));
			ImGui::ProgressBar(progress, { -1.0f, 18.0f * scale }, percentage);
		}
		ImGui::Dummy({ 0.0f, 28.0f * scale });
		const bool systemReady = m_onboardingKeysReady && m_onboardingFirmwareReady;
		ImGui::BeginDisabled(!systemReady || operationActive);
		if (fullButton(english ? "Next" : "Seguinte"))
			m_onboardingPage = 2;
		ImGui::EndDisabled();
		if (!systemReady)
			ImGui::TextColored(theme.textSecondary, "%s",
				english ? "Keys and firmware are required to continue."
					: "Keys e firmware sao necessarios para continuar.");
	}
	else
	{
		ImGui::SetWindowFontScale(1.35f);
		ImGui::TextUnformatted(english ? "Library and appearance" : "Biblioteca e aparencia");
		ImGui::SetWindowFontScale(1.0f);
		ImGui::TextColored(statusColor(m_onboardingGamesReady), "%s  -  %s",
			english ? "Select games folder" : "Selecione pasta de jogos",
			statusText(m_onboardingGamesReady));
		ImGui::BeginDisabled(operationActive);
		if (fullButton(english ? "Select games folder" : "Selecionar pasta de jogos"))
			SetPendingAction(-1000);
		ImGui::SetItemDefaultFocus();
		ImGui::EndDisabled();
		ImGui::Spacing();
		ImGui::TextColored(theme.textSecondary, "%s",
			english ? "Select theme style" : "Selecione estilo de tema");
		if (ImGui::RadioButton(english ? "Night - black and red" : "Noturno - preto e vermelho",
			m_onboardingTheme == 0))
			m_onboardingTheme = 0;
		if (ImGui::RadioButton(english ? "Day - white and blue" : "Dia - branco e azul",
			m_onboardingTheme == 1))
			m_onboardingTheme = 1;
		ImGui::Dummy({ 0.0f, 80.0f * scale });
		ImGui::BeginDisabled(!m_onboardingGamesReady || operationActive);
		if (fullButton(english ? "Finish setup" : "Concluir configuracao"))
			SetPendingAction(m_onboardingTheme == 1 ? -1111 : -1110);
		ImGui::EndDisabled();
	}

	ImGui::EndChild();
	ImGui::PopStyleVar(3);
	ImGui::PopStyleColor(7);
	ImGui::End();
	ImGui::PopStyleColor();
	ImGui::PopStyleVar();
}

void ImGuiD3D11Shell::DrawHome()
{
	const ImVec2 content = ImGui::GetContentRegionAvail();
	const ImVec2 windowPos = ImGui::GetWindowPos();
	const ImVec2 windowSize = ImGui::GetWindowSize();
	ImDrawList* draw = ImGui::GetWindowDrawList();
	const int gameCount = static_cast<int>(m_titles.size());

	// Side controls reproduce the compact icon rails from the supplied
	// frontend. They are decorative only; controller focus remains explicit.
	const float railX = windowPos.x + 48.0f;
	for (int icon = 0; icon < 3; icon++)
	{
		const float y = windowPos.y + 112.0f + icon * 58.0f;
		const bool active = icon == 0;
		draw->AddCircleFilled(ImVec2(railX, y), 27.0f, UiColor(active ? ImVec4(0.20f, 0.02f, 0.16f, 1.0f) : Surface));
		draw->AddCircle(ImVec2(railX, y), 27.0f, UiColor(active ? Pink : ImVec4(0.33f, 0.28f, 0.38f, 1.0f)), 24, active ? 2.0f : 1.0f);
		const ImU32 iconColor = UiColor(active ? Text : Muted);
		if (icon == 0)
		{
			draw->AddLine(ImVec2(railX - 7.0f, y - 1.0f), ImVec2(railX, y - 8.0f), iconColor, 1.8f);
			draw->AddLine(ImVec2(railX, y - 8.0f), ImVec2(railX + 7.0f, y - 1.0f), iconColor, 1.8f);
			draw->AddRect(ImVec2(railX - 5.0f, y - 1.0f), ImVec2(railX + 5.0f, y + 7.0f), iconColor, 1.0f, 0, 1.8f);
		}
		else if (icon == 1)
		{
			for (int cellY = -1; cellY <= 1; cellY++)
				for (int cellX = -1; cellX <= 1; cellX++)
					draw->AddRectFilled(ImVec2(railX + cellX * 5.0f - 1.5f, y + cellY * 5.0f - 1.5f),
						ImVec2(railX + cellX * 5.0f + 1.5f, y + cellY * 5.0f + 1.5f), iconColor);
		}
		else
		{
			draw->AddCircle(ImVec2(railX, y), 6.0f, iconColor, 12, 1.7f);
			draw->AddCircleFilled(ImVec2(railX, y), 2.0f, iconColor);
		}
	}
	const float rightRailX = windowPos.x + windowSize.x - 48.0f;
	for (int icon = 0; icon < 3; icon++)
	{
		const float y = windowPos.y + 112.0f + icon * 58.0f;
		draw->AddCircle(ImVec2(rightRailX, y), 22.0f, UiColor(ImVec4(0.25f, 0.22f, 0.30f, 0.8f)), 24, 1.0f);
		const ImU32 iconColor = UiColor(Muted);
		if (icon == 0)
		{
			draw->AddRect(ImVec2(rightRailX - 10.0f, y - 5.0f), ImVec2(rightRailX + 10.0f, y + 4.0f), iconColor, 5.0f, 0, 1.7f);
			draw->AddCircle(ImVec2(rightRailX - 6.0f, y), 2.0f, iconColor, 10, 1.3f);
			draw->AddCircle(ImVec2(rightRailX + 6.0f, y), 2.0f, iconColor, 10, 1.3f);
		}
		else if (icon == 1)
		{
			draw->AddRect(ImVec2(rightRailX - 10.0f, y - 8.0f), ImVec2(rightRailX + 10.0f, y + 5.0f), iconColor, 2.0f, 0, 1.7f);
			draw->AddLine(ImVec2(rightRailX - 5.0f, y + 9.0f), ImVec2(rightRailX + 5.0f, y + 9.0f), iconColor, 1.7f);
			draw->AddLine(ImVec2(rightRailX, y + 5.0f), ImVec2(rightRailX, y + 9.0f), iconColor, 1.7f);
		}
		else
		{
			draw->AddCircle(ImVec2(rightRailX, y), 8.0f, iconColor, 16, 1.7f);
			draw->AddLine(ImVec2(rightRailX, y - 10.0f), ImVec2(rightRailX, y - 1.0f), iconColor, 2.0f);
		}
	}

	if (gameCount == 0)
	{
		ImGui::SetCursorPos(ImVec2(content.x * 0.25f, 190.0f));
		ImGui::TextColored(Cyan, "BIBLIOTECA VAZIA");
		ImGui::TextDisabled("Abra Configuracoes com o botao Menu para adicionar jogos.");
		return;
	}

	const int selected = std::clamp(m_selectedIndex, 0, gameCount - 1);
	draw->AddCircleFilled(ImVec2(windowPos.x + content.x * 0.5f, windowPos.y + 286.0f), 176.0f, UiColor(ImVec4(0.32f, 0.02f, 0.25f, 0.12f)));

	const float carouselCenter = content.x * 0.5f;
	for (int offset = -2; offset <= 2; offset++)
	{
		const int index = (selected + offset + gameCount) % gameCount;
		const bool isSelected = offset == 0;
		const int distance = std::abs(offset);
		const float cardWidth = isSelected ? 166.0f : (distance == 1 ? 146.0f : 122.0f);
		const float cardHeight = isSelected ? 236.0f : (distance == 1 ? 208.0f : 178.0f);
		const float cardY = 145.0f + (236.0f - cardHeight);
		const float cardCenter = carouselCenter + static_cast<float>(offset) * 150.0f;
		ImGui::SetCursorPos(ImVec2(cardCenter - cardWidth * 0.5f, cardY));
		const float cardOpacity = isSelected ? 1.0f : (distance == 1 ? 0.78f : 0.38f);
		const ImVec4 cardColor = isSelected ? ImVec4(0.16f, 0.02f, 0.13f, 1.0f) : ImVec4(Surface.x, Surface.y, Surface.z, cardOpacity);
		ImGui::PushID(index);
		ImGui::PushStyleColor(ImGuiCol_ChildBg, cardColor);
		ImGui::PushStyleVar(ImGuiStyleVar_ChildRounding, 16.0f);
		ImGui::BeginChild("cover-card", ImVec2(cardWidth, cardHeight), true, ImGuiWindowFlags_NoScrollbar);
		const ImVec2 cardPos = ImGui::GetWindowPos();
		ImDrawList* cardDraw = ImGui::GetWindowDrawList();
		if (isSelected)
		{
			cardDraw->AddRect(ImVec2(cardPos.x - 1.0f, cardPos.y - 1.0f),
				ImVec2(cardPos.x + cardWidth + 1.0f, cardPos.y + cardHeight + 1.0f), UiColor(Pink), 16.0f, 0, 2.5f);
		}
		cardDraw->AddText(ImVec2(cardPos.x + 10.0f, cardPos.y + 8.0f), UiColor(isSelected ? Text : Muted), "NINTENDO SWITCH");
		ImGui::Dummy(ImVec2(1.0f, 18.0f));
		const auto cover = m_d12 ? reinterpret_cast<ImTextureID>(m_d12Covers[index]) :
			reinterpret_cast<ImTextureID>(m_coverTextures[index].Get());
		const bool hasCover = cover != nullptr;
		if (hasCover)
		{
			ImGui::Image(cover,
				ImVec2(cardWidth - 18.0f, cardHeight - 84.0f));
		}
		else
		{
			const ImVec2 placeholder = ImGui::GetCursorScreenPos();
			cardDraw->AddRectFilled(placeholder, ImVec2(placeholder.x + cardWidth - 18.0f, placeholder.y + cardHeight - 84.0f), UiColor(CardAccent(index)));
			ImGui::Dummy(ImVec2(cardWidth - 18.0f, cardHeight - 84.0f));
		}
		const std::string title = Utf8(DisplayTitle(m_titles[index]));
		ImGui::TextWrapped("%s", title.c_str());
		if (isSelected)
		{
			ImGui::TextColored(Cyan, "A  JOGAR");
		}
		ImGui::EndChild();
		ImGui::PopStyleVar();
		ImGui::PopStyleColor();
		ImGui::PopID();
	}

	const std::string selectedTitle = Utf8(DisplayTitle(m_titles[selected]));
	const float titleWidth = ImGui::CalcTextSize(selectedTitle.c_str()).x;
	ImGui::SetCursorPos(ImVec2(std::max(0.0f, (content.x - titleWidth) * 0.5f), 405.0f));
	ImGui::TextColored(Text, "%s", selectedTitle.c_str());
	const char* meta = "Pronto para jogar  |  Handheld  |  Shader cache ativo";
	const float metaWidth = ImGui::CalcTextSize(meta).x;
	ImGui::SetCursorPos(ImVec2(std::max(0.0f, (content.x - metaWidth) * 0.5f), 434.0f));
	ImGui::TextDisabled("%s", meta);

	const float footerY = std::max(350.0f, content.y - 48.0f);
	const char* footerKeys[] = { "< / >", "A", "A / D", "Menu" };
	const char* footerLabels[] = { "Carrossel", "Jogar", "Navegar", "Configuracoes" };
	float footerWidth = 0.0f;
	for (int item = 0; item < 4; item++)
	{
		footerWidth += ImGui::CalcTextSize(footerKeys[item]).x + 16.0f
			+ ImGui::CalcTextSize(footerLabels[item]).x + (item == 3 ? 0.0f : 24.0f);
	}
	float footerX = std::max(16.0f, (content.x - footerWidth) * 0.5f);
	for (int item = 0; item < 4; item++)
	{
		const float keyWidth = ImGui::CalcTextSize(footerKeys[item]).x + 16.0f;
		const ImVec2 keyMin(windowPos.x + footerX, windowPos.y + footerY - 3.0f);
		const ImVec2 keyMax(keyMin.x + keyWidth, keyMin.y + 22.0f);
		draw->AddRectFilled(keyMin, keyMax, UiColor(ImVec4(0.10f, 0.07f, 0.13f, 1.0f)), 6.0f);
		draw->AddRect(keyMin, keyMax, UiColor(ImVec4(0.38f, 0.30f, 0.43f, 1.0f)), 6.0f, 0, 1.0f);
		draw->AddText(ImVec2(keyMin.x + 8.0f, keyMin.y + 4.0f), UiColor(Text), footerKeys[item]);
		footerX += keyWidth + 6.0f;
		draw->AddText(ImVec2(windowPos.x + footerX, windowPos.y + footerY + 2.0f), UiColor(Muted), footerLabels[item]);
		footerX += ImGui::CalcTextSize(footerLabels[item]).x + (item == 3 ? 0.0f : 18.0f);
	}
}

void ImGuiD3D11Shell::DrawSettings()
{
	const ImVec2 content = ImGui::GetContentRegionAvail();
	const float sideWidth = 184.0f;
	const float mainWidth = std::max(360.0f, content.x - sideWidth - 18.0f);
	ImGui::PushStyleColor(ImGuiCol_ChildBg, Surface);
	ImGui::BeginChild("settings-navigation", ImVec2(sideWidth, content.y), true);
	ImGui::TextColored(Cyan, "MENU");
	ImGui::Spacing();
	ImGui::Selectable("Biblioteca", false, ImGuiSelectableFlags_DontClosePopups, ImVec2(sideWidth - 28.0f, 42.0f));
	ImGui::PushStyleColor(ImGuiCol_Header, SurfaceStrong);
	ImGui::Selectable("Configuracoes", true, ImGuiSelectableFlags_DontClosePopups, ImVec2(sideWidth - 28.0f, 42.0f));
	ImGui::PopStyleColor();
	ImGui::Spacing();
	ImGui::TextColored(Pink, "CONTROLE");
	ImGui::TextDisabled("D-pad / analogico");
	ImGui::TextDisabled("A  confirmar");
	ImGui::TextDisabled("B  voltar");
	ImGui::EndChild();
	ImGui::PopStyleColor();
	ImGui::SameLine(0.0f, 18.0f);

	ImGui::BeginChild("settings-content", ImVec2(mainWidth, content.y), false);
	ImGui::TextColored(Text, "CONFIGURACOES");
	ImGui::SameLine();
	ImGui::TextDisabled("  controle e armazenamento");
	ImGui::Spacing();
	ImGui::PushStyleColor(ImGuiCol_ChildBg, SurfaceStrong);
	ImGui::BeginChild("requirements", ImVec2(0, 112), true);
	ImGui::TextColored(Cyan, "STATUS DO COREHOST");
	ImGui::Text("Keys");
	ImGui::SameLine(96.0f);
	ImGui::TextColored(Pink, "%ls", m_keysStatus.c_str());
	ImGui::Text("Firmware");
	ImGui::SameLine(96.0f);
	ImGui::TextColored(Cyan, "%ls", m_firmwareStatus.c_str());
	ImGui::EndChild();
	ImGui::PopStyleColor();
	ImGui::Spacing();
	ImGui::TextColored(Muted, "ARMAZENAMENTO");
	const char* settings[] = { "Selecionar pasta de jogos", "Selecionar arquivo de firmware", "Selecionar pasta de firmware" };
	for (int index = 0; index < 3; index++)
	{
		const bool selected = index == m_settingsIndex;
		ImGui::PushStyleColor(ImGuiCol_Header, selected ? SurfaceStrong : Surface);
		ImGui::PushStyleColor(ImGuiCol_HeaderHovered, ImVec4(0.20f, 0.08f, 0.20f, 1.0f));
		ImGui::Selectable(settings[index], selected, ImGuiSelectableFlags_DontClosePopups, ImVec2(0, 48));
		ImGui::PopStyleColor(2);
		if (selected)
		{
			ImGui::SameLine(mainWidth - 112.0f);
			ImGui::TextColored(Cyan, "A  ABRIR");
		}
	}
	ImGui::Spacing();
	ImGui::PushStyleColor(ImGuiCol_ChildBg, Surface);
	ImGui::BeginChild("runtime", ImVec2(0, 112), true);
	ImGui::TextColored(Pink, "PERFIL DE EXECUCAO");
	ImGui::Text("Renderer              Direct3D 11 nativo");
	ImGui::Text("Memoria               modo portatil / baixo consumo");
	ImGui::Text("Apresentacao          SwapChain nativo sem overlay XAML");
	ImGui::EndChild();
	ImGui::PopStyleColor();
	ImGui::TextDisabled("D-pad / analogico navega    A confirma    B retorna a biblioteca");
	ImGui::EndChild();
}

HRESULT ImGuiD3D11Shell::CreateDeviceObjects()
{
	if (m_d12)
	{
		HRESULT hr = m_d12->Initialize(VertexShader, PixelShader);
		if (FAILED(hr)) return hr;
		unsigned char* pixels = nullptr; int width = 0, height = 0;
		ImGui::GetIO().Fonts->GetTexDataAsRGBA32(&pixels, &width, &height);
		hr = m_d12->CreateTexture(pixels, width, height, width * 4, &m_d12Font);
		if (FAILED(hr)) return hr;
		ImGui::GetIO().Fonts->TexID = reinterpret_cast<ImTextureID>(m_d12Font);
		ImGui::GetIO().BackendFlags |= ImGuiBackendFlags_RendererHasVtxOffset;
		ImGui::GetIO().Fonts->ClearTexData();
		return S_OK;
	}
	ComPtr<ID3DBlob> vertexBlob;
	ComPtr<ID3DBlob> pixelBlob;
	ComPtr<ID3DBlob> errors;
	HRESULT result = D3DCompile(VertexShader, std::strlen(VertexShader), "RyuboxImGuiD3D11.hlsl",
		nullptr, nullptr, "main", "vs_4_0", D3DCOMPILE_ENABLE_STRICTNESS, 0, &vertexBlob, &errors);
	if (FAILED(result))
	{
		return result;
	}
	result = D3DCompile(PixelShader, std::strlen(PixelShader), "RyuboxImGuiD3D11.hlsl",
		nullptr, nullptr, "main", "ps_4_0", D3DCOMPILE_ENABLE_STRICTNESS, 0, &pixelBlob, &errors);
	if (FAILED(result))
	{
		return result;
	}
	result = m_device->CreateVertexShader(vertexBlob->GetBufferPointer(), vertexBlob->GetBufferSize(), nullptr, &m_vertexShader);
	if (FAILED(result)) return result;
	result = m_device->CreatePixelShader(pixelBlob->GetBufferPointer(), pixelBlob->GetBufferSize(), nullptr, &m_pixelShader);
	if (FAILED(result)) return result;

	const D3D11_INPUT_ELEMENT_DESC elements[] =
	{
		{ "POSITION", 0, DXGI_FORMAT_R32G32_FLOAT, 0, static_cast<UINT>(offsetof(ImDrawVert, pos)), D3D11_INPUT_PER_VERTEX_DATA, 0 },
		{ "COLOR", 0, DXGI_FORMAT_R8G8B8A8_UNORM, 0, static_cast<UINT>(offsetof(ImDrawVert, col)), D3D11_INPUT_PER_VERTEX_DATA, 0 },
		{ "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, static_cast<UINT>(offsetof(ImDrawVert, uv)), D3D11_INPUT_PER_VERTEX_DATA, 0 },
	};
	result = m_device->CreateInputLayout(elements, 3, vertexBlob->GetBufferPointer(), vertexBlob->GetBufferSize(), &m_inputLayout);
	if (FAILED(result)) return result;

	D3D11_BUFFER_DESC constantDescription{};
	constantDescription.ByteWidth = 64;
	constantDescription.Usage = D3D11_USAGE_DYNAMIC;
	constantDescription.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
	constantDescription.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
	result = m_device->CreateBuffer(&constantDescription, nullptr, &m_constantBuffer);
	if (FAILED(result)) return result;

	D3D11_BLEND_DESC blendDescription{};
	blendDescription.RenderTarget[0].BlendEnable = TRUE;
	blendDescription.RenderTarget[0].SrcBlend = D3D11_BLEND_SRC_ALPHA;
	blendDescription.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
	blendDescription.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
	blendDescription.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
	blendDescription.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
	blendDescription.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
	blendDescription.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
	result = m_device->CreateBlendState(&blendDescription, &m_blendState);
	if (FAILED(result)) return result;

	D3D11_RASTERIZER_DESC rasterDescription{};
	rasterDescription.FillMode = D3D11_FILL_SOLID;
	rasterDescription.CullMode = D3D11_CULL_NONE;
	rasterDescription.ScissorEnable = TRUE;
	rasterDescription.DepthClipEnable = TRUE;
	result = m_device->CreateRasterizerState(&rasterDescription, &m_rasterizerState);
	if (FAILED(result)) return result;

	D3D11_SAMPLER_DESC samplerDescription{};
	samplerDescription.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
	samplerDescription.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
	samplerDescription.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
	samplerDescription.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
	samplerDescription.ComparisonFunc = D3D11_COMPARISON_ALWAYS;
	samplerDescription.MinLOD = 0;
	samplerDescription.MaxLOD = D3D11_FLOAT32_MAX;
	result = m_device->CreateSamplerState(&samplerDescription, &m_samplerState);
	if (FAILED(result)) return result;

	unsigned char* pixels = nullptr;
	int fontWidth = 0;
	int fontHeight = 0;
	ImGui::GetIO().Fonts->GetTexDataAsRGBA32(&pixels, &fontWidth, &fontHeight);
	D3D11_TEXTURE2D_DESC fontDescription{};
	fontDescription.Width = static_cast<UINT>(fontWidth);
	fontDescription.Height = static_cast<UINT>(fontHeight);
	fontDescription.MipLevels = 1;
	fontDescription.ArraySize = 1;
	fontDescription.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
	fontDescription.SampleDesc.Count = 1;
	fontDescription.Usage = D3D11_USAGE_DEFAULT;
	fontDescription.BindFlags = D3D11_BIND_SHADER_RESOURCE;
	D3D11_SUBRESOURCE_DATA fontData{};
	fontData.pSysMem = pixels;
	fontData.SysMemPitch = static_cast<UINT>(fontWidth * 4);
	ComPtr<ID3D11Texture2D> fontTexture;
	result = m_device->CreateTexture2D(&fontDescription, &fontData, &fontTexture);
	if (FAILED(result)) return result;
	result = m_device->CreateShaderResourceView(fontTexture.Get(), nullptr, &m_fontTexture);
	if (FAILED(result)) return result;
	ImGui::GetIO().Fonts->TexID = static_cast<ImTextureID>(m_fontTexture.Get());
	ImGui::GetIO().Fonts->ClearTexData();
	return S_OK;
}

void ImGuiD3D11Shell::DestroyDeviceObjects() noexcept
{
	m_d12.reset();
	m_d12Covers.clear();
	m_d12Font = 0;
	if (ImGui::GetCurrentContext() != nullptr)
	{
		ImGui::GetIO().Fonts->TexID = 0;
	}
	m_fontTexture.Reset();
	m_coverTextures.clear();
	m_samplerState.Reset();
	m_rasterizerState.Reset();
	m_blendState.Reset();
	m_constantBuffer.Reset();
	m_indexBuffer.Reset();
	m_vertexBuffer.Reset();
	m_inputLayout.Reset();
	m_pixelShader.Reset();
	m_vertexShader.Reset();
}

HRESULT ImGuiD3D11Shell::RenderDrawData()
{
	ImDrawData* drawData = ImGui::GetDrawData();
	if (m_d12) return m_d12->Render(drawData, m_width, m_height);
	if (drawData == nullptr || drawData->TotalVtxCount == 0)
	{
		return S_OK;
	}

	D3D11_BUFFER_DESC vertexDescription{};
	vertexDescription.Usage = D3D11_USAGE_DYNAMIC;
	vertexDescription.BindFlags = D3D11_BIND_VERTEX_BUFFER;
	vertexDescription.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
	vertexDescription.ByteWidth = static_cast<UINT>(drawData->TotalVtxCount * sizeof(ImDrawVert));
	D3D11_BUFFER_DESC indexDescription{};
	indexDescription.Usage = D3D11_USAGE_DYNAMIC;
	indexDescription.BindFlags = D3D11_BIND_INDEX_BUFFER;
	indexDescription.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
	indexDescription.ByteWidth = static_cast<UINT>(drawData->TotalIdxCount * sizeof(ImDrawIdx));
	HRESULT result = S_OK;
	D3D11_BUFFER_DESC existing{};
	if (m_vertexBuffer) m_vertexBuffer->GetDesc(&existing);
	if (!m_vertexBuffer || existing.ByteWidth < vertexDescription.ByteWidth)
	{
		vertexDescription.ByteWidth = std::max(vertexDescription.ByteWidth, 65536u);
		result = m_device->CreateBuffer(&vertexDescription, nullptr, &m_vertexBuffer);
		if (FAILED(result)) return result;
	}
	existing = {};
	if (m_indexBuffer) m_indexBuffer->GetDesc(&existing);
	if (!m_indexBuffer || existing.ByteWidth < indexDescription.ByteWidth)
	{
		indexDescription.ByteWidth = std::max(indexDescription.ByteWidth, 16384u);
		result = m_device->CreateBuffer(&indexDescription, nullptr, &m_indexBuffer);
		if (FAILED(result)) return result;
	}

	D3D11_MAPPED_SUBRESOURCE mapped{};
	result = m_context->Map(m_vertexBuffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
	if (FAILED(result)) return result;
	ImDrawVert* vertexDestination = static_cast<ImDrawVert*>(mapped.pData);
	for (int listIndex = 0; listIndex < drawData->CmdListsCount; listIndex++)
	{
		const ImDrawList* list = drawData->CmdLists[listIndex];
		std::memcpy(vertexDestination, list->VtxBuffer.Data, list->VtxBuffer.Size * sizeof(ImDrawVert));
		vertexDestination += list->VtxBuffer.Size;
	}
	m_context->Unmap(m_vertexBuffer.Get(), 0);
	result = m_context->Map(m_indexBuffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
	if (FAILED(result)) return result;
	ImDrawIdx* indexDestination = static_cast<ImDrawIdx*>(mapped.pData);
	for (int listIndex = 0; listIndex < drawData->CmdListsCount; listIndex++)
	{
		const ImDrawList* list = drawData->CmdLists[listIndex];
		std::memcpy(indexDestination, list->IdxBuffer.Data, list->IdxBuffer.Size * sizeof(ImDrawIdx));
		indexDestination += list->IdxBuffer.Size;
	}
	m_context->Unmap(m_indexBuffer.Get(), 0);

	const float left = drawData->DisplayPos.x;
	const float right = drawData->DisplayPos.x + drawData->DisplaySize.x;
	const float top = drawData->DisplayPos.y;
	const float bottom = drawData->DisplayPos.y + drawData->DisplaySize.y;
	const float matrix[4][4] =
	{
		{ 2.0f / (right - left), 0.0f, 0.0f, 0.0f },
		{ 0.0f, 2.0f / (top - bottom), 0.0f, 0.0f },
		{ 0.0f, 0.0f, 0.5f, 0.0f },
		{ (right + left) / (left - right), (top + bottom) / (bottom - top), 0.5f, 1.0f },
	};
	result = m_context->Map(m_constantBuffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
	if (FAILED(result)) return result;
	std::memcpy(mapped.pData, matrix, sizeof(matrix));
	m_context->Unmap(m_constantBuffer.Get(), 0);

	const UINT stride = sizeof(ImDrawVert);
	const UINT offset = 0;
	m_context->IASetInputLayout(m_inputLayout.Get());
	m_context->IASetVertexBuffers(0, 1, m_vertexBuffer.GetAddressOf(), &stride, &offset);
	m_context->IASetIndexBuffer(m_indexBuffer.Get(), sizeof(ImDrawIdx) == 2 ? DXGI_FORMAT_R16_UINT : DXGI_FORMAT_R32_UINT, 0);
	m_context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
	m_context->VSSetShader(m_vertexShader.Get(), nullptr, 0);
	m_context->VSSetConstantBuffers(0, 1, m_constantBuffer.GetAddressOf());
	m_context->PSSetShader(m_pixelShader.Get(), nullptr, 0);
	m_context->PSSetSamplers(0, 1, m_samplerState.GetAddressOf());
	m_context->OMSetBlendState(m_blendState.Get(), nullptr, 0xffffffff);
	m_context->RSSetState(m_rasterizerState.Get());
	D3D11_VIEWPORT viewport{};
	viewport.TopLeftX = 0.0f;
	viewport.TopLeftY = 0.0f;
	viewport.Width = static_cast<float>(m_width);
	viewport.Height = static_cast<float>(m_height);
	viewport.MinDepth = 0.0f;
	viewport.MaxDepth = 1.0f;
	m_context->RSSetViewports(1, &viewport);

	int globalIndexOffset = 0;
	int globalVertexOffset = 0;
	for (int listIndex = 0; listIndex < drawData->CmdListsCount; listIndex++)
	{
		const ImDrawList* list = drawData->CmdLists[listIndex];
		for (const ImDrawCmd& command : list->CmdBuffer)
		{
			if (command.UserCallback != nullptr)
			{
				if (command.UserCallback == ImDrawCallback_ResetRenderState)
				{
					m_context->IASetInputLayout(m_inputLayout.Get());
				}
				continue;
			}
			D3D11_RECT scissor{};
			scissor.left = static_cast<LONG>(std::max(0.0f, command.ClipRect.x - drawData->DisplayPos.x));
			scissor.top = static_cast<LONG>(std::max(0.0f, command.ClipRect.y - drawData->DisplayPos.y));
			scissor.right = static_cast<LONG>(std::min(static_cast<float>(m_width), command.ClipRect.z - drawData->DisplayPos.x));
			scissor.bottom = static_cast<LONG>(std::min(static_cast<float>(m_height), command.ClipRect.w - drawData->DisplayPos.y));
			if (scissor.right <= scissor.left || scissor.bottom <= scissor.top)
			{
				continue;
			}
			m_context->RSSetScissorRects(1, &scissor);
			ID3D11ShaderResourceView* texture = reinterpret_cast<ID3D11ShaderResourceView*>(command.GetTexID());
			if (texture == nullptr)
			{
				texture = m_fontTexture.Get();
			}
			m_context->PSSetShaderResources(0, 1, &texture);
			m_context->DrawIndexed(command.ElemCount, command.IdxOffset + globalIndexOffset,
				command.VtxOffset + globalVertexOffset);
		}
		globalIndexOffset += list->IdxBuffer.Size;
		globalVertexOffset += list->VtxBuffer.Size;
	}
	ID3D11ShaderResourceView* nullTexture = nullptr;
	m_context->PSSetShaderResources(0, 1, &nullTexture);
	return S_OK;
}

HRESULT ImGuiD3D11Shell::Render()
{
	if (!m_initialized || (!m_d12 && (!m_context || !m_renderTarget)))
	{
		return E_UNEXPECTED;
	}

	UpdateInputState();
	ImGuiIO& io = ImGui::GetIO();
	io.DeltaTime = 1.0f / 60.0f;
	io.DisplaySize = ImVec2(static_cast<float>(m_width), static_cast<float>(m_height));
	ImGui::NewFrame();
	BuildFrame();
	ImGui::Render();

	if (!m_d12) m_context->OMSetRenderTargets(1, m_renderTarget.GetAddressOf(), nullptr);
	const HRESULT result = RenderDrawData();
	return result;
}

HRESULT ImGuiD3D11Shell::RenderGameOverlay(float fps)
{
	if (!m_initialized || (!m_d12 && (!m_context || !m_renderTarget))) return E_UNEXPECTED;
	if (!m_d12 && !m_overlayContext)
	{
		HRESULT hr = m_device->CreateDeferredContext(0, &m_overlayContext);
		if (FAILED(hr)) return hr;
	}
	// Do not call UpdateInputState, BuildFrame or the frontend during a game.
	// Only a draw-list is generated: no focus, widgets, navigation or sound.
	ImGuiIO& io = ImGui::GetIO();
	io.DeltaTime = 1.0f / 60.0f;
	io.DisplaySize = ImVec2(static_cast<float>(m_width), static_cast<float>(m_height));
	ImGui::NewFrame();
	char label[48]{};
	std::snprintf(label, sizeof(label), "FPS  %.1f", fps);
	ImDrawList* draw = ImGui::GetForegroundDrawList();
	const float scale = std::max(1.0f, static_cast<float>(m_height) / 720.0f);
	const ImVec2 start(18.0f * scale, 18.0f * scale);
	const ImVec2 end(start.x + 150.0f * scale, start.y + 42.0f * scale);
	draw->AddRectFilled(start, end, IM_COL32(8, 8, 12, 235), 6.0f * scale);
	draw->AddText(ImGui::GetFont(), 22.0f * scale,
		ImVec2(start.x + 10.0f * scale, start.y + 9.0f * scale), IM_COL32(255, 255, 255, 255), label);
	ImGui::Render();
	if (m_d12) return m_d12->Render(ImGui::GetDrawData(), m_width, m_height);
	// Preserve ALL guest pipeline state using a deferred command list. A simple
	// OM/viewport restore misses shaders, UAVs, blend/depth state and bindings.
	ComPtrContext immediate = m_context;
	m_context = m_overlayContext;
	m_context->OMSetRenderTargets(1, m_renderTarget.GetAddressOf(), nullptr);
	HRESULT hr = RenderDrawData();
	m_context = immediate;
	Microsoft::WRL::ComPtr<ID3D11CommandList> commands;
	HRESULT finish = m_overlayContext->FinishCommandList(FALSE, &commands);
	if (FAILED(hr)) return hr;
	if (FAILED(finish)) return finish;
	immediate->ExecuteCommandList(commands.Get(), TRUE);
	return S_OK;
}
}
