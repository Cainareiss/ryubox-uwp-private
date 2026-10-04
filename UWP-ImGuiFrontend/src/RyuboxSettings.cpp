#include "UwpImGuiFrontend/RyuboxSettings.h"

#include "UwpImGuiFrontend/Frontend.h"
#include "UwpImGuiFrontend/ShellRenderer.h"
#include "UwpImGuiFrontend/Widgets.h"

#include <imgui.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <string>

namespace UwpImGuiFrontend
{
namespace
{
ImVec4 Alpha(ImVec4 color, float alpha)
{
	color.w *= alpha;
	return color;
}

void NotifyChanged(const RyuboxSettingsCallbacks& callbacks, std::string_view id)
{
	if (callbacks.changed)
		callbacks.changed(id);
}

bool SettingAvailable(std::string_view id, bool declaredSupported,
	const RyuboxSettingsCallbacks& callbacks)
{
	if (!declaredSupported)
		return false;
	if (!callbacks.strictRuntimeBinding)
		return true;
	return callbacks.isBound && callbacks.isBound(id);
}

void UnsupportedHint(bool supported)
{
	if (!supported)
	{
		ImGui::SameLine();
		ImGui::TextDisabled("Unavailable / not wired to Ryubox core");
	}
}

bool DrawChoice(const char* id, RyuboxChoiceSetting& setting,
	const RyuboxSettingsCallbacks& callbacks)
{
	if (setting.choices.empty())
	{
		ImGui::TextDisabled("%s: sem opcoes disponiveis", setting.label.c_str());
		return false;
	}
	if (setting.selected >= setting.choices.size())
		setting.selected = 0;
	const bool available = SettingAvailable(id, setting.supported, callbacks);
	if (!available)
		ImGui::BeginDisabled();
	const bool changed = RyuboxCombo(setting.label.c_str(), &setting.selected,
		setting.choices);
	if (!available)
		ImGui::EndDisabled();
	UnsupportedHint(available);
	if (changed)
		NotifyChanged(callbacks, id);
	return changed;
}

bool DrawToggle(const char* id, RyuboxToggleSetting& setting,
	const RyuboxSettingsCallbacks& callbacks)
{
	const bool available = SettingAvailable(id, setting.supported, callbacks);
	if (!available)
		ImGui::BeginDisabled();
	const bool changed = RyuboxToggle(setting.label.c_str(), &setting.value);
	if (!available)
		ImGui::EndDisabled();
	UnsupportedHint(available);
	if (changed)
		NotifyChanged(callbacks, id);
	return changed;
}

bool DrawFloat(const char* id, RyuboxFloatSetting& setting,
	const RyuboxSettingsCallbacks& callbacks)
{
	const bool available = SettingAvailable(id, setting.supported, callbacks);
	if (!available)
		ImGui::BeginDisabled();
	const bool changed = RyuboxSlider(setting.label.c_str(), &setting.value,
		setting.minimum, setting.maximum, setting.format);
	if (!available)
		ImGui::EndDisabled();
	UnsupportedHint(available);
	if (changed)
		NotifyChanged(callbacks, id);
	return changed;
}

bool DrawInt(const char* id, RyuboxIntSetting& setting,
	const RyuboxSettingsCallbacks& callbacks)
{
	const bool available = SettingAvailable(id, setting.supported, callbacks);
	if (!available)
		ImGui::BeginDisabled();
	const bool changed = ShellSliderInt(setting.label.c_str(), &setting.value,
		setting.minimum, setting.maximum, setting.format);
	if (!available)
		ImGui::EndDisabled();
	UnsupportedHint(available);
	if (changed)
		NotifyChanged(callbacks, id);
	return changed;
}

void DrawStatusPill(const char* label, bool good)
{
	const ShellTheme& theme = CurrentWidgetTheme();
	ImGui::PushStyleColor(ImGuiCol_Button,
		good ? Alpha(theme.pageIndicatorActive, 0.18f) : Alpha(theme.panelBorder, 0.34f));
	ImGui::PushStyleColor(ImGuiCol_ButtonHovered,
		good ? Alpha(theme.pageIndicatorActive, 0.24f) : Alpha(theme.panelBorder, 0.42f));
	ImGui::PushStyleColor(ImGuiCol_Text,
		good ? theme.pageIndicatorActive : theme.textSecondary);
	ImGui::Button(label, { 0.0f, 32.0f });
	ImGui::PopStyleColor(3);
}

static bool s_twoColumnsActive = false;

void BeginTwoColumns(float leftWidth = 0.48f)
{
	s_twoColumnsActive = ImGui::BeginTable("##ryubox-two-columns", 2,
		ImGuiTableFlags_SizingStretchProp | ImGuiTableFlags_NoSavedSettings);
	if (s_twoColumnsActive)
	{
		ImGui::TableSetupColumn("left", ImGuiTableColumnFlags_WidthStretch, leftWidth);
		ImGui::TableSetupColumn("right", ImGuiTableColumnFlags_WidthStretch, 1.0f - leftWidth);
		ImGui::TableNextRow();
		ImGui::TableSetColumnIndex(0);
	}
}

void MiddleColumn()
{
	if (s_twoColumnsActive)
	{
		ImGui::TableSetColumnIndex(1);
	}
}

void EndTwoColumns()
{
	if (s_twoColumnsActive)
	{
		ImGui::EndTable();
		s_twoColumnsActive = false;
	}
}

void DrawTextureContained(TextureHandle texture, ImVec2 size)
{
	if (!texture || texture.width == 0 || texture.height == 0)
	{
		ImGui::Dummy(size);
		return;
	}
	const float sourceAspect = static_cast<float>(texture.width) /
		static_cast<float>(texture.height);
	ImVec2 drawSize = size;
	const float targetAspect = size.x / std::max(1.0f, size.y);
	if (sourceAspect > targetAspect)
		drawSize.y = size.x / sourceAspect;
	else
		drawSize.x = size.y * sourceAspect;
	ImVec2 cursor = ImGui::GetCursorPos();
	ImGui::SetCursorPos({ cursor.x + (size.x - drawSize.x) * 0.5f,
		cursor.y + (size.y - drawSize.y) * 0.5f });
#if IMGUI_VERSION_NUM >= 19200
	ImGui::Image(ImTextureRef(static_cast<ImTextureID>(texture.id)), drawSize);
#else
	ImGui::Image(reinterpret_cast<ImTextureID>(texture.id), drawSize);
#endif
	ImGui::SetCursorPos({ cursor.x, cursor.y + size.y });
}

std::string GuestForHost(const RyuboxPlayerInputSettings& player, std::string_view host)
{
	const auto found = std::find_if(player.bindings.begin(), player.bindings.end(),
		[host](const RyuboxButtonBinding& binding) { return binding.hostInput == host; });
	return found == player.bindings.end() ? std::string("?") : found->guestButton;
}

void DrawMappingChip(ImDrawList* draw, const ShellTheme& theme, ImVec2 min, ImVec2 max,
	std::string_view host, std::string_view guest)
{
	if (!draw)
		return;
	const ImU32 panel = ImGui::ColorConvertFloat4ToU32(Alpha(theme.panelBase, 0.96f));
	const ImU32 border = ImGui::ColorConvertFloat4ToU32(Alpha(theme.panelBorder, 0.92f));
	const ImU32 pink = ImGui::ColorConvertFloat4ToU32(theme.cursorNormal);
	const ImU32 cyan = ImGui::ColorConvertFloat4ToU32(theme.pageIndicatorActive);
	const ImU32 muted = ImGui::ColorConvertFloat4ToU32(theme.textSecondary);
	draw->AddRectFilled(min, max, panel, 7.0f);
	draw->AddRect(min, max, border, 7.0f, ImDrawFlags_RoundCornersAll, 1.0f);
	draw->AddRect({ min.x, min.y }, { min.x + 3.0f, max.y }, pink, 7.0f,
		ImDrawFlags_RoundCornersLeft, 2.0f);

	const std::string hostText(host);
	const std::string guestText(guest);
	const ImVec2 hostSize = ImGui::CalcTextSize(hostText.c_str());
	const ImVec2 arrowSize = ImGui::CalcTextSize("  >  ");
	const ImVec2 guestSize = ImGui::CalcTextSize(guestText.c_str());
	const float total = hostSize.x + arrowSize.x + guestSize.x;
	const float x = min.x + std::max(8.0f, ((max.x - min.x) - total) * 0.5f);
	const float y = min.y + ((max.y - min.y) - hostSize.y) * 0.5f;
	draw->AddText({ x, y }, pink, hostText.c_str());
	draw->AddText({ x + hostSize.x, y }, muted, "  >  ");
	draw->AddText({ x + hostSize.x + arrowSize.x, y }, cyan, guestText.c_str());
}

void DrawXboxSwitchMappingDiagram(RyuboxPlayerInputSettings& player,
	std::size_t playerIndex, const RyuboxSettingsCallbacks& callbacks, ImVec2 requestedSize)
{
	const ShellTheme& theme = CurrentWidgetTheme();
	const float width = std::max(420.0f, requestedSize.x);
	const float height = std::max(330.0f, requestedSize.y);
	const ImVec2 origin = ImGui::GetCursorScreenPos();
	ImGui::InvisibleButton("##xbox-switch-diagram", { width, height });
	ImDrawList* draw = ImGui::GetWindowDrawList();
	if (!draw)
		return;

	const auto P = [origin, width, height](float x, float y) {
		return ImVec2(origin.x + x * width, origin.y + y * height);
	};
	const ImU32 panel = ImGui::ColorConvertFloat4ToU32(Alpha(theme.panelBase, 0.74f));
	const ImU32 border = ImGui::ColorConvertFloat4ToU32(Alpha(theme.panelBorder, 0.80f));
	const ImU32 line = ImGui::ColorConvertFloat4ToU32(Alpha(theme.textSecondary, 0.92f));
	const ImU32 pink = ImGui::ColorConvertFloat4ToU32(theme.cursorNormal);
	const ImU32 cyan = ImGui::ColorConvertFloat4ToU32(theme.pageIndicatorActive);
	const ImU32 text = ImGui::ColorConvertFloat4ToU32(theme.textPrimary);
	const ImU32 muted = ImGui::ColorConvertFloat4ToU32(theme.textSecondary);

	draw->AddRectFilled(origin, { origin.x + width, origin.y + height }, panel, 12.0f);
	draw->AddRect(origin, { origin.x + width, origin.y + height }, border, 12.0f,
		ImDrawFlags_RoundCornersAll, 1.0f);

	// Header / legend: host Xbox input is Ryubox pink, emulated Switch input is cyan.
	draw->AddText(P(0.035f, 0.035f), text, "MAPEAMENTO: XBOX SERIES -> NINTENDO SWITCH");
	draw->AddCircleFilled(P(0.640f, 0.055f), 4.0f, pink, 20);
	draw->AddText(P(0.655f, 0.035f), muted, "Xbox");
	draw->AddCircleFilled(P(0.760f, 0.055f), 4.0f, cyan, 20);
	draw->AddText(P(0.775f, 0.035f), muted, "Switch");

	// Xbox Series inspired line-art silhouette. It deliberately uses draw-list primitives
	// instead of a raster controller photo so it scales cleanly with the Xbox UI.
	const ImVec2 cTopL = P(0.285f, 0.285f);
	const ImVec2 cTopR = P(0.715f, 0.285f);
	const ImVec2 cBottomL = P(0.235f, 0.825f);
	const ImVec2 cBottomR = P(0.765f, 0.825f);
	// Wide Xbox Series silhouette: rounded shoulders, deep grips and the central battery ridge.
	draw->AddBezierCubic(cTopL, P(0.235f, 0.30f), P(0.185f, 0.61f), cBottomL, line, 2.2f);
	draw->AddBezierCubic(cBottomL, P(0.255f, 0.94f), P(0.355f, 0.755f), P(0.435f, 0.755f), line, 2.2f);
	draw->AddBezierCubic(P(0.435f, 0.755f), P(0.475f, 0.735f), P(0.525f, 0.735f), P(0.565f, 0.755f), line, 2.2f);
	draw->AddBezierCubic(P(0.565f, 0.755f), P(0.645f, 0.755f), P(0.745f, 0.94f), cBottomR, line, 2.2f);
	draw->AddBezierCubic(cBottomR, P(0.815f, 0.61f), P(0.765f, 0.30f), cTopR, line, 2.2f);
	draw->AddBezierCubic(cTopL, P(0.345f, 0.205f), P(0.420f, 0.255f), P(0.500f, 0.255f), line, 2.2f);
	draw->AddBezierCubic(P(0.500f, 0.255f), P(0.580f, 0.255f), P(0.655f, 0.205f), cTopR, line, 2.2f);
	// Grip seams visible in the reference drawing.
	draw->AddBezierCubic(P(0.245f, 0.81f), P(0.29f, 0.77f), P(0.32f, 0.70f), P(0.355f, 0.665f), line, 1.0f);
	draw->AddBezierCubic(P(0.755f, 0.81f), P(0.71f, 0.77f), P(0.68f, 0.70f), P(0.645f, 0.665f), line, 1.0f);
	// Shoulder/trigger ridges.
	draw->AddBezierCubic(P(0.29f, 0.29f), P(0.31f, 0.17f), P(0.405f, 0.17f), P(0.42f, 0.255f), line, 1.4f);
	draw->AddBezierCubic(P(0.58f, 0.255f), P(0.595f, 0.17f), P(0.69f, 0.17f), P(0.71f, 0.29f), line, 1.4f);

	// Sticks.
	for (const ImVec2 center : { P(0.355f, 0.445f), P(0.585f, 0.625f) })
	{
		draw->AddCircle(center, height * 0.078f, line, 40, 1.7f);
		draw->AddCircle(center, height * 0.057f, line, 40, 1.3f);
		draw->AddCircleFilled(center, height * 0.041f, ImGui::ColorConvertFloat4ToU32(Alpha(theme.panelBase, 0.88f)), 40);
	}
	// D-pad.
	const ImVec2 dp = P(0.405f, 0.625f);
	const float d = height * 0.043f;
	draw->AddRect({ dp.x - d * 0.45f, dp.y - d * 1.3f }, { dp.x + d * 0.45f, dp.y + d * 1.3f }, line, 3.0f);
	draw->AddRect({ dp.x - d * 1.3f, dp.y - d * 0.45f }, { dp.x + d * 1.3f, dp.y + d * 0.45f }, line, 3.0f);

	// Xbox/home, View and Menu buttons.
	draw->AddCircle(P(0.500f, 0.365f), height * 0.037f, line, 28, 1.5f);
	draw->AddText(P(0.488f, 0.340f), text, "X");
	draw->AddCircle(P(0.465f, 0.465f), height * 0.022f, line, 24, 1.2f);
	draw->AddCircle(P(0.535f, 0.465f), height * 0.022f, line, 24, 1.2f);
	draw->AddText(P(0.455f, 0.443f), muted, "[]");
	draw->AddText(P(0.527f, 0.443f), muted, "=");

	// Face buttons: physical Xbox labels remain readable; mapping callouts carry pink/cyan semantics.
	const std::array<std::pair<const char*, ImVec2>, 4> face = {{
		{ "Y", P(0.665f, 0.405f) }, { "B", P(0.710f, 0.485f) },
		{ "A", P(0.665f, 0.565f) }, { "X", P(0.620f, 0.485f) },
	}};
	for (const auto& [label, center] : face)
	{
		draw->AddCircle(center, height * 0.030f, line, 24, 1.3f);
		const ImVec2 ts = ImGui::CalcTextSize(label);
		draw->AddText({ center.x - ts.x * 0.5f, center.y - ts.y * 0.5f }, text, label);
	}

	struct Callout { const char* host; ImVec2 chipMin; ImVec2 chipMax; ImVec2 anchor; bool right; };
	const std::array<Callout, 10> callouts = {{
		{ "LT", P(0.025f, 0.16f), P(0.205f, 0.24f), P(0.345f, 0.25f), false },
		{ "LB", P(0.025f, 0.28f), P(0.205f, 0.36f), P(0.330f, 0.31f), false },
		{ "LS", P(0.025f, 0.58f), P(0.225f, 0.66f), P(0.390f, 0.47f), false },
		{ "View", P(0.255f, 0.10f), P(0.440f, 0.18f), P(0.485f, 0.49f), false },
		{ "Menu", P(0.560f, 0.10f), P(0.745f, 0.18f), P(0.548f, 0.49f), true },
		{ "RT", P(0.795f, 0.16f), P(0.975f, 0.24f), P(0.655f, 0.25f), true },
		{ "RB", P(0.795f, 0.28f), P(0.975f, 0.36f), P(0.670f, 0.31f), true },
		{ "Y", P(0.795f, 0.43f), P(0.975f, 0.51f), P(0.655f, 0.425f), true },
		{ "B", P(0.795f, 0.54f), P(0.975f, 0.62f), P(0.690f, 0.500f), true },
		{ "A", P(0.795f, 0.65f), P(0.975f, 0.73f), P(0.655f, 0.575f), true },
	}};
	for (const Callout& c : callouts)
	{
		const std::string guest = GuestForHost(player, c.host);
		DrawMappingChip(draw, theme, c.chipMin, c.chipMax, c.host, guest);
		const ImVec2 edge = c.right ? ImVec2(c.chipMin.x, (c.chipMin.y + c.chipMax.y) * 0.5f) :
			ImVec2(c.chipMax.x, (c.chipMin.y + c.chipMax.y) * 0.5f);
		const ImVec2 elbow = { c.right ? edge.x - width * 0.055f : edge.x + width * 0.055f, edge.y };
		draw->AddLine(edge, elbow, pink, 1.3f);
		draw->AddLine(elbow, c.anchor, cyan, 1.1f);
	}

	// Remaining face X and right-stick mappings are kept close to the controller to avoid overflow.
	DrawMappingChip(draw, theme, P(0.795f, 0.76f), P(0.975f, 0.84f), "X", GuestForHost(player, "X"));
	DrawMappingChip(draw, theme, P(0.560f, 0.86f), P(0.745f, 0.94f), "RS", GuestForHost(player, "RS"));
	DrawMappingChip(draw, theme, P(0.255f, 0.86f), P(0.440f, 0.94f), "D-Pad", GuestForHost(player, "D-Pad"));

	// Clicking the diagram opens a practical binding entry point without making the raster/shape itself stateful.
	if (ImGui::IsItemHovered() && ImGui::IsMouseDoubleClicked(ImGuiMouseButton_Left) && callbacks.captureBinding)
		callbacks.captureBinding(playerIndex, "A");
}

void DrawControllerMappings(RyuboxPlayerInputSettings& player,
	std::size_t playerIndex, const RyuboxSettingsCallbacks& callbacks)
{
	SectionTitle("Comparacao de botoes: Xbox -> Switch");
	if (player.bindings.empty())
	{
		ImGui::TextDisabled("No bindings supplied by the host.");
		return;
	}

	if (ImGui::BeginTable("##bindings", 4,
		ImGuiTableFlags_SizingStretchSame | ImGuiTableFlags_NoSavedSettings))
	{
		for (std::size_t index = 0; index < player.bindings.size(); ++index)
		{
			ImGui::TableNextColumn();
			const RyuboxButtonBinding& binding = player.bindings[index];
			std::string label = binding.hostInput + "  ->  " + binding.guestButton;
			ImGui::PushID(static_cast<int>(index));
			if (ShellButton(label.c_str(), { 0.0f, 42.0f }) && callbacks.captureBinding)
				callbacks.captureBinding(playerIndex, binding.guestButton);
			ImGui::PopID();
		}
		ImGui::EndTable();
	}
}

void DrawSetup(RyuboxSetupSettings& setup, const RyuboxSettingsCallbacks& callbacks)
{
	// Keep setup actions in one vertical navigation lane. On Xbox, placing
	// Firmware/Keys in the left table column and Library in the right column
	// made ImGui's spatial navigation prefer horizontal neighbours and could
	// strand the focus on the firmware action.
	SectionTitle("Firmware do Nintendo Switch");
	ImGui::Text("Firmware: %s", setup.firmwareVersion.c_str());
	ImGui::TextDisabled("%s", setup.firmwareStatus.c_str());
	ImGui::TextWrapped("Selecione diretamente um arquivo ZIP ou XCI de firmware. A instalacao e feita agora e permanece salva no Ryubox.");
	if (ShellButton("Instalar firmware de arquivo (.ZIP / .XCI)") && callbacks.action)
		callbacks.action(RyuboxSettingsAction::InstallFirmware);
	ImGui::Spacing();
	SectionTitle("Chaves");
	ImGui::Text("prod.keys: %s", setup.prodKeysStatus.c_str());
	ImGui::Text("title.keys: %s", setup.titleKeysStatus.c_str());
	ImGui::TextWrapped("Escolha a pasta que contem prod.keys. Se title.keys estiver na mesma pasta, ele tambem sera importado.");
	if (ShellButton("Selecionar pasta de prod.keys") && callbacks.action)
		callbacks.action(RyuboxSettingsAction::ImportProdKeys);
	ImGui::TextDisabled("O progresso e os erros aparecem na faixa de status inferior.");

	ImGui::Spacing();
	ImGui::Separator();
	ImGui::Spacing();
	SectionTitle("Biblioteca");
	if (setup.gameDirectories.empty())
		ImGui::TextDisabled("Nenhuma pasta de jogos adicionada.");
	else
	{
		for (const std::string& path : setup.gameDirectories)
			ImGui::BulletText("%s", path.c_str());
	}
	if (ShellButton("Adicionar pasta de jogos") && callbacks.action)
		callbacks.action(RyuboxSettingsAction::AddGameDirectory);
	if (ShellButton("Remover pasta selecionada") && callbacks.action)
		callbacks.action(RyuboxSettingsAction::RemoveGameDirectory);
	if (ShellButton("Atualizar biblioteca") && callbacks.action)
		callbacks.action(RyuboxSettingsAction::RefreshLibrary);

	ImGui::Spacing();
	ImGui::Separator();
	ImGui::Spacing();
	SectionTitle("Capas de Jogos");
	ImGui::TextWrapped("Personalize as capas dos jogos utilizando imagens PNG de uma pasta local.");
	if (ShellButton("Selecionar capas de jogos"))
	{
		ImGui::OpenPopup("Aviso: Capas de Jogos");
	}

	if (ImGui::BeginPopupModal("Aviso: Capas de Jogos", nullptr, ImGuiWindowFlags_AlwaysAutoResize))
	{
		ImGui::TextWrapped("se vc escolher uma pasta com capinhas vc ira perders todas as capas embutidas");
		ImGui::Spacing();
		ImGui::Separator();
		ImGui::Spacing();
		if (ShellButton("Confirmar e Escolher Pasta"))
		{
			ImGui::CloseCurrentPopup();
			if (callbacks.action)
				callbacks.action(RyuboxSettingsAction::PickCoversFolder);
		}
		ImGui::SameLine();
		if (ShellButton("Cancelar"))
		{
			ImGui::CloseCurrentPopup();
		}
		ImGui::EndPopup();
	}
}

void DrawSystem(RyuboxSystemSettings& model, const RyuboxSettingsCallbacks& callbacks)
{
	BeginTwoColumns();
	SectionTitle("Console");
	DrawChoice("system.consoleMode", model.consoleMode, callbacks);
	DrawChoice("system.language", model.language, callbacks);
	DrawChoice("system.region", model.region, callbacks);
	DrawChoice("system.timeZone", model.timeZone, callbacks);
	DrawInt("system.timeOffset", model.systemTimeOffsetMinutes, callbacks);

	MiddleColumn();
	SectionTitle("Compatibilidade");
	DrawToggle("system.ignoreMissingServices", model.ignoreMissingServices, callbacks);
	DrawToggle("system.fsIntegrity", model.fsIntegrityChecks, callbacks);
	DrawToggle("system.expandedDram", model.expandedDram, callbacks);
	EndTwoColumns();
}

void DrawCpu(RyuboxCpuSettings& model, const RyuboxSettingsCallbacks& callbacks)
{
	SectionTitle("CPU / JIT");
	DrawToggle("cpu.ptc", model.ptc, callbacks);
	DrawChoice("cpu.memoryManager", model.memoryManagerMode, callbacks);
	DrawToggle("cpu.hypervisor", model.hypervisor, callbacks);
	DrawToggle("cpu.fastMemory", model.fastMemory, callbacks);
	ImGui::Spacing();
	ImGui::TextDisabled("Opcoes que exigem reinicio do CoreHost devem ser aplicadas no proximo jogo.");
}

void DrawAudio(RyuboxAudioSettings& model, const RyuboxSettingsCallbacks& callbacks)
{
	SectionTitle("Audio");
	DrawChoice("audio.backend", model.backend, callbacks);
	DrawFloat("audio.volume", model.masterVolume, callbacks);
	DrawToggle("audio.mute", model.mute, callbacks);
	DrawToggle("audio.stretching", model.stretching, callbacks);
	DrawInt("audio.latency", model.latencyMs, callbacks);
}

void DrawNetwork(RyuboxNetworkSettings& model, const RyuboxSettingsCallbacks& callbacks)
{
	SectionTitle("Rede");
	DrawToggle("network.internet", model.guestInternetAccess, callbacks);
	DrawChoice("network.multiplayer", model.multiplayerMode, callbacks);
	DrawChoice("network.interface", model.networkInterface, callbacks);
}

void DrawLogs(RyuboxLogSettings& model, const RyuboxSettingsCallbacks& callbacks)
{
	BeginTwoColumns();
	SectionTitle("Niveis");
	DrawToggle("logs.file", model.fileLogging, callbacks);
	DrawToggle("logs.trace", model.trace, callbacks);
	DrawToggle("logs.debug", model.debug, callbacks);
	DrawToggle("logs.info", model.information, callbacks);
	DrawToggle("logs.warning", model.warning, callbacks);
	DrawToggle("logs.error", model.error, callbacks);
	DrawToggle("logs.stub", model.stub, callbacks);
	DrawToggle("logs.guest", model.guestLogs, callbacks);
	DrawToggle("logs.fs", model.fsAccessLogs, callbacks);
	DrawToggle("logs.graphics", model.graphicsDebug, callbacks);

	MiddleColumn();
	SectionTitle("Ryubox D3D12 Diagnostics");
	DrawToggle("logs.shaderTelemetry", model.shaderTelemetry, callbacks);
	DrawToggle("logs.performanceCsv", model.performanceCsv, callbacks);
	DrawToggle("logs.textureView", model.textureViewFailures, callbacks);
	DrawToggle("logs.swizzleUpload", model.swizzleUpload, callbacks);
	DrawToggle("logs.presentation", model.presentationLogs, callbacks);
	DrawToggle("logs.syncStalls", model.syncStalls, callbacks);
	ImGui::Spacing();
	ImGui::TextDisabled("Deixe diagnosticos pesados desligados no perfil Release normal.");
	EndTwoColumns();
}

void DrawControllerPage(RyuboxControllerSettings& model,
	const RyuboxSettingsCallbacks& callbacks)
{
	if (model.players.empty())
	{
		ImGui::TextDisabled("Nenhum jogador foi inicializado.");
		return;
	}
	if (model.selectedPlayer >= model.players.size())
		model.selectedPlayer = 0;
	// Keep the controller page on the stable widget path used by the working
	// build. The large custom draw-list/table diagram was the trigger for the
	// Xbox-only native termination when focus entered this page.
	SectionTitle("Jogador ativo");
	std::size_t playerIndex = model.selectedPlayer;
	std::vector<ChoiceItem> playerChoices;
	playerChoices.reserve(model.players.size());
	for (std::size_t index = 0; index < model.players.size(); ++index)
		playerChoices.push_back({ std::to_string(index), "P" + std::to_string(index + 1), true });
	if (ShellCombo("Jogador", &playerIndex, playerChoices))
		model.selectedPlayer = playerIndex;

	RyuboxPlayerInputSettings& player = model.players[model.selectedPlayer];
	SectionTitle("Xbox Series -> Switch");
	DrawChoice("controller.device", player.device, callbacks);
	DrawChoice("controller.type", player.emulatedController, callbacks);
	DrawChoice("controller.profile", player.profile, callbacks);
	DrawFloat("controller.leftDeadzone", player.leftDeadzone, callbacks);
	DrawFloat("controller.rightDeadzone", player.rightDeadzone, callbacks);
	DrawFloat("controller.stickRange", player.stickRange, callbacks);
	DrawFloat("controller.triggerThreshold", player.triggerThreshold, callbacks);
	DrawToggle("controller.invertY", player.invertY, callbacks);
	DrawToggle("controller.rumble", player.rumble, callbacks);
	DrawFloat("controller.rumbleStrength", player.rumbleStrength, callbacks);
	DrawToggle("controller.motion", player.motion, callbacks);
	DrawFloat("controller.gyroSensitivity", player.gyroSensitivity, callbacks);
	DrawFloat("controller.gyroDeadzone", player.gyroDeadzone, callbacks);
	DrawChoice("controller.dsuServer", player.dsuServer, callbacks);
	DrawInt("controller.dsuSlot", player.dsuSlot, callbacks);
	if (callbacks.action && ShellButton("Testar vibracao"))
		callbacks.action(RyuboxSettingsAction::TestRumble);
	ImGui::Spacing();
	ImGui::TextDisabled("Mapeamento ativo: A/B/X/Y, ombros, gatilhos, D-pad e sticks.");
	if (callbacks.action && ShellButton("Restaurar perfil padrao"))
		callbacks.action(RyuboxSettingsAction::RestoreControllerDefaults);
}

void DrawGraphicsPage(RyuboxGraphicsSettings& model,
	const RyuboxSettingsCallbacks& callbacks)
{
	// Keep video settings on one stable widget lane. This avoids the nested
	// table path that was crashing when Xbox focus entered the video page.
	SectionTitle("Video / D3D12");
	DrawChoice("graphics.backend", model.backend, callbacks);
	DrawChoice("graphics.gpu", model.preferredGpu, callbacks);
	DrawChoice("graphics.resolutionScale", model.resolutionScale, callbacks);
	DrawFloat("graphics.customScale", model.customResolutionScale, callbacks);
	DrawChoice("graphics.aspectRatio", model.aspectRatio, callbacks);
	DrawToggle("graphics.vsync", model.vsync, callbacks);
	DrawInt("graphics.fpsLimit", model.fpsLimit, callbacks);

	DrawChoice("graphics.anisotropy", model.anisotropicFiltering, callbacks);
	DrawChoice("graphics.aa", model.antiAliasing, callbacks);
	DrawChoice("graphics.scalingFilter", model.scalingFilter, callbacks);
	DrawFloat("graphics.sharpness", model.scalingSharpness, callbacks);
	DrawToggle("graphics.textureRecompression", model.textureRecompression, callbacks);
	DrawToggle("graphics.shaderCache", model.shaderCache, callbacks);
	DrawToggle("graphics.gpuThreading", model.gpuThreading, callbacks);
	DrawToggle("graphics.macroHle", model.macroHle, callbacks);
	DrawToggle("graphics.colorSpace", model.colorSpacePassthrough, callbacks);
	DrawToggle("graphics.showFpsCounter", model.showFpsCounter, callbacks);
	ImGui::Spacing();
	SectionTitle("D3D12 / Xbox — status, nao toggles de compatibilidade");
	ImGui::TextDisabled("Crop normalization: %s", model.nativeCropNormalization ? "ativo" : "inativo");
	ImGui::TextDisabled("Subresource hazards: %s", model.subresourceHazards ? "ativo" : "inativo");
	ImGui::TextDisabled("GPU-only presentation: %s", model.gpuOnlyPresentation ? "ativo" : "inativo");
	ImGui::TextDisabled("Persistent shader cache: %s", model.persistentShaderDiskCache ? "ativo" : "inativo");
	if (callbacks.action && ShellButton("Limpar shader cache", { 220.0f, 40.0f }))
		callbacks.action(RyuboxSettingsAction::ClearShaderCache);
}
}

RyuboxSettingsCallbacks MakeRyuboxRuntimeCallbacks(
	IRyuboxSettingsRuntime& runtime, RyuboxSettingsModel& model)
{
	RyuboxSettingsCallbacks callbacks;
	callbacks.strictRuntimeBinding = true;
	callbacks.isBound = [&runtime](std::string_view id) {
		return runtime.IsSettingBound(id);
	};
	callbacks.changed = [&runtime, &model](std::string_view id) {
		(void)runtime.ApplySetting(id, model);
	};
	callbacks.action = [&runtime, &model](RyuboxSettingsAction action) {
		runtime.ExecuteSettingsAction(action, model);
	};
	callbacks.captureBinding = [&runtime](std::size_t player, std::string_view guestButton) {
		runtime.CaptureControllerBinding(player, guestButton);
	};
	return callbacks;
}

bool RefreshRyuboxSettingsFromRuntime(
	IRyuboxSettingsRuntime& runtime, RyuboxSettingsModel& model)
{
	return runtime.RefreshModel(model);
}

bool ApplyRyuboxFrontendInterfaceSetting(Frontend& frontend,
	std::string_view id, const RyuboxInterfaceSettings& settings)
{
	if (id == "interface.catalogueLayout")
	{
		CatalogueLayoutMode mode = CatalogueLayoutMode::MinimalistShelfPS5;
		if (settings.catalogueLayout.selected == 1)
			mode = CatalogueLayoutMode::PremiumCarousel3D;
		else if (settings.catalogueLayout.selected == 2)
			mode = CatalogueLayoutMode::Grid;
		frontend.SetCatalogueLayout(mode);
		return true;
	}
	if (id == "interface.carouselReflections")
	{
		frontend.SetCarouselReflections(settings.carouselReflections.value);
		return true;
	}
	if (id == "interface.carouselGlow")
	{
		frontend.SetCarouselGlow(settings.carouselGlow.value);
		return true;
	}
	if (id == "interface.carouselDepth")
	{
		frontend.SetCarouselDepthStrength(settings.carouselDepth.value);
		return true;
	}
	if (id == "interface.launchAnimation")
	{
		frontend.SetAnimateLaunches(settings.launchAnimation.value);
		return true;
	}
	return false;
}

void SyncRyuboxFrontendInterfaceSettings(const Frontend& frontend,
	RyuboxInterfaceSettings& settings)
{
	const FrontendConfiguration& configuration = frontend.Configuration();
	settings.catalogueLayout.selected =
		configuration.catalogueLayout == CatalogueLayoutMode::MinimalistShelfPS5 ? 0u :
		(configuration.catalogueLayout == CatalogueLayoutMode::PremiumCarousel3D ? 1u : 2u);
	settings.carouselReflections.value = configuration.premiumCarousel.reflections;
	settings.carouselGlow.value = configuration.premiumCarousel.selectedGlow;
	settings.carouselDepth.value = configuration.premiumCarousel.depthStrength;
	settings.launchAnimation.value = configuration.animateLaunches;
}

bool ApplyRyuboxShellPresentationSetting(ShellPresentation& presentation,
	std::string_view id, const RyuboxInterfaceSettings& settings)
{
	if (id == "interface.dynamicBackground")
	{
		presentation.overrideThemeDynamicBackground = !settings.dynamicBackground.value;
		if (!settings.dynamicBackground.value)
		{
			presentation.backgroundArtwork.reset();
			presentation.videoBackground = false;
		}
		return true;
	}
	if (id == "interface.controllerHints")
	{
		presentation.showActionHints = settings.controllerHints.value;
		return true;
	}
	return false;
}

void SyncRyuboxShellPresentationSettings(const ShellPresentation& presentation,
	RyuboxInterfaceSettings& settings)
{
	settings.dynamicBackground.value = !presentation.overrideThemeDynamicBackground ||
		presentation.backgroundArtwork.has_value() || presentation.videoBackground;
	settings.controllerHints.value = presentation.showActionHints;
}

RyuboxSettingsModel MakeDefaultRyuboxSettingsModel()
{
	RyuboxSettingsModel model;

	auto choices = [](std::initializer_list<const char*> labels) {
		std::vector<ChoiceItem> result;
		for (const char* label : labels)
			result.push_back({ label, label, true });
		return result;
	};

	model.system.consoleMode = { "Modo do console", choices({ "Docked", "Handheld" }), 0, true, false };
	model.system.language = { "Idioma do sistema", choices({ "PortugueseBrazil", "EnglishUS", "Japanese", "Spanish" }), 0, true, false };
	model.system.region = { "Regiao", choices({ "Brazil", "USA", "Europe", "Japan" }), 0, true, false };
	model.system.timeZone = { "Fuso horario", choices({ "System", "UTC-03:00", "UTC", "UTC+09:00" }), 0, true, false };
	model.system.systemTimeOffsetMinutes = { "Offset do relogio (min)", 0, -1440, 1440, "%d min", true, false };
	model.system.ignoreMissingServices = { "Ignorar servicos ausentes", false, true, false };
	model.system.fsIntegrityChecks = { "FS integrity checks", true, true, false };
	model.system.expandedDram = { "Expanded DRAM", false, false, false };

	model.cpu.ptc = { "PTC / PPTC", true, true, false };
	model.cpu.memoryManagerMode = { "Memory manager", choices({ "HostMappedUnsafe", "HostMapped", "SoftwarePageTable" }), 0, true, false };
	model.cpu.hypervisor = { "Hypervisor", false, false, false };
	model.cpu.fastMemory = { "Fast memory mode", true, true, false };

	model.graphics.backend = { "Backend", choices({ "D3D12 Native" }), 0, true, false };
	model.graphics.preferredGpu = { "GPU", choices({ "Xbox GPU" }), 0, true, false };
	model.graphics.resolutionScale = { "Escala de resolucao", choices({ "0.5x", "0.75x", "1x", "1.5x", "2x", "3x", "Custom" }), 2, true, false };
	model.graphics.customResolutionScale = { "Escala custom", 1.0f, 0.5f, 4.0f, "%.2fx", true, false };
	model.graphics.aspectRatio = { "Aspect ratio", choices({ "16:9", "16:10", "21:9", "4:3", "Stretch" }), 0, true, true };
	model.graphics.vsync = { "VSync", true, true, true };
	model.graphics.anisotropicFiltering = { "Filtro anisotropico", choices({ "Auto", "2x", "4x", "8x", "16x" }), 0, true, true };
	model.graphics.antiAliasing = { "Anti-aliasing", choices({ "None", "FXAA", "SMAA Low", "SMAA Medium", "SMAA High", "SMAA Ultra" }), 0, true, true };
	model.graphics.scalingFilter = { "Scaling filter", choices({ "Bilinear", "Nearest", "FSR", "Area" }), 0, true, true };
	model.graphics.scalingSharpness = { "Sharpness", 0.2f, 0.0f, 1.0f, "%.2f", true, true };
	model.graphics.textureRecompression = { "Texture recompression", true, true, false };
	model.graphics.shaderCache = { "Shader disk cache", true, true, false };
	model.graphics.gpuThreading = { "GPU threading", true, true, false };
	model.graphics.macroHle = { "Macro HLE", true, true, false };
	model.graphics.colorSpacePassthrough = { "Color-space passthrough", false, false, false };
	model.graphics.showFpsCounter = { "Mostrar contador de FPS", true, true, false };
	model.graphics.fpsLimit = { "FPS limit", 0, 0, 240, "%d FPS", true, true };

	model.audio.backend = { "Backend de audio", choices({ "XAudio2", "SDL2", "Dummy" }), 0, true, false };
	model.audio.masterVolume = { "Volume", 1.0f, 0.0f, 1.0f, "%.0f%%", true, true };
	model.audio.mute = { "Mute", false, true, true };
	model.audio.stretching = { "Audio stretching / sync", true, true, true };
	model.audio.latencyMs = { "Buffer / latencia", 60, 10, 250, "%d ms", true, false };

	for (std::size_t i = 0; i < model.controllers.players.size(); ++i)
	{
		auto& player = model.controllers.players[i];
		player.device = { "Dispositivo", choices({ "Xbox Wireless Controller", "Keyboard", "None" }), i == 0 ? 0u : 2u, true, true };
		player.emulatedController = { "Controle emulado", choices({ "Pro Controller", "Joy-Con Pair", "Joy-Con Left", "Joy-Con Right", "Handheld" }), 0, true, false };
		player.profile = { "Perfil", choices({ "Default", "Global", "Per-game" }), 0, true, true };
		player.leftDeadzone = { "Deadzone esquerdo", 0.12f, 0.0f, 0.5f, "%.2f", true, true };
		player.rightDeadzone = { "Deadzone direito", 0.12f, 0.0f, 0.5f, "%.2f", true, true };
		player.stickRange = { "Stick range", 1.0f, 0.5f, 1.5f, "%.2f", true, true };
		player.triggerThreshold = { "Trigger threshold", 0.20f, 0.0f, 1.0f, "%.2f", true, true };
		player.rumble = { "Vibracao", true, true, true };
		player.rumbleStrength = { "Forca da vibracao", 1.0f, 0.0f, 1.0f, "%.0f%%", true, true };
		player.motion = { "Motion / gyro", false, true, true };
		player.gyroSensitivity = { "Sensibilidade gyro", 1.0f, 0.1f, 3.0f, "%.2f", true, true };
		player.gyroDeadzone = { "Deadzone gyro", 0.01f, 0.0f, 0.25f, "%.3f", true, true };
		player.invertY = { "Inverter Y", false, true, true };
		player.dsuServer = { "DSU server", choices({ "Off", "127.0.0.1:26760" }), 0, true, true };
		player.dsuSlot = { "DSU slot", static_cast<int>(i), 0, 7, "%d", true, true };
		player.bindings = {
			{ "A", "B" }, { "B", "A" }, { "X", "Y" }, { "Y", "X" },
			{ "L", "LB" }, { "R", "RB" }, { "ZL", "LT" }, { "ZR", "RT" },
			{ "+", "Menu" }, { "-", "View" }, { "L Stick", "LS" }, { "R Stick", "RS" },
			{ "D-Pad", "D-Pad" }, { "Left Stick", "Left Stick" }, { "Right Stick", "Right Stick" },
		};
	}

	model.network.guestInternetAccess = { "Guest internet access", true, true, false };
	model.network.multiplayerMode = { "Multiplayer / LDN", choices({ "Disabled", "LAN", "LDN" }), 0, true, false };
	model.network.networkInterface = { "Interface de rede", choices({ "Auto" }), 0, true, false };

	model.logs.fileLogging = { "Salvar log em arquivo", true, true, true };
	model.logs.trace = { "Trace", false, true, true };
	model.logs.debug = { "Debug", false, true, true };
	model.logs.information = { "Info", true, true, true };
	model.logs.warning = { "Warning", true, true, true };
	model.logs.error = { "Error", true, true, true };
	model.logs.stub = { "Stub", false, true, true };
	model.logs.guestLogs = { "Guest logs", false, true, true };
	model.logs.fsAccessLogs = { "FS access logs", false, true, true };
	model.logs.graphicsDebug = { "Graphics debug", false, true, true };
	model.logs.shaderTelemetry = { "Shader telemetry", false, true, true };
	model.logs.performanceCsv = { "Performance CSV", false, true, true };
	model.logs.textureViewFailures = { "TextureView failure logs", false, true, true };
	model.logs.swizzleUpload = { "Swizzle / upload logs", false, true, true };
	model.logs.presentationLogs = { "Presentation logs", false, true, true };
	model.logs.syncStalls = { "Sync stall logs", false, true, true };

	model.interface.catalogueLayout = { "Biblioteca", choices({ "Roleta Minimalista (PS5)", "Carrossel Premium 3D", "Grade classica" }), 0, true, true };
	model.interface.carouselReflections = { "Reflexo das capas", true, true, true };
	model.interface.carouselGlow = { "Glow da capa selecionada", true, true, true };
	model.interface.carouselDepth = { "Profundidade do carrossel", 1.0f, 0.65f, 1.35f, "%.2fx", true, true };
	model.interface.dynamicBackground = { "Background dinamico", true, true, true };
	model.interface.launchAnimation = { "Animacao ao abrir jogos", true, true, true };
	model.interface.controllerHints = { "Dicas do controle", true, true, true };
	model.interface.uiSoundEffects = { "Sons da interface", true, true, true };
	model.interface.language = { "Idioma da interface", choices({ "Português (Brasil)", "English" }), 0, true, true };
	model.interface.searchBehavior = { "Busca", choices({ "Instantanea", "Ao confirmar" }), 0, true, true };
	model.interface.confirmExit = { "Confirmar ao sair", true, true, true };

	return model;
}

RyuboxQuickViewResult DrawRyuboxControllerQuickView(
	RyuboxControllerSettings& model, const RyuboxSettingsCallbacks& callbacks,
	ImVec2 size)
{
	RyuboxQuickViewResult result;
	const ShellTheme& theme = CurrentWidgetTheme();
	const ImVec2 resolved = ResolveChildSize(size);
	if (!BeginShellPane("##ryubox-controller-quick", resolved,
		ImGuiWindowFlags_NoScrollbar | ImGuiWindowFlags_NoScrollWithMouse,
		20.0f, 0.84f, { 22.0f, 18.0f }))
	{
		EndShellPane();
		return result;
	}

	ImGui::PushStyleColor(ImGuiCol_Text, theme.textPrimary);
	ImGui::Text("RYUBOX");
	ImGui::SameLine();
	ImGui::TextColored(theme.cursorNormal, "  CONTROLES");
	ImGui::PopStyleColor();
	ImGui::TextDisabled("Xbox Series -> Nintendo Switch mapping");
	ImGui::Separator();

	// Player tabs fit in one 1080p row and are controller friendly.
	if (ImGui::BeginTable("##players", 8,
		ImGuiTableFlags_SizingStretchSame | ImGuiTableFlags_NoSavedSettings))
	{
		for (std::size_t index = 0; index < model.players.size(); ++index)
		{
			ImGui::TableNextColumn();
			char label[8]{};
			std::snprintf(label, sizeof(label), "P%zu", index + 1);
			if (ShellSelectableWithId(label, label, model.selectedPlayer == index,
				{ 0.0f, 40.0f }))
				model.selectedPlayer = index;
		}
		ImGui::EndTable();
	}
	ImGui::Spacing();

	RyuboxPlayerInputSettings& player = model.players[model.selectedPlayer];
	if (ImGui::BeginTable("##controller-layout", 2,
		ImGuiTableFlags_SizingStretchProp | ImGuiTableFlags_NoSavedSettings))
	{
		ImGui::TableSetupColumn("preview", ImGuiTableColumnFlags_WidthStretch, 0.92f);
		ImGui::TableSetupColumn("settings", ImGuiTableColumnFlags_WidthStretch, 1.08f);
		ImGui::TableNextRow();
		ImGui::TableSetColumnIndex(0);

		SectionTitle("Xbox Series -> Switch");
		const ImVec2 available = ImGui::GetContentRegionAvail();
		DrawXboxSwitchMappingDiagram(player, model.selectedPlayer, callbacks,
			{ std::max(420.0f, available.x - 8.0f), 365.0f });
		ImGui::TextColored(theme.pageIndicatorActive, "●");
		ImGui::SameLine();
		ImGui::Text("%s", player.connectionStatus.c_str());
		if (!player.batteryText.empty())
		{
			ImGui::SameLine();
			ImGui::TextDisabled("  %s", player.batteryText.c_str());
		}
		ImGui::Spacing();
		if (callbacks.action && ShellButton("Testar vibracao", { 0.0f, 42.0f }))
			callbacks.action(RyuboxSettingsAction::TestRumble);
		if (callbacks.action && ShellButton("Salvar perfil", { 0.0f, 42.0f }))
			callbacks.action(RyuboxSettingsAction::SaveControllerProfile);

		ImGui::TableSetColumnIndex(1);
		SectionTitle(("Player " + std::to_string(model.selectedPlayer + 1)).c_str());
		DrawChoice("controller.device", player.device, callbacks);
		DrawChoice("controller.type", player.emulatedController, callbacks);
		DrawChoice("controller.profile", player.profile, callbacks);
		DrawToggle("controller.rumble", player.rumble, callbacks);
		DrawFloat("controller.leftDeadzone", player.leftDeadzone, callbacks);
		DrawFloat("controller.rightDeadzone", player.rightDeadzone, callbacks);
		DrawFloat("controller.triggerThreshold", player.triggerThreshold, callbacks);
		DrawToggle("controller.invertY", player.invertY, callbacks);
		ImGui::EndTable();
	}

	ImGui::Spacing();
	DrawControllerMappings(player, model.selectedPlayer, callbacks);
	ImGui::Spacing();
	ImGui::TextDisabled("A Selecionar   B Voltar   X Editar binding   Y Restaurar   LB/RB Jogador");

	if (ConsumeWidgetBackPressed())
		result.closeRequested = true;
	EndShellPane();
	return result;
}

RyuboxQuickViewResult DrawRyuboxGraphicsQuickView(
	RyuboxGraphicsSettings& model, const RyuboxSettingsCallbacks& callbacks,
	ImVec2 size)
{
	RyuboxQuickViewResult result;
	const ShellTheme& theme = CurrentWidgetTheme();
	const ImVec2 resolved = ResolveChildSize(size);
	if (!BeginShellPane("##ryubox-graphics-quick", resolved,
		ImGuiWindowFlags_NoScrollbar | ImGuiWindowFlags_NoScrollWithMouse,
		20.0f, 0.84f, { 22.0f, 18.0f }))
	{
		EndShellPane();
		return result;
	}

	ImGui::Text("RYUBOX");
	ImGui::SameLine();
	ImGui::TextColored(theme.cursorNormal, "  GRAFICOS");
	ImGui::TextDisabled("Escala interna e qualidade de imagem — sem tocar no CompositionScale/SetMatrixTransform.");
	ImGui::Separator();

	if (ImGui::BeginTable("##graphics-layout", 2,
		ImGuiTableFlags_SizingStretchProp | ImGuiTableFlags_NoSavedSettings))
	{
		ImGui::TableSetupColumn("preview", ImGuiTableColumnFlags_WidthStretch, 0.95f);
		ImGui::TableSetupColumn("settings", ImGuiTableColumnFlags_WidthStretch, 1.05f);
		ImGui::TableNextRow();
		ImGui::TableSetColumnIndex(0);
		SectionTitle("Preview");
		DrawTextureContained(model.previewTexture,
			{ std::max(360.0f, ImGui::GetContentRegionAvail().x - 8.0f), 300.0f });

		SectionTitle("Resumo");
		if (ImGui::BeginTable("##performance", 4,
			ImGuiTableFlags_SizingStretchSame | ImGuiTableFlags_NoSavedSettings))
		{
			const char* scale = (model.resolutionScale.selected < model.resolutionScale.choices.size()) ?
				model.resolutionScale.choices[model.resolutionScale.selected].label.c_str() : "-";
			std::array<std::string, 4> values = {
				scale,
				std::to_string(static_cast<int>(std::round(model.estimatedFps))) + " FPS",
				std::to_string(static_cast<int>(std::round(model.gpuLoadPercent))) + "% GPU",
				std::to_string(model.memoryUsageGiB).substr(0, 3) + " GiB"
			};
			for (const std::string& value : values)
			{
				ImGui::TableNextColumn();
				ImGui::TextColored(theme.cursorNormal, "%s", value.c_str());
			}
			ImGui::EndTable();
		}
		ImGui::Spacing();
		SectionTitle("D3D12 status");
		DrawStatusPill("Crop normalization: ON", model.nativeCropNormalization);
		DrawStatusPill("Subresource hazards: ON", model.subresourceHazards);
		DrawStatusPill(model.gpuOnlyPresentation ? "GPU-only presentation: ON" : "GPU-only presentation: pending",
			model.gpuOnlyPresentation);
		DrawStatusPill("Persistent shader cache: ON", model.persistentShaderDiskCache);

		ImGui::TableSetColumnIndex(1);
		SectionTitle("Configuracoes graficas");
		DrawChoice("graphics.resolutionScale", model.resolutionScale, callbacks);
		if (model.resolutionScale.selected < model.resolutionScale.choices.size() &&
			model.resolutionScale.choices[model.resolutionScale.selected].label == "Custom")
			DrawFloat("graphics.customScale", model.customResolutionScale, callbacks);
		DrawToggle("graphics.vsync", model.vsync, callbacks);
		DrawChoice("graphics.aspectRatio", model.aspectRatio, callbacks);
		DrawChoice("graphics.anisotropy", model.anisotropicFiltering, callbacks);
		DrawChoice("graphics.aa", model.antiAliasing, callbacks);
		DrawChoice("graphics.scalingFilter", model.scalingFilter, callbacks);
		DrawFloat("graphics.sharpness", model.scalingSharpness, callbacks);
		DrawToggle("graphics.textureRecompression", model.textureRecompression, callbacks);
		DrawToggle("graphics.shaderCache", model.shaderCache, callbacks);
		DrawToggle("graphics.gpuThreading", model.gpuThreading, callbacks);
		DrawToggle("graphics.macroHle", model.macroHle, callbacks);
		DrawInt("graphics.fpsLimit", model.fpsLimit, callbacks);
		ImGui::Spacing();
		if (callbacks.action && ShellButton("Aplicar", { 0.0f, 42.0f }))
		{
			callbacks.action(RyuboxSettingsAction::ApplyGraphics);
			result.applyRequested = true;
		}
		if (callbacks.action && ShellButton("Restaurar padroes", { 0.0f, 42.0f }))
		{
			callbacks.action(RyuboxSettingsAction::RestoreGraphicsDefaults);
			result.resetRequested = true;
		}
		ImGui::EndTable();
	}

	ImGui::Spacing();
	ImGui::TextDisabled("A Selecionar   B Voltar   X Aplicar   Y Restaurar");
	if (ConsumeWidgetBackPressed())
		result.closeRequested = true;
	EndShellPane();
	return result;
}

std::vector<SettingsExtensionPage> BuildRyuboxSettingsExtensions(
	RyuboxSettingsModel& model, const RyuboxSettingsCallbacks& callbacks)
{
	std::vector<SettingsExtensionPage> pages;
	pages.reserve(9);
	// Keep this order aligned with the console-first IA.  The old technical
	// grouping (CPU, network and logs as top-level pages) forced controller
	// users through implementation details before common settings.
	pages.push_back({ "ryubox-general", "Geral", true,
		[&model, callbacks] { DrawSetup(model.setup, callbacks); } });
	pages.push_back({ "ryubox-graphics", "Graficos", true,
		[&model, callbacks] { DrawGraphicsPage(model.graphics, callbacks); } });
	pages.push_back({ "ryubox-system", "Sistema", true,
		[&model, callbacks] { DrawSystem(model.system, callbacks); } });
	pages.push_back({ "ryubox-audio", "Audio", true,
		[&model, callbacks] { DrawAudio(model.audio, callbacks); } });
	pages.push_back({ "ryubox-controllers", "Controles", true,
		[&model, callbacks] { DrawControllerPage(model.controllers, callbacks); } });
	pages.push_back({ "ryubox-interface", "Interface", true,
		[&model, callbacks] { DrawRyuboxInterfaceSettings(model.interface, callbacks); } });
	pages.push_back({ "ryubox-storage", "Armazenamento", true,
		[&model, callbacks] { DrawSetup(model.setup, callbacks); } });
	pages.push_back({ "ryubox-advanced", "Avancado", true,
		[&model, callbacks] {
			SectionTitle("CPU e memoria");
			DrawCpu(model.cpu, callbacks);
			SectionTitle("Rede");
			DrawNetwork(model.network, callbacks);
			SectionTitle("Diagnostico");
			DrawLogs(model.logs, callbacks);
		} });
	pages.push_back({ "ryubox-about", "Sobre", true,
		[] {
			SectionTitle("Ryubox");
			RyuboxInfoCard("Ryubox UWP", "Frontend ImGui para Xbox e Windows. Todas as acoes devem permanecer utilizaveis somente com controle.");
		} });
	return pages;
}

void DrawRyuboxInterfaceSettings(RyuboxInterfaceSettings& model,
	const RyuboxSettingsCallbacks& callbacks)
{
	SectionTitle("Ryubox Neon / Biblioteca Premium");
	DrawChoice("interface.catalogueLayout", model.catalogueLayout, callbacks);
	DrawToggle("interface.carouselReflections", model.carouselReflections, callbacks);
	DrawToggle("interface.carouselGlow", model.carouselGlow, callbacks);
	DrawFloat("interface.carouselDepth", model.carouselDepth, callbacks);
	DrawToggle("interface.dynamicBackground", model.dynamicBackground, callbacks);
	DrawToggle("interface.launchAnimation", model.launchAnimation, callbacks);
	DrawToggle("interface.controllerHints", model.controllerHints, callbacks);
	DrawToggle("interface.uiSfx", model.uiSoundEffects, callbacks);
	DrawChoice("interface.language", model.language, callbacks);
	DrawChoice("interface.searchBehavior", model.searchBehavior, callbacks);
	DrawToggle("interface.confirmExit", model.confirmExit, callbacks);
}
}
