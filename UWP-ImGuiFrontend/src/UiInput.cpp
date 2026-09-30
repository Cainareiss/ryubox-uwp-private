#include "UwpImGuiFrontend/UiInput.h"

#include <algorithm>
#include <cmath>

namespace UwpImGuiFrontend
{
namespace
{
UiInput Convert(const FrameInput& input) noexcept
{
	constexpr float deadzone = 0.55f;
	return {
		.confirm = input.accept,
		.cancel = input.back,
		.actionX = input.context,
		.actionY = input.alternate,
		.left = input.left || input.leftStickX <= -deadzone,
		.right = input.right || input.leftStickX >= deadzone,
		.up = input.up || input.leftStickY >= deadzone,
		.down = input.down || input.leftStickY <= -deadzone,
		.lb = input.leftShoulder,
		.rb = input.rightShoulder,
		.start = input.menu,
	};
}
}

UiInputEvents UiInputTracker::Update(const FrameInput& frame) noexcept
{
	const float delta = std::clamp(frame.deltaSeconds, 0.0f, 0.1f);
	const UiInput current = Convert(frame);
	UiInputEvents result;
	if (m_blockedSeconds > 0.0f || frame.controllerInputHeld)
	{
		m_blockedSeconds = std::max(0.0f, m_blockedSeconds - delta);
		m_previous = current;
		return result;
	}

	const auto pressed = [](bool value, bool previous) { return value && !previous; };
	result.confirmPressed = pressed(current.confirm, m_previous.confirm);
	result.cancelPressed = pressed(current.cancel, m_previous.cancel);
	result.actionXPressed = pressed(current.actionX, m_previous.actionX);
	result.actionYPressed = pressed(current.actionY, m_previous.actionY);
	result.lbPressed = pressed(current.lb, m_previous.lb);
	result.rbPressed = pressed(current.rb, m_previous.rb);
	result.startPressed = pressed(current.start, m_previous.start);
	result.leftPressed = Repeat(current.left, m_previous.left, m_leftHeld, delta);
	result.rightPressed = Repeat(current.right, m_previous.right, m_rightHeld, delta);
	result.upPressed = Repeat(current.up, m_previous.up, m_upHeld, delta);
	result.downPressed = Repeat(current.down, m_previous.down, m_downHeld, delta);
	m_previous = current;
	return result;
}

void UiInputTracker::BlockFor(float seconds) noexcept
{
	m_blockedSeconds = std::max(m_blockedSeconds, std::max(0.0f, seconds));
}

void UiInputTracker::Reset() noexcept
{
	*this = {};
}

bool UiInputTracker::Repeat(bool current, bool previous, float& held, float delta) noexcept
{
	constexpr float initialDelay = 0.30f;
	constexpr float repeatInterval = 0.10f;
	if (!current)
	{
		held = 0.0f;
		return false;
	}
	if (!previous)
	{
		held = 0.0f;
		return true;
	}
	const float before = held;
	held += delta;
	if (held < initialDelay)
		return false;
	const int oldRepeat = before < initialDelay ? -1 : static_cast<int>(std::floor((before - initialDelay) / repeatInterval));
	const int newRepeat = static_cast<int>(std::floor((held - initialDelay) / repeatInterval));
	return newRepeat > oldRepeat;
}
}
