#pragma once

#include "Input.h"

namespace UwpImGuiFrontend
{
// Retained, controller-first input used by modal flows.  FrameInput remains the
// platform boundary; this class turns it into deterministic edge/repeat events.
struct UiInput
{
	bool confirm = false;
	bool cancel = false;
	bool actionX = false;
	bool actionY = false;
	bool left = false;
	bool right = false;
	bool up = false;
	bool down = false;
	bool lb = false;
	bool rb = false;
	bool start = false;
};

struct UiInputEvents
{
	bool confirmPressed = false;
	bool cancelPressed = false;
	bool actionXPressed = false;
	bool actionYPressed = false;
	bool leftPressed = false;
	bool rightPressed = false;
	bool upPressed = false;
	bool downPressed = false;
	bool lbPressed = false;
	bool rbPressed = false;
	bool startPressed = false;
};

class UiInputTracker
{
public:
	[[nodiscard]] UiInputEvents Update(const FrameInput& input) noexcept;
	void BlockFor(float seconds) noexcept;
	void Reset() noexcept;

private:
	[[nodiscard]] bool Repeat(bool current, bool previous, float& held, float delta) noexcept;

	UiInput m_previous{};
	float m_leftHeld = 0.0f;
	float m_rightHeld = 0.0f;
	float m_upHeld = 0.0f;
	float m_downHeld = 0.0f;
	float m_blockedSeconds = 0.0f;
};
}
