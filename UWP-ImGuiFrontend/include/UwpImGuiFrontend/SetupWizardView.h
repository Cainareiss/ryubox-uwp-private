#pragma once

#include "SetupWizard.h"

#include <optional>

namespace UwpImGuiFrontend
{
struct SetupWizardViewResult
{
	std::optional<SetupPickerRequest> pickerRequest;
	std::unique_ptr<PickedItem> selectedItem;
	bool finished = false;
};

// Draws one controller-first setup page. The embedding UWP host starts the
// requested picker and performs the selected filesystem operation; it reports
// progress/completion back through SetupWizard. This deliberately prevents the
// view and the host from starting the same operation twice.
[[nodiscard]] SetupWizardViewResult DrawSetupWizard(SetupWizard& wizard,
	float deltaSeconds, bool english = false);
}
