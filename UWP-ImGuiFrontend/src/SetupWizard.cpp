#include "UwpImGuiFrontend/SetupWizard.h"

#include <algorithm>
#include <utility>

namespace UwpImGuiFrontend
{
bool SetupWizard::CanAcceptInput() const noexcept
{
	return m_state.operation == SetupOperationState::Idle ||
		m_state.operation == SetupOperationState::Failed;
}

bool SetupWizard::Advance()
{
	if (!CanAcceptInput())
		return false;
	if (m_state.operation == SetupOperationState::Failed)
	{
		Retry();
		return true;
	}
	switch (m_state.step)
	{
	case SetupStep::Welcome:
		AdvanceStep();
		return true;
	case SetupStep::ProdKeys:
		BeginPicker(SetupPickerKind::ProdKeys, { .startLocation = PickerStartLocation::Computer });
		return true;
	case SetupStep::Firmware:
		BeginPicker(SetupPickerKind::Firmware, { .startLocation = PickerStartLocation::Computer,
			.fileTypes = { L".zip", L".xci" } });
		return true;
	case SetupStep::GamesFolder:
		BeginPicker(SetupPickerKind::GamesFolder, { .startLocation = PickerStartLocation::Computer });
		return true;
	case SetupStep::Finished:
		return false;
	}
	return false;
}

std::optional<SetupPickerRequest> SetupWizard::TakePickerRequest() noexcept
{
	return std::exchange(m_pickerRequest, std::nullopt);
}

std::unique_ptr<PickedItem> SetupWizard::TakeSelectedItem() noexcept
{
	return std::move(m_selectedItem);
}

void SetupWizard::CompletePicker(std::unique_ptr<PickedItem> item)
{
	if (m_state.operation != SetupOperationState::WaitingForPicker)
		return;
	if (!item)
	{
		m_state.operation = SetupOperationState::Idle;
		m_state.statusText.clear();
		return;
	}
	m_selectedItem = std::move(item);
	m_state.operation = SetupOperationState::Processing;
	m_state.progress = 0.0f;
	m_state.statusText = "Processing selection...";
	m_state.errorText.clear();
	m_state.popupOpen = m_popups.Open(PopupType::Progress, "ryubox-setup-progress", "ryubox-setup-next");
}

void SetupWizard::ReportProgress(float fraction, std::string status)
{
	if (m_state.operation != SetupOperationState::Processing)
		return;
	m_state.progress = std::clamp(fraction, 0.0f, 1.0f);
	m_state.statusText = std::move(status);
}

void SetupWizard::CompleteOperation(bool success, std::string message)
{
	if (m_state.operation != SetupOperationState::Processing)
		return;
	m_state.progress = success ? 1.0f : m_state.progress;
	m_state.statusText = std::move(message);
	m_state.operation = success ? SetupOperationState::Success : SetupOperationState::Failed;
	m_state.errorText = success ? std::string{} : m_state.statusText;
	m_state.requestClosePopup = true;
	m_popups.Close();
	m_state.popupOpen = false;
	m_focusRestoreRequested = true;
	m_completionSeconds = success ? 1.0f : 0.0f;
}

void SetupWizard::Tick(float deltaSeconds) noexcept
{
	if (m_state.operation != SetupOperationState::Success)
		return;
	m_completionSeconds -= std::max(0.0f, deltaSeconds);
	if (m_completionSeconds <= 0.0f)
	{
		AdvanceStep();
		m_state.operation = SetupOperationState::Idle;
		m_state.requestClosePopup = false;
	}
}

void SetupWizard::Retry() noexcept
{
	if (m_state.operation != SetupOperationState::Failed)
		return;
	m_state.operation = SetupOperationState::Idle;
	m_state.errorText.clear();
	m_state.statusText.clear();
	m_state.requestClosePopup = false;
}

void SetupWizard::Back() noexcept
{
	if (!CanAcceptInput() || m_state.step == SetupStep::Welcome || m_state.step == SetupStep::Finished)
		return;
	m_state.step = static_cast<SetupStep>(static_cast<int>(m_state.step) - 1);
	m_state.operation = SetupOperationState::Idle;
	m_state.errorText.clear();
	m_state.statusText.clear();
}

bool SetupWizard::ConsumeFocusRestore() noexcept
{
	const bool requested = m_focusRestoreRequested;
	m_focusRestoreRequested = false;
	return requested;
}

void SetupWizard::BeginPicker(SetupPickerKind kind, PickerRequest request)
{
	m_state.operation = SetupOperationState::WaitingForPicker;
	m_state.statusText.clear();
	m_state.errorText.clear();
	m_pickerRequest = SetupPickerRequest{ kind, std::move(request) };
}

void SetupWizard::AdvanceStep() noexcept
{
	if (m_state.step != SetupStep::Finished)
		m_state.step = static_cast<SetupStep>(static_cast<int>(m_state.step) + 1);
}
}
