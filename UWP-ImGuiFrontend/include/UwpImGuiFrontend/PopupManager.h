#pragma once

#include <string>

namespace UwpImGuiFrontend
{
enum class PopupType
{
	None,
	Progress,
	Error,
	Confirmation,
	GameOptions,
	FilePickerResult,
	FirmwareInstall,
	KeysInstall,
};

// Enforces the frontend rule that only one blocking modal may own controller
// focus at a time. Rendering code consumes RequestFocusRestore once it has
// drawn the originating item.
class PopupManager
{
public:
	[[nodiscard]] bool Open(PopupType type, std::string id, std::string restoreFocusId = {});
	void Close() noexcept;
	void Reset() noexcept;
	[[nodiscard]] bool IsOpen() const noexcept { return m_type != PopupType::None; }
	[[nodiscard]] PopupType Type() const noexcept { return m_type; }
	[[nodiscard]] const std::string& Id() const noexcept { return m_id; }
	[[nodiscard]] bool RequestFocusRestore() noexcept;
	[[nodiscard]] const std::string& RestoreFocusId() const noexcept { return m_restoreFocusId; }

private:
	PopupType m_type = PopupType::None;
	std::string m_id;
	std::string m_restoreFocusId;
	bool m_restoreFocusRequested = false;
};
}
