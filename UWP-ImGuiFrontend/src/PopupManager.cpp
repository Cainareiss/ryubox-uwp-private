#include "UwpImGuiFrontend/PopupManager.h"

#include <utility>

namespace UwpImGuiFrontend
{
bool PopupManager::Open(PopupType type, std::string id, std::string restoreFocusId)
{
	if (type == PopupType::None || id.empty())
		return false;
	if (IsOpen())
		return m_type == type && m_id == id;
	m_type = type;
	m_id = std::move(id);
	m_restoreFocusId = std::move(restoreFocusId);
	m_restoreFocusRequested = false;
	return true;
}

void PopupManager::Close() noexcept
{
	if (!IsOpen())
		return;
	m_type = PopupType::None;
	m_id.clear();
	m_restoreFocusRequested = true;
}

void PopupManager::Reset() noexcept
{
	m_type = PopupType::None;
	m_id.clear();
	m_restoreFocusId.clear();
	m_restoreFocusRequested = false;
}

bool PopupManager::RequestFocusRestore() noexcept
{
	const bool value = m_restoreFocusRequested;
	m_restoreFocusRequested = false;
	return value;
}
}
