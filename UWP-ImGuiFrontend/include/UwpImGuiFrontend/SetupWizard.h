#pragma once

#include "Pickers.h"
#include "PopupManager.h"

#include <optional>
#include <string>

namespace UwpImGuiFrontend
{
enum class SetupStep
{
	Welcome,
	ProdKeys,
	Firmware,
	GamesFolder,
	Finished,
};

enum class SetupOperationState
{
	Idle,
	WaitingForPicker,
	Processing,
	Success,
	Failed,
};

struct SetupWizardState
{
	SetupStep step = SetupStep::Welcome;
	SetupOperationState operation = SetupOperationState::Idle;
	float progress = 0.0f;
	std::string statusText;
	std::string errorText;
	bool popupOpen = false;
	bool requestClosePopup = false;
};

enum class SetupPickerKind { ProdKeys, Firmware, GamesFolder };

struct SetupPickerRequest
{
	SetupPickerKind kind{};
	PickerRequest request;
};

// UI-agnostic wizard state. Platform picker callbacks feed CompletePicker and
// the CoreHost action reports progress/completion. The controller may only
// start one action; cancellation never changes the step or opens a modal.
class SetupWizard
{
public:
	[[nodiscard]] const SetupWizardState& State() const noexcept { return m_state; }
	[[nodiscard]] bool CanAcceptInput() const noexcept;
	[[nodiscard]] bool Advance();
	[[nodiscard]] std::optional<SetupPickerRequest> TakePickerRequest() noexcept;
	[[nodiscard]] std::unique_ptr<PickedItem> TakeSelectedItem() noexcept;
	void CompletePicker(std::unique_ptr<PickedItem> item);
	void ReportProgress(float fraction, std::string status);
	void CompleteOperation(bool success, std::string message = {});
	void Tick(float deltaSeconds) noexcept;
	void Retry() noexcept;
	void Back() noexcept;
	[[nodiscard]] bool ConsumeFocusRestore() noexcept;
	[[nodiscard]] PopupManager& Popups() noexcept { return m_popups; }

private:
	void BeginPicker(SetupPickerKind kind, PickerRequest request);
	void AdvanceStep() noexcept;

	SetupWizardState m_state;
	PopupManager m_popups;
	std::optional<SetupPickerRequest> m_pickerRequest;
	std::unique_ptr<PickedItem> m_selectedItem;
	float m_completionSeconds = 0.0f;
	bool m_focusRestoreRequested = false;
};
}
