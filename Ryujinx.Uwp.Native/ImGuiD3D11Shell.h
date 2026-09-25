#pragma once

#include <d3d11.h>
#include <windows.h>
#include <wrl.h>

#include <cstdint>
#include <cfloat>
#include <string>
#include <vector>
#include "ImGuiD3D12Renderer.h"
#include <memory>

#include "../UWP-ImGuiFrontend/include/UwpImGuiFrontend/RyuboxSettings.h"
#include "../UWP-ImGuiFrontend/include/UwpImGuiFrontend/Settings.h"
#include "../UWP-ImGuiFrontend/include/UwpImGuiFrontend/Widgets.h"

struct ImFont;

namespace Ryujinx::Uwp::Native
{
struct ImGuiShellInput final
{
	std::uint32_t buttons = 0;
	float leftX = 0.0f;
	float leftY = 0.0f;
	float rightX = 0.0f;
	float rightY = 0.0f;
	float leftTrigger = 0.0f;
	float rightTrigger = 0.0f;
};

struct ImGuiShellPointer final
{
	float x = -FLT_MAX;
	float y = -FLT_MAX;
	float wheel = 0.0f;
	bool leftDown = false;
	bool rightDown = false;
};

// Native, controller-first launcher surface. It owns only ImGui resources;
// the D3D11 device, context, swap chain and composition surface remain owned
// by D3D11GraphicsHost. This lets the guest take exclusive ownership without
// a second window or a XAML overlay being left above the game.
class ImGuiD3D11Shell final
{
public:
	// Shared UI/controller model; D3D12 mode never creates a D3D11 device.
	HRESULT InitializeD3D12(void* context, std::uint32_t width, std::uint32_t height);
	HRESULT ResizeD3D12(std::uint32_t width, std::uint32_t height);
	HRESULT Initialize(ID3D11Device* device, ID3D11DeviceContext* context,
		ID3D11RenderTargetView* renderTarget, std::uint32_t width,
		std::uint32_t height);
	void Shutdown() noexcept;
	void ClearRenderTarget() noexcept { m_renderTarget.Reset(); }
	HRESULT Resize(ID3D11RenderTargetView* renderTarget, std::uint32_t width,
		std::uint32_t height);
	void SetInput(const ImGuiShellInput& input) noexcept;
	void SetPointer(const ImGuiShellPointer& pointer) noexcept;
	void SetCatalogue(const wchar_t* newlineSeparatedTitles);
	HRESULT SetCover(int index, const void* rgbaPixels, std::uint32_t width,
		std::uint32_t height, std::uint32_t rowPitch);
	void SetStatus(const wchar_t* keysStatus, const wchar_t* firmwareStatus) noexcept;
	void SetOperation(const wchar_t* message, float progress, int state) noexcept;
	void SetOnboardingState(bool completed, int language, int theme,
		bool keysReady, bool firmwareReady, bool gamesReady) noexcept;
	int ConsumeAction() noexcept;
	void SetPendingAction(int action) noexcept;
	HRESULT Render();
	HRESULT RenderGameOverlay(float fps);
	[[nodiscard]] bool IsInitialized() const noexcept { return m_initialized; }

private:
	HRESULT InitializeUi();
	std::unique_ptr<ImGuiD3D12Renderer> m_d12;
	std::vector<std::uint64_t> m_d12Covers;
	std::uint64_t m_d12Font = 0;
	ImFont* m_fontNormal = nullptr;
	ImFont* m_fontMedium = nullptr;
	ImFont* m_fontTitle = nullptr;
	ImFont* m_fontSmall = nullptr;
	HRESULT CreateDeviceObjects();
	void DestroyDeviceObjects() noexcept;
	void BuildFrame();
	void DrawHome();
	void DrawSettings();
	void DrawOnboarding();
	void UpdateInputState();
	HRESULT RenderDrawData();

	using ComPtrDevice = Microsoft::WRL::ComPtr<ID3D11Device>;
	using ComPtrContext = Microsoft::WRL::ComPtr<ID3D11DeviceContext>;
	using ComPtrRTV = Microsoft::WRL::ComPtr<ID3D11RenderTargetView>;

	ComPtrDevice m_device;
	ComPtrContext m_context;
	ComPtrContext m_overlayContext;
	ComPtrRTV m_renderTarget;
	Microsoft::WRL::ComPtr<ID3D11VertexShader> m_vertexShader;
	Microsoft::WRL::ComPtr<ID3D11PixelShader> m_pixelShader;
	Microsoft::WRL::ComPtr<ID3D11InputLayout> m_inputLayout;
	Microsoft::WRL::ComPtr<ID3D11Buffer> m_vertexBuffer;
	Microsoft::WRL::ComPtr<ID3D11Buffer> m_indexBuffer;
	Microsoft::WRL::ComPtr<ID3D11Buffer> m_constantBuffer;
	Microsoft::WRL::ComPtr<ID3D11BlendState> m_blendState;
	Microsoft::WRL::ComPtr<ID3D11RasterizerState> m_rasterizerState;
	Microsoft::WRL::ComPtr<ID3D11SamplerState> m_samplerState;
	Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> m_fontTexture;
	std::vector<Microsoft::WRL::ComPtr<ID3D11ShaderResourceView>> m_coverTextures;

	std::vector<std::wstring> m_titles;
	std::wstring m_keysStatus = L"nao configuradas";
	std::wstring m_firmwareStatus = L"nao instalado";
	std::wstring m_operationMessage;
	float m_operationProgress = 0.0f;
	int m_operationState = 0;
	ImGuiShellInput m_input{};
	ImGuiShellPointer m_pointer{};
	UwpImGuiFrontend::WidgetInputState m_previousWidgetInput{};
	std::uint32_t m_previousButtons = 0;
	float m_previousLeftX = 0.0f;
	float m_previousLeftY = 0.0f;
	bool m_suppressSettingsAcceptUntilRelease = false;
	// Managed startup publishes the persisted value before restoring brokered
	// folders.  Default to the safe gate so the dashboard cannot appear first
	// while a disconnected/slow external drive delays that publication.
	bool m_onboardingCompleted = false;
	bool m_onboardingKeysReady = false;
	bool m_onboardingFirmwareReady = false;
	bool m_onboardingGamesReady = false;
	bool m_onboardingFirmwarePopup = false;
	int m_onboardingLanguage = 0;
	int m_onboardingTheme = 0;
	int m_onboardingPage = 0;
	int m_selectedIndex = 0;
	int m_settingsIndex = 0;
	int m_page = 0;
	int m_pendingAction = -1;
	bool m_actionReleaseRequired = false;
	std::uint32_t m_width = 1;
	std::uint32_t m_height = 1;
	bool m_initialized = false;
};
}
