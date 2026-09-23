#include "UwpImGuiFrontend/SetupWizardView.h"

#include "UwpImGuiFrontend/Widgets.h"

#include <imgui.h>

#include <algorithm>
#include <string_view>

namespace UwpImGuiFrontend
{
namespace
{
const char* StepTitle(SetupStep step, bool english) noexcept
{
	if (english)
	{
		switch (step)
		{
		case SetupStep::Welcome: return "Welcome";
		case SetupStep::ProdKeys: return "Select your prod.keys";
		case SetupStep::Firmware: return "Install firmware";
		case SetupStep::GamesFolder: return "Select games folder";
		case SetupStep::Finished: return "Setup complete";
		}
	}
	switch (step)
	{
	case SetupStep::Welcome: return "Boas-vindas";
	case SetupStep::ProdKeys: return "Selecione seus prod.keys";
	case SetupStep::Firmware: return "Instalar firmware";
	case SetupStep::GamesFolder: return "Selecione a pasta de jogos";
	case SetupStep::Finished: return "Configuracao concluida";
	}
	return "Ryubox";
}

const char* StepDescription(SetupStep step, bool english) noexcept
{
	if (english)
	{
		switch (step)
		{
		case SetupStep::Welcome: return "Set up Ryubox using your controller.";
		case SetupStep::ProdKeys: return "Choose the folder containing prod.keys. title.keys is imported when present.";
		case SetupStep::Firmware: return "Choose a firmware ZIP or XCI file.";
		case SetupStep::GamesFolder: return "Choose the folder where your legally dumped games are stored.";
		case SetupStep::Finished: return "Ryubox is ready. You can change these settings later.";
		}
	}
	switch (step)
	{
	case SetupStep::Welcome: return "Configure o Ryubox usando somente o controle.";
	case SetupStep::ProdKeys: return "Escolha a pasta que contem prod.keys. title.keys sera importado quando existir.";
	case SetupStep::Firmware: return "Escolha um arquivo ZIP ou XCI de firmware.";
	case SetupStep::GamesFolder: return "Escolha a pasta onde estao seus jogos obtidos legalmente.";
	case SetupStep::Finished: return "O Ryubox esta pronto. Estas opcoes podem ser alteradas depois.";
	}
	return {};
}

const char* NextLabel(SetupStep step, bool english) noexcept
{
	if (step == SetupStep::Finished)
		return english ? "Open Ryubox" : "Abrir Ryubox";
	return english ? "Next" : "Seguinte";
}
}

SetupWizardViewResult DrawSetupWizard(SetupWizard& wizard, float deltaSeconds,
	bool english)
{
	SetupWizardViewResult result;
	wizard.Tick(deltaSeconds);
	const SetupWizardState& state = wizard.State();
	const bool restoreFocus = wizard.ConsumeFocusRestore();
	SectionTitle(StepTitle(state.step, english));
	ImGui::TextWrapped("%s", StepDescription(state.step, english));
	ImGui::Spacing();

	if (state.operation == SetupOperationState::Failed)
	{
		RyuboxInfoCard(english ? "Operation failed" : "Falha na operacao",
			state.errorText.empty() ? (english ? "Try again or go back." : "Tente novamente ou volte.") : state.errorText);
		if (restoreFocus)
			ImGui::SetKeyboardFocusHere();
		if (RyuboxButton(english ? "Try again" : "Tentar novamente"))
			wizard.Retry();
		if (RyuboxButton(english ? "Back" : "Voltar"))
			wizard.Back();
	}
	else if (state.operation == SetupOperationState::WaitingForPicker)
	{
		ImGui::TextDisabled("%s", english ? "Waiting for file picker..." : "Aguardando o seletor de arquivo...");
	}
	else if (state.operation == SetupOperationState::Success)
	{
		RyuboxInfoCard(english ? "Success" : "Concluido", state.statusText);
	}
	else
	{
		if (restoreFocus)
			ImGui::SetKeyboardFocusHere();
		if (RyuboxButton(NextLabel(state.step, english)))
			(void)wizard.Advance();
		ImGui::SetItemDefaultFocus();
		if (state.step != SetupStep::Welcome && state.step != SetupStep::Finished &&
			RyuboxButton(english ? "Back" : "Voltar"))
		{
			wizard.Back();
		}
	}

	if (wizard.Popups().IsOpen() && wizard.Popups().Type() == PopupType::Progress)
	{
		ImGui::OpenPopup(wizard.Popups().Id().c_str());
		if (BeginShellModal(wizard.Popups().Id().c_str()))
		{
			ImGui::TextUnformatted(english ? "Processing..." : "Processando...");
			ImGui::TextWrapped("%s", state.statusText.c_str());
			const int percent = static_cast<int>(std::clamp(state.progress, 0.0f, 1.0f) * 100.0f);
			ShellProgressBar(state.progress, std::to_string(percent) + "%");
			ImGui::TextDisabled("%s", english ? "Please do not close Ryubox." : "Nao feche o Ryubox.");
			EndShellModal();
		}
	}

	result.pickerRequest = wizard.TakePickerRequest();
	result.selectedItem = wizard.TakeSelectedItem();
	result.finished = wizard.State().step == SetupStep::Finished &&
		wizard.State().operation == SetupOperationState::Idle;
	return result;
}
}
