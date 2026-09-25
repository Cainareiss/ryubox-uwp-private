#include "UwpImGuiFrontend/ShellRenderer.h"
#include "UwpImGuiFrontend/Text.h"
#include "UwpImGuiFrontend/Views.h"
#include "UwpImGuiFrontend/Widgets.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <ctime>
#include <map>
#include <random>
#include <string_view>
#include <utility>

namespace UwpImGuiFrontend
{
namespace
{
ImU32 Color(ImVec4 color)
{
	return ImGui::ColorConvertFloat4ToU32(color);
}

#if IMGUI_VERSION_NUM >= 19200
ImTextureRef TextureReference(std::uintptr_t id)
{
	return ImTextureRef(static_cast<ImTextureID>(id));
}
#else
ImTextureID TextureReference(std::uintptr_t id)
{
	return reinterpret_cast<ImTextureID>(id);
}
#endif

ImVec2 TextSizeUnits(const ShellLayoutContext& context, float sizeUnits,
	std::string_view text, ImFont* font = nullptr)
{
	if (text.empty())
		return {};
	font = font ? font : ImGui::GetFont();
	const ImVec2 pixels = font->CalcTextSizeA(sizeUnits * context.uiScale,
		FLT_MAX, 0.0f, text.data(), text.data() + text.size());
	return { pixels.x / context.uiScale, pixels.y / context.uiScale };
}

void DrawText(ImDrawList* draw, const ShellLayoutContext& context, float sizeUnits,
	ImVec2 positionUnits, ImU32 color, std::string_view text, ImFont* font = nullptr)
{
	if (!draw || text.empty())
		return;
	font = font ? font : ImGui::GetFont();
	draw->AddText(font, sizeUnits * context.uiScale,
		ToPx(context, positionUnits), color, text.data(), text.data() + text.size());
}

std::string Ellipsize(const ShellLayoutContext& context, float sizeUnits,
	std::string text, float maxWidth, ImFont* font = nullptr)
{
	if (TextSizeUnits(context, sizeUnits, text, font).x <= maxWidth)
		return text;
	constexpr std::string_view suffix = "...";
	while (!text.empty() && TextSizeUnits(context, sizeUnits,
		text + std::string(suffix), font).x > maxWidth)
	{
		std::size_t eraseAt = text.size() - 1;
		while (eraseAt > 0 && (static_cast<unsigned char>(text[eraseAt]) & 0xc0u) == 0x80u)
			--eraseAt;
		text.erase(eraseAt);
	}
	return text.empty() ? std::string(suffix) : text + std::string(suffix);
}

void DrawTexture(ImDrawList* draw, const ShellLayoutContext& context,
	TextureHandle texture, const UnitRect& rect, float radius, float alpha = 1.0f)
{
	if (!draw || !texture)
		return;
	ImVec2 min = RectMinPx(context, rect);
	ImVec2 max = RectMaxPx(context, rect);
	NormalizeRoundedRectPx(min, max);
	draw->AddImageRounded(TextureReference(texture.id), min, max,
		{ 0.0f, 0.0f }, { 1.0f, 1.0f },
		IM_COL32(255, 255, 255, static_cast<int>(std::clamp(alpha * 255.0f, 0.0f, 255.0f))),
		radius * context.uiScale, ImDrawFlags_RoundCornersAll);
}

void DrawTextureContained(ImDrawList* draw, const ShellLayoutContext& context,
	TextureHandle texture, const UnitRect& bounds, float radius, float alpha = 1.0f)
{
	if (!draw || !texture || texture.width == 0 || texture.height == 0)
		return;
	const float sourceAspect = static_cast<float>(texture.width) /
		static_cast<float>(texture.height);
	UnitRect destination = bounds;
	destination.w = std::min(bounds.w, bounds.h * sourceAspect);
	destination.h = std::min(bounds.h, destination.w / sourceAspect);
	if (destination.h < bounds.h && destination.w < bounds.w)
	{
		destination.h = bounds.h;
		destination.w = std::min(bounds.w, destination.h * sourceAspect);
	}
	destination.x = bounds.x + (bounds.w - destination.w) * 0.5f;
	destination.y = bounds.y + (bounds.h - destination.h) * 0.5f;
	DrawTexture(draw, context, texture, destination, radius, alpha);
}

void DrawTextureCover(ImDrawList* draw, TextureHandle texture, ImVec2 min, ImVec2 max,
	float alpha = 1.0f)
{
	if (!draw || !texture || max.x <= min.x || max.y <= min.y)
		return;
	const float destinationAspect = (max.x - min.x) / std::max(1.0f, max.y - min.y);
	const float sourceAspect = static_cast<float>(texture.width) /
		std::max(1.0f, static_cast<float>(texture.height));
	ImVec2 uv0{ 0.0f, 0.0f };
	ImVec2 uv1{ 1.0f, 1.0f };
	if (sourceAspect > destinationAspect)
	{
		const float crop = 1.0f - destinationAspect / sourceAspect;
		uv0.x = crop * 0.5f;
		uv1.x = 1.0f - crop * 0.5f;
	}
	else if (sourceAspect < destinationAspect)
	{
		const float crop = 1.0f - sourceAspect / destinationAspect;
		uv0.y = crop * 0.5f;
		uv1.y = 1.0f - crop * 0.5f;
	}
	draw->AddImage(TextureReference(texture.id), min, max, uv0, uv1,
		IM_COL32(255, 255, 255, static_cast<int>(std::clamp(alpha * 255.0f, 0.0f, 255.0f))));
}

std::pair<std::string, std::string> LocalClockText()
{
	const std::time_t now = std::time(nullptr);
	std::tm local{};
	localtime_s(&local, &now);
	std::array<char, 32> time{};
	std::array<char, 32> date{};
	std::strftime(time.data(), time.size(), "%H:%M", &local);
	std::strftime(date.data(), date.size(), "%d/%m/%Y", &local);
	return { time.data(), date.data() };
}

struct FloatingShape
{
	ImVec2 position{};
	float size = 20.0f;
	float speed = 10.0f;
	float phase = 0.0f;
	float wobble = 12.0f;
	float rotation = 0.0f;
	float rotationSpeed = 0.0f;
	int type = 0;
	float alpha = 0.08f;
};

const MediaAsset* FindPlatformMedia(
	const std::vector<MediaAsset>& media, MediaKind kind) noexcept
{
	const auto found = std::find_if(media.begin(), media.end(),
		[kind](const MediaAsset& asset) { return asset.kind == kind; });
	return found == media.end() ? nullptr : &*found;
}
}

class ShellRenderer::Impl
{
public:
	explicit Impl(IFrontendHost& host) : m_host(host) {}

	ImFont* FontOr(ImFont* role) const noexcept
	{
		return role ? role : (m_normalFont ? m_normalFont : ImGui::GetFont());
	}

	~Impl()
	{
		ClearTextures();
	}

	void ClearTextures()
	{
		m_host.StopVideoPlayback();
		m_launch = {};
		for (const auto& [path, texture] : m_textures)
		{
			if (texture)
				m_host.ReleaseTexture(texture);
		}
		m_textures.clear();
	}

	TextureHandle Texture(const std::filesystem::path& path, bool applicationAsset = false)
	{
		if (path.empty())
			return {};
		const std::filesystem::path resolved = applicationAsset && path.is_relative() ?
			m_host.ResourceRoot() / path : path;
		const auto existing = m_textures.find(resolved);
		if (existing != m_textures.end())
			return existing->second;
		return m_textures.emplace(resolved, m_host.LoadTexture(resolved)).first->second;
	}

	TextureHandle PreferredArtwork(const LibraryItem& item,
		const ShellPresentation& presentation)
	{
		if (const MediaAsset* media = FindMedia(item, presentation.listArtwork))
			return Texture(media->path);

		// Ryubox libraries are not guaranteed to have the same scraper media set.
		// The Premium carousel should still show a real game image when Cover3D is
		// absent instead of falling straight to the generic placeholder.
		constexpr std::array<MediaKind, 4> fallbacks = {
			MediaKind::Cover2D, MediaKind::Cover3D,
			MediaKind::TitleScreen, MediaKind::Screenshot,
		};
		for (const MediaKind kind : fallbacks)
		{
			if (kind == presentation.listArtwork)
				continue;
			if (const MediaAsset* media = FindMedia(item, kind))
				return Texture(media->path);
		}
		return {};
	}

	void DrawBackground(ImDrawList* draw, const ShellLayoutContext& context,
		Frontend& frontend, const ShellPresentation& presentation, const ShellTheme& theme,
		float deltaSeconds)
	{
		TextureHandle media;
		const LibraryItem* selected = nullptr;
		if (presentation.backgroundItem)
		{
			const auto found = std::find_if(frontend.Catalogue().begin(),
				frontend.Catalogue().end(), [&presentation](const LibraryItem& item) {
					return item.id == *presentation.backgroundItem;
				});
			if (found != frontend.Catalogue().end())
				selected = &*found;
		}
		else if (!frontend.Catalogue().empty() &&
			frontend.State().SelectedIndex() < frontend.Catalogue().size())
		{
			selected = &frontend.Catalogue()[frontend.State().SelectedIndex()];
		}

		const bool useThemeDynamicBackground =
			!presentation.overrideThemeDynamicBackground;
		const bool videoBackground = presentation.videoBackground ||
			(useThemeDynamicBackground && !presentation.backgroundArtwork &&
				theme.mediaMode == ShellThemeMediaMode::DynamicVideo);
		const std::optional<MediaKind> backgroundArtwork = presentation.backgroundArtwork ?
			presentation.backgroundArtwork :
			(useThemeDynamicBackground &&
				theme.mediaMode == ShellThemeMediaMode::DynamicBackground ?
				std::optional<MediaKind>(MediaKind::FanArt) : std::nullopt);
		if (videoBackground)
		{
			const MediaAsset* video = selected ? FindMedia(*selected, MediaKind::Video) : nullptr;
			if (!video && presentation.allowPlatformBackgroundFallback)
				video = FindPlatformMedia(presentation.platformMedia, MediaKind::Video);
			if (video)
				media = m_host.AcquireVideoFrame(video->path);
			if (!video)
				m_host.StopVideoPlayback();
		}
		else
		{
			m_host.StopVideoPlayback();
		}

		if (!media && backgroundArtwork)
		{
			if (selected)
			{
				if (const MediaAsset* artwork = FindMedia(*selected, *backgroundArtwork))
					media = Texture(artwork->path);
			}
			if (!media && presentation.allowPlatformBackgroundFallback)
			{
				if (const MediaAsset* artwork = FindPlatformMedia(
					presentation.platformMedia, *backgroundArtwork))
				{
					media = Texture(artwork->path);
				}
			}
		}

		const ImVec2 viewportMin = context.originPx;
		const ImVec2 viewportMax{ context.originPx.x + context.viewportPx.x,
			context.originPx.y + context.viewportPx.y };
		if (media)
		{
			DrawTextureCover(draw, media, viewportMin, viewportMax);
			draw->AddRectFilled(viewportMin, viewportMax,
				IM_COL32(0, 0, 0, videoBackground ? 112 : 96));
			draw->AddRectFilledMultiColor(viewportMin, viewportMax,
				IM_COL32(0, 0, 0, 92), IM_COL32(0, 0, 0, 92),
				IM_COL32(0, 0, 0, 176), IM_COL32(0, 0, 0, 176));
			return;
		}

		draw->AddRectFilledMultiColor(viewportMin, viewportMax,
			Color(theme.backgroundAccent), Color(theme.backgroundAccent),
			Color(theme.background), Color(theme.background));
		EnsureShapes(context);
		for (FloatingShape& shape : m_shapes)
		{
			shape.position.y -= shape.speed * deltaSeconds;
			shape.position.x += std::sin(static_cast<float>(ImGui::GetTime()) * 0.7f + shape.phase) *
				shape.wobble * deltaSeconds;
			shape.rotation += shape.rotationSpeed * deltaSeconds;
			if (shape.position.y + shape.size < -20.0f)
			{
				shape.position.y = context.layoutUnitsH + shape.size + 20.0f;
				shape.position.x = std::fmod(shape.position.x + 319.0f, context.layoutUnitsW);
			}
			ImVec4 color = theme.shapeColor;
			color.w = shape.alpha;
			DrawShape(draw, context, shape, Color(color));
		}
	}

	void EnsureShapes(const ShellLayoutContext& context)
	{
		const int targetCount = std::clamp(static_cast<int>(std::round(30.0f *
			std::sqrt((context.layoutUnitsW * context.layoutUnitsH) / (1280.0f * 720.0f)))),
			30, 56);
		if (static_cast<int>(m_shapes.size()) == targetCount &&
			std::abs(m_shapeArea.x - context.layoutUnitsW) < 8.0f &&
			std::abs(m_shapeArea.y - context.layoutUnitsH) < 8.0f)
		{
			return;
		}

		m_shapeArea = { context.layoutUnitsW, context.layoutUnitsH };
		m_shapes.clear();
		std::mt19937 random(0x55495046u);
		std::uniform_real_distribution<float> unit(0.0f, 1.0f);
		for (int index = 0; index < targetCount; ++index)
		{
			FloatingShape shape;
			shape.position = { unit(random) * context.layoutUnitsW,
				unit(random) * context.layoutUnitsH };
			shape.size = 14.0f + unit(random) * 40.0f;
			shape.speed = 6.0f + unit(random) * 22.0f;
			shape.phase = unit(random) * 6.2831853f;
			shape.wobble = 6.0f + unit(random) * 16.0f;
			shape.rotation = unit(random) * 6.2831853f;
			shape.rotationSpeed = (unit(random) > 0.5f ? 1.0f : -1.0f) *
				(0.18f + unit(random) * 0.32f);
			shape.type = index % 5;
			shape.alpha = 0.05f + unit(random) * 0.11f;
			m_shapes.push_back(shape);
		}
	}

	void DrawShape(ImDrawList* draw, const ShellLayoutContext& context,
		const FloatingShape& shape, ImU32 color)
	{
		const ImVec2 center = ToPx(context, shape.position);
		const float radius = shape.size * context.uiScale;
		if (shape.type == 0)
		{
			draw->AddCircleFilled(center, radius, color, 24);
			return;
		}
		const int pointCount = shape.type == 1 ? 3 : (shape.type == 4 ? 6 : 4);
		std::array<ImVec2, 6> points{};
		for (int index = 0; index < pointCount; ++index)
		{
			const float angle = shape.rotation + static_cast<float>(index) *
				(6.2831853f / static_cast<float>(pointCount));
			points[index] = { center.x + std::cos(angle) * radius,
				center.y + std::sin(angle) * radius };
		}
		draw->AddConvexPolyFilled(points.data(), pointCount, color);
	}

	void DrawTopHud(ImDrawList* draw, const ShellLayoutContext& context,
		const FrontendConfiguration& configuration, const ShellPresentation& presentation,
		const ShellTheme& theme)
	{
		// Premium V3 identity follows the supplied Eden reference: wordmark at
		// the left, a wide search field in the centre and clock/platform status
		// at the right. The portable sample's three floating cards looked like a
		// different product and made the PC/Xbox build diverge from the mock-up.
		const UnitRect logoArea{ 36.0f, 15.0f, 210.0f, 55.0f };
		DrawText(draw, context, 25.0f, { logoArea.x, logoArea.y },
			Color(theme.textPrimary), "RYU");
		const float ryuWidth = TextSizeUnits(context, 25.0f, "RYU").x;
		DrawText(draw, context, 25.0f, { logoArea.x + ryuWidth - 1.0f, logoArea.y },
			Color(theme.cursorNormal), "BOX");
		DrawText(draw, context, 8.5f, { logoArea.x, logoArea.y + 33.0f },
			Color(theme.textSecondary), configuration.emulationSubtitle);

		const UnitRect searchRect{ context.layoutUnitsW * 0.5f - 260.0f, 14.0f, 520.0f, 48.0f };
		DrawShellPanelPx(draw, RectMinPx(context, searchRect), RectMaxPx(context, searchRect),
			theme, 24.0f * context.uiScale, 0.82f);
		const ImVec2 searchCenter = ToPx(context,
			{ searchRect.x + 22.0f, searchRect.y + searchRect.h * 0.5f });
		draw->AddCircle(searchCenter, 5.0f * context.uiScale,
			Color(theme.textSecondary), 18, 1.2f * context.uiScale);
		draw->AddLine({ searchCenter.x + 3.5f * context.uiScale, searchCenter.y + 3.5f * context.uiScale },
			{ searchCenter.x + 7.0f * context.uiScale, searchCenter.y + 7.0f * context.uiScale },
			Color(theme.textSecondary), 1.2f * context.uiScale);
		DrawText(draw, context, 13.0f, { searchRect.x + 43.0f, searchRect.y + 15.0f },
			Color(theme.textSecondary), configuration.searchPlaceholder);

		const auto [time, date] = LocalClockText();
		const UnitRect rightArea{ context.layoutUnitsW - 244.0f, 13.0f, 208.0f, 55.0f };
		const ImVec2 timeSize = TextSizeUnits(context, 20.0f, time);
		DrawText(draw, context, 20.0f,
			{ rightArea.right() - timeSize.x, rightArea.y },
			Color(theme.textPrimary), time);
		const std::string platform = presentation.controllerStatus.empty()
			? configuration.platformName : presentation.controllerStatus;
		const ImVec2 platformSize = TextSizeUnits(context, 10.5f, platform);
		DrawText(draw, context, 10.5f,
			{ rightArea.right() - platformSize.x, rightArea.y + 32.0f },
			Color(theme.textSecondary), platform);

		const ImVec2 lineA = ToPx(context, { 0.0f, 78.0f });
		const ImVec2 lineB = ToPx(context, { context.layoutUnitsW, 78.0f });
		draw->AddLine(lineA, lineB, ColorWithAlpha(theme.panelBorder, 0.32f),
			1.0f * context.uiScale);
	}

	UnitRect RailRect(const ShellLayoutContext& context, bool left,
		std::size_t index, std::size_t count) const
	{
		constexpr float buttonSize = 70.0f;
		constexpr float gap = 16.0f;
		const float totalHeight = static_cast<float>(count) * buttonSize +
			static_cast<float>(count > 0 ? count - 1 : 0) * gap;
		return {
			left ? 14.0f : context.layoutUnitsW - 14.0f - buttonSize,
			(context.layoutUnitsH - totalHeight) * 0.5f + static_cast<float>(index) * (buttonSize + gap),
			buttonSize,
			buttonSize,
		};
	}

	void DrawRailFallbackIcon(ImDrawList* draw, const ShellLayoutContext& context,
		const UnitRect& rect, std::string_view id, ImU32 color)
	{
		const ImVec2 c = ToPx(context, { rect.x + rect.w * 0.5f, rect.y + rect.h * 0.5f });
		const float s = 14.0f * context.uiScale;
		const float w = 2.0f * context.uiScale;
		if (id == "home")
		{
			const ImVec2 roof[] = { {c.x-s,c.y},{c.x,c.y-s},{c.x+s,c.y} };
			draw->AddPolyline(roof, 3, color, 0, w);
			draw->AddRect({c.x-s*0.72f,c.y},{c.x+s*0.72f,c.y+s},color,2.0f*context.uiScale,0,w);
		}
		else if (id == "library")
		{
			for (int y = -1; y <= 1; ++y)
				for (int x = -1; x <= 1; ++x)
					draw->AddRectFilled({c.x+x*s*0.65f-2.0f*context.uiScale,c.y+y*s*0.65f-2.0f*context.uiScale},
						{c.x+x*s*0.65f+2.0f*context.uiScale,c.y+y*s*0.65f+2.0f*context.uiScale},color,1.0f);
		}
		else if (id == "settings")
		{
			draw->AddCircle(c, s * 0.62f, color, 18, w);
			draw->AddCircle(c, s * 0.22f, color, 18, w);
			for (int i = 0; i < 8; ++i)
			{
				const float a = static_cast<float>(i) * 3.14159265f / 4.0f;
				const ImVec2 a0{ c.x + std::cos(a) * s * 0.72f, c.y + std::sin(a) * s * 0.72f };
				const ImVec2 a1{ c.x + std::cos(a) * s, c.y + std::sin(a) * s };
				draw->AddLine(a0, a1, color, w);
			}
		}
		else if (id == "controllers")
		{
			draw->AddRect({c.x-s,c.y-s*0.45f},{c.x+s,c.y+s*0.55f},color,s*0.42f,0,w);
			draw->AddLine({c.x-s*0.55f,c.y},{c.x-s*0.15f,c.y},color,w);
			draw->AddLine({c.x-s*0.35f,c.y-s*0.2f},{c.x-s*0.35f,c.y+s*0.2f},color,w);
			draw->AddCircleFilled({c.x+s*0.42f,c.y-s*0.10f},2.2f*context.uiScale,color);
		}
		else if (id == "graphics")
		{
			draw->AddRect({c.x-s,c.y-s*0.68f},{c.x+s,c.y+s*0.55f},color,2.0f,0,w);
			draw->AddLine({c.x-s*0.55f,c.y+s},{c.x+s*0.55f,c.y+s},color,w);
			draw->AddLine({c.x,c.y+s*0.55f},{c.x,c.y+s},color,w);
		}
		else
		{
			draw->AddCircle(c,s*0.72f,color,28,w);
			draw->AddLine({c.x,c.y-s},{c.x,c.y},color,w);
		}
	}

	void DrawRail(ImDrawList* draw, const ShellLayoutContext& context, Frontend& frontend,
		const ShellTheme& theme, bool left)
	{
		const auto& actions = left ? frontend.Configuration().leftRail :
			frontend.Configuration().rightRail;
		for (std::size_t index = 0; index < actions.size(); ++index)
		{
			const UnitRect rect = RailRect(context, left, index, actions.size());
			const bool focused = frontend.FocusArea() ==
				(left ? ShellFocusArea::LeftRail : ShellFocusArea::RightRail) &&
				(left ? frontend.LeftRailIndex() : frontend.RightRailIndex()) == index;
			DrawShellPanelPx(draw, RectMinPx(context, rect), RectMaxPx(context, rect), theme,
				16.0f * context.uiScale, focused ? 1.0f : 0.88f);
			const TextureHandle icon = Texture(actions[index].icon, true);
			if (icon)
				DrawTexture(draw, context, icon, rect.Shrunk(6.0f), 12.0f, focused ? 1.0f : 0.92f);
			else
				DrawRailFallbackIcon(draw, context, rect, actions[index].id,
					Color(focused ? theme.textPrimary : theme.textSecondary));
			if (focused)
			{
				const ImVec2 labelSize = TextSizeUnits(context, 13.0f, actions[index].label);
				DrawText(draw, context, 13.0f,
					{ rect.x + (rect.w - labelSize.x) * 0.5f, rect.bottom() + 5.0f },
					Color(theme.textSecondary), actions[index].label);
			}
			if (!IsWidgetInputCaptured() &&
				ImGui::IsMouseClicked(ImGuiMouseButton_Left) &&
				rect.Contains(UnitPointFromPx(context, ImGui::GetIO().MousePos)))
			{
				const ShellFocusArea area = left ? ShellFocusArea::LeftRail :
					ShellFocusArea::RightRail;
				frontend.SetFocus(area, index);
				frontend.ActivateAction(area, index);
			}
		}
	}

	UnitRect GridRect(const ShellLayoutContext& context, std::size_t localIndex) const
	{
		const LayoutContext generic = MakeLayoutContext({ context.viewportPx.x, context.viewportPx.y });
		const GridLayout grid = MakeGridLayout(generic, 1);
		const float gridWidth = static_cast<float>(grid.columns) * grid.tileSize +
			static_cast<float>(grid.columns - 1) * grid.horizontalGap;
		const float gridHeight = static_cast<float>(grid.rows) * grid.tileSize +
			static_cast<float>(grid.rows - 1) * grid.verticalGap;
		const float originX = (context.layoutUnitsW - gridWidth) * 0.5f;
		const float originY = grid.band.y + (grid.band.height - gridHeight) * 0.5f;
		const std::size_t column = localIndex % grid.columns;
		const std::size_t row = localIndex / grid.columns;
		return {
			originX + static_cast<float>(column) * (grid.tileSize + grid.horizontalGap),
			originY + static_cast<float>(row) * (grid.tileSize + grid.verticalGap),
			grid.tileSize,
			grid.tileSize,
		};
	}


	UnitRect CarouselCenterRect(const ShellLayoutContext& context,
		const PremiumCarouselConfiguration& carousel) const
	{
		const float width = std::min(carousel.centerWidth,
			std::max(150.0f, context.layoutUnitsW * 0.22f));
		const float height = width * (carousel.centerHeight / std::max(1.0f, carousel.centerWidth));
		return {
			(context.layoutUnitsW - width) * 0.5f,
			std::max(108.0f, (context.layoutUnitsH - height) * 0.43f),
			width,
			height,
		};
	}

	struct CarouselCardQuad
	{
		std::array<ImVec2, 4> points{};
		ImVec2 min{};
		ImVec2 max{};
		float alpha = 1.0f;
		float scale = 1.0f;
	};

	CarouselCardQuad MakeCarouselCardQuad(const ShellLayoutContext& context,
		float distance, const PremiumCarouselConfiguration& carousel) const
	{
		const UnitRect center = CarouselCenterRect(context, carousel);
		const float depth = std::abs(distance);
		const float depthStrength = std::clamp(carousel.depthStrength, 0.5f, 1.5f);
		const float scale = std::max(carousel.minimumScale,
			1.0f - depth * carousel.sideScaleStep * depthStrength);
		const float width = center.w * scale;
		const float height = center.h * scale;
		const float x = center.x + center.w * 0.5f + distance * carousel.spacing - width * 0.5f;
		const float y = center.y + (center.h - height) * 0.5f +
			depth * carousel.verticalDepthStep * depthStrength;
		const float tilt = std::min(carousel.perspectiveTilt * depth * depthStrength,
			height * 0.16f);

		ImVec2 tl = ToPx(context, x, y);
		ImVec2 tr = ToPx(context, x + width, y);
		ImVec2 br = ToPx(context, x + width, y + height);
		ImVec2 bl = ToPx(context, x, y + height);
		const float tiltPx = tilt * context.uiScale;
		if (distance < -0.01f)
		{
			tl.y += tiltPx;
			bl.y -= tiltPx;
		}
		else if (distance > 0.01f)
		{
			tr.y += tiltPx;
			br.y -= tiltPx;
		}

		CarouselCardQuad result;
		result.points = { tl, tr, br, bl };
		result.min = {
			std::min({ tl.x, tr.x, br.x, bl.x }),
			std::min({ tl.y, tr.y, br.y, bl.y }),
		};
		result.max = {
			std::max({ tl.x, tr.x, br.x, bl.x }),
			std::max({ tl.y, tr.y, br.y, bl.y }),
		};
		result.alpha = std::clamp(1.0f - depth * 0.17f, 0.28f, 1.0f);
		result.scale = scale;
		return result;
	}

	void DrawCarouselArtwork(ImDrawList* draw, TextureHandle artwork,
		const CarouselCardQuad& quad, float alpha)
	{
		if (!draw || !artwork)
			return;
		draw->AddImageQuad(TextureReference(artwork.id),
			quad.points[0], quad.points[1], quad.points[2], quad.points[3],
			{ 0.0f, 0.0f }, { 1.0f, 0.0f }, { 1.0f, 1.0f }, { 0.0f, 1.0f },
			IM_COL32(255, 255, 255,
				static_cast<int>(std::clamp(alpha * 255.0f, 0.0f, 255.0f))));
	}

	void DrawCarouselReflection(ImDrawList* draw, TextureHandle artwork,
		const CarouselCardQuad& quad, float alpha, const ShellTheme& theme)
	{
		if (!draw || !artwork || alpha <= 0.001f)
			return;
		const float gap = 7.0f;
		const float reflectionHeight = (quad.max.y - quad.min.y) * 0.24f;
		const ImVec2 r0{ quad.points[3].x, quad.points[3].y + gap };
		const ImVec2 r1{ quad.points[2].x, quad.points[2].y + gap };
		const ImVec2 r2{ quad.points[2].x, quad.points[2].y + gap + reflectionHeight };
		const ImVec2 r3{ quad.points[3].x, quad.points[3].y + gap + reflectionHeight };
		draw->AddImageQuad(TextureReference(artwork.id), r0, r1, r2, r3,
			{ 0.0f, 1.0f }, { 1.0f, 1.0f }, { 1.0f, 0.70f }, { 0.0f, 0.70f },
			IM_COL32(255, 255, 255,
				static_cast<int>(std::clamp(alpha * 255.0f, 0.0f, 255.0f))));
		const float minX = std::min(r0.x, r3.x);
		const float maxX = std::max(r1.x, r2.x);
		const float topY = std::min(r0.y, r1.y);
		const float bottomY = std::max(r2.y, r3.y);
		const ImVec4 bg = theme.background;
		const int rr = static_cast<int>(std::clamp(bg.x * 255.0f, 0.0f, 255.0f));
		const int gg = static_cast<int>(std::clamp(bg.y * 255.0f, 0.0f, 255.0f));
		const int bb = static_cast<int>(std::clamp(bg.z * 255.0f, 0.0f, 255.0f));
		draw->AddRectFilledMultiColor({ minX, topY }, { maxX, bottomY },
			IM_COL32(rr, gg, bb, 80), IM_COL32(rr, gg, bb, 80),
			IM_COL32(rr, gg, bb, 250), IM_COL32(rr, gg, bb, 250));
	}

	void DrawCarouselPosition(ImDrawList* draw, const ShellLayoutContext& context,
		const Frontend& frontend, const ShellTheme& theme)
	{
		if (frontend.Catalogue().empty())
			return;
		const std::string text = std::to_string(frontend.State().SelectedIndex() + 1) +
			" / " + std::to_string(frontend.Catalogue().size());
		const ImVec2 size = TextSizeUnits(context, 12.5f, text);
		const UnitRect panel{ context.layoutUnitsW * 0.5f - (size.x + 30.0f) * 0.5f,
			context.layoutUnitsH - 36.0f, size.x + 30.0f, 25.0f };
		DrawShellPanelPx(draw, RectMinPx(context, panel), RectMaxPx(context, panel), theme,
			12.5f * context.uiScale, 0.72f);
		DrawText(draw, context, 12.5f,
			{ panel.x + (panel.w - size.x) * 0.5f, panel.y + 4.0f },
			Color(theme.textSecondary), text);
	}

	enum class HomeGlyph
	{
		Docked,
		Handheld,
		Resolution,
		Favorite,
		Update,
		Mods,
	};

	void DrawHomeGlyph(ImDrawList* draw, HomeGlyph glyph, ImVec2 center,
		float size, ImU32 color) const
	{
		const float line = std::max(1.4f, size * 0.11f);
		switch (glyph)
		{
		case HomeGlyph::Docked:
			draw->AddRect({ center.x - size * 0.58f, center.y - size * 0.42f },
				{ center.x + size * 0.58f, center.y + size * 0.32f }, color, size * 0.10f, 0, line);
			draw->AddLine({ center.x, center.y + size * 0.32f },
				{ center.x, center.y + size * 0.55f }, color, line);
			draw->AddLine({ center.x - size * 0.34f, center.y + size * 0.55f },
				{ center.x + size * 0.34f, center.y + size * 0.55f }, color, line);
			break;
		case HomeGlyph::Handheld:
			draw->AddRect({ center.x - size * 0.65f, center.y - size * 0.38f },
				{ center.x + size * 0.65f, center.y + size * 0.38f }, color, size * 0.24f, 0, line);
			draw->AddRect({ center.x - size * 0.34f, center.y - size * 0.29f },
				{ center.x + size * 0.34f, center.y + size * 0.29f }, color, size * 0.05f, 0, line);
			draw->AddCircleFilled({ center.x - size * 0.49f, center.y }, line, color);
			draw->AddCircleFilled({ center.x + size * 0.49f, center.y }, line, color);
			break;
		case HomeGlyph::Resolution:
			draw->AddRect({ center.x - size * 0.52f, center.y - size * 0.38f },
				{ center.x + size * 0.52f, center.y + size * 0.38f }, color, size * 0.08f, 0, line);
			draw->AddLine({ center.x - size * 0.34f, center.y + size * 0.18f },
				{ center.x + size * 0.30f, center.y - size * 0.18f }, color, line);
			break;
		case HomeGlyph::Favorite:
		{
			std::array<ImVec2, 10> star{};
			for (std::size_t index = 0; index < star.size(); ++index)
			{
				const float radius = index % 2 == 0 ? size * 0.58f : size * 0.25f;
				const float angle = -1.5707963f + static_cast<float>(index) * 0.6283185f;
				star[index] = { center.x + std::cos(angle) * radius,
					center.y + std::sin(angle) * radius };
			}
			draw->AddConvexPolyFilled(star.data(), static_cast<int>(star.size()), color);
			break;
		}
		case HomeGlyph::Update:
			draw->AddCircle(center, size * 0.46f, color, 18, line);
			draw->AddLine({ center.x, center.y - size * 0.24f },
				{ center.x, center.y + size * 0.20f }, color, line);
			draw->AddTriangleFilled({ center.x - size * 0.20f, center.y + size * 0.10f },
				{ center.x + size * 0.20f, center.y + size * 0.10f },
				{ center.x, center.y + size * 0.36f }, color);
			break;
		case HomeGlyph::Mods:
			draw->AddRectFilled({ center.x - size * 0.52f, center.y - size * 0.12f },
				{ center.x + size * 0.52f, center.y + size * 0.12f }, color, size * 0.08f);
			draw->AddRectFilled({ center.x - size * 0.12f, center.y - size * 0.52f },
				{ center.x + size * 0.12f, center.y + size * 0.52f }, color, size * 0.08f);
			break;
		}
	}

	float DrawHomeChip(ImDrawList* draw, const ShellLayoutContext& context,
		const ShellTheme& theme, float x, float y, HomeGlyph glyph,
		std::string_view label, bool accent = false) const
	{
		ImFont* font = FontOr(m_smallFont);
		const float textWidth = TextSizeUnits(context, 11.5f, label, font).x;
		const float width = textWidth + 43.0f;
		const UnitRect rect{ x, y, width, 27.0f };
		const ImU32 background = ColorWithAlpha(
			accent ? theme.cursorNormal : theme.panelBase, accent ? 0.34f : 0.86f);
		draw->AddRectFilled(RectMinPx(context, rect), RectMaxPx(context, rect),
			background, 13.5f * context.uiScale);
		draw->AddRect(RectMinPx(context, rect), RectMaxPx(context, rect),
			ColorWithAlpha(accent ? theme.cursorNormal : theme.panelBorder, 0.72f),
			13.5f * context.uiScale, 0, context.uiScale);
		DrawHomeGlyph(draw, glyph, ToPx(context, { x + 16.0f, y + 13.5f }),
			9.0f * context.uiScale, Color(theme.textPrimary));
		DrawText(draw, context, 11.5f, { x + 30.0f, y + 6.0f },
			Color(theme.textPrimary), label, font);
		return width;
	}

	void DrawHomeStatus(ImDrawList* draw, const ShellLayoutContext& context,
		const ShellTheme& theme, const LibraryItem& item) const
	{
		char scale[16]{};
		std::snprintf(scale, sizeof(scale), "%.1fx", item.resolutionScale);
		struct Chip { HomeGlyph glyph; std::string label; bool accent; };
		std::vector<Chip> chips;
		const bool handheld = item.consoleMode == "Handheld" || item.consoleMode == "Portable";
		chips.push_back({ handheld ? HomeGlyph::Handheld : HomeGlyph::Docked,
			handheld ? "PORTATIL" : "TV", false });
		chips.push_back({ HomeGlyph::Resolution, scale, false });
		if (item.favorite) chips.push_back({ HomeGlyph::Favorite, "FAVORITO", true });
		if (item.updateActive) chips.push_back({ HomeGlyph::Update, "UPDATE", true });
		if (item.modsActive) chips.push_back({ HomeGlyph::Mods, "MODS", true });

		float total = 0.0f;
		for (const Chip& chip : chips)
			total += TextSizeUnits(context, 11.5f, chip.label, FontOr(m_smallFont)).x + 51.0f;
		float x = std::max(92.0f, (context.layoutUnitsW - total) * 0.5f);
		for (const Chip& chip : chips)
			x += DrawHomeChip(draw, context, theme, x, context.layoutUnitsH - 151.0f,
				chip.glyph, chip.label, chip.accent) + 8.0f;
	}

	void DrawPremiumCarousel(ImDrawList* draw, const ShellLayoutContext& context,
		Frontend& frontend, const ShellPresentation& presentation, const ShellTheme& theme,
		float deltaSeconds)
	{
		const auto& catalogue = frontend.Catalogue();
		const PremiumCarouselConfiguration& carousel = frontend.Configuration().premiumCarousel;
		if (catalogue.empty())
		{
			const UnitRect card = CarouselCenterRect(context, carousel);
			DrawShellPanelPx(draw, RectMinPx(context, card), RectMaxPx(context, card), theme,
				18.0f * context.uiScale, 0.94f);
			TextureHandle placeholder = Texture(frontend.Configuration().placeholderArtwork, true);
			if (placeholder)
				DrawTextureContained(draw, context, placeholder, card.Shrunk(22.0f), 14.0f, 0.42f);
			const std::string add = frontend.Configuration().emptyCatalogueLabel;
			const ImVec2 textSize = TextSizeUnits(context, 18.0f, add);
			DrawText(draw, context, 18.0f,
				{ card.x + (card.w - textSize.x) * 0.5f, card.bottom() + 18.0f },
				Color(theme.textSecondary), add);
			if (!IsWidgetInputCaptured() && ImGui::IsMouseClicked(ImGuiMouseButton_Left) &&
				card.Contains(UnitPointFromPx(context, ImGui::GetIO().MousePos)))
			{
				m_host.Execute(HostCommand::AddContent);
			}
			DrawTitlePill(draw, context, frontend, theme, deltaSeconds);
			return;
		}

		const float selected = static_cast<float>(frontend.State().SelectedIndex());
		if (!m_carouselInitialized || !std::isfinite(m_carouselVisualIndex))
		{
			m_carouselVisualIndex = selected;
			m_carouselInitialized = true;
		}
		else
		{
			m_carouselVisualIndex = std::clamp(m_carouselVisualIndex, 0.0f,
				static_cast<float>(catalogue.size() - 1));
			const float snapDistance = std::max(8.0f,
				static_cast<float>(carousel.sideItems) * 2.0f + 2.0f);
			if (std::abs(selected - m_carouselVisualIndex) > snapDistance)
				m_carouselVisualIndex = selected;
		}
		const float response = 1.0f - std::exp(-std::max(1.0f, carousel.animationSpeed) * deltaSeconds);
		m_carouselVisualIndex += (selected - m_carouselVisualIndex) * response;
		if (std::abs(selected - m_carouselVisualIndex) < 0.001f)
			m_carouselVisualIndex = selected;

		std::vector<std::size_t> visible;
		const int radius = static_cast<int>(std::max<std::size_t>(carousel.sideItems, 1u)) + 2;
		const int centerIndex = static_cast<int>(std::floor(m_carouselVisualIndex));
		for (int candidate = centerIndex - radius; candidate <= centerIndex + radius; ++candidate)
		{
			if (candidate < 0 || candidate >= static_cast<int>(catalogue.size()))
				continue;
			const float distance = static_cast<float>(candidate) - m_carouselVisualIndex;
			if (std::abs(distance) <= static_cast<float>(carousel.sideItems) + 1.2f)
				visible.push_back(static_cast<std::size_t>(candidate));
		}
		std::sort(visible.begin(), visible.end(), [this](std::size_t a, std::size_t b) {
			return std::abs(static_cast<float>(a) - m_carouselVisualIndex) >
				std::abs(static_cast<float>(b) - m_carouselVisualIndex);
		});

		for (const std::size_t index : visible)
		{
			const float distance = static_cast<float>(index) - m_carouselVisualIndex;
			const CarouselCardQuad quad = MakeCarouselCardQuad(context, distance, carousel);
			const bool selectedCard = index == frontend.State().SelectedIndex();
			const bool focused = selectedCard && frontend.FocusArea() == ShellFocusArea::Catalogue;
			const float selectedBoost = selectedCard ? 1.0f : 0.88f;
			const float alpha = quad.alpha * selectedBoost;
			TextureHandle artwork = PreferredArtwork(catalogue[index], presentation);
			if (!artwork)
				artwork = Texture(frontend.Configuration().placeholderArtwork, true);

			// Shadow gives the flat ImGui quad a convincing floating-cover depth cue.
			std::array<ImVec2, 4> shadow = quad.points;
			for (ImVec2& p : shadow)
			{
				p.x += 8.0f * context.uiScale;
				p.y += 12.0f * context.uiScale;
			}
			draw->AddQuadFilled(shadow[0], shadow[1], shadow[2], shadow[3],
				IM_COL32(0, 0, 0, static_cast<int>(96.0f * alpha)));

			if (carousel.selectedGlow && focused)
			{
				const ImU32 pink = ColorWithAlpha(theme.cursorNormal, 0.30f);
				const ImU32 cyan = ColorWithAlpha(theme.pageIndicatorActive, 0.24f);
				draw->AddPolyline(quad.points.data(), 4, pink, ImDrawFlags_Closed,
					8.0f * context.uiScale);
				draw->AddPolyline(quad.points.data(), 4, cyan, ImDrawFlags_Closed,
					3.5f * context.uiScale);
			}
			DrawCarouselArtwork(draw, artwork, quad, alpha);
			draw->AddPolyline(quad.points.data(), 4,
				selectedCard ? Color(theme.textPrimary) : ColorWithAlpha(theme.panelBorder, 0.72f),
				ImDrawFlags_Closed, (selectedCard ? 2.0f : 1.0f) * context.uiScale);

			if (carousel.reflections && std::abs(distance) <= 1.35f)
			{
				DrawCarouselReflection(draw, artwork, quad,
					carousel.reflectionStrength * alpha, theme);
			}

			const ImVec2 mouse = ImGui::GetIO().MousePos;
			const bool hovered = !IsWidgetInputCaptured() && mouse.x >= quad.min.x && mouse.x <= quad.max.x &&
				mouse.y >= quad.min.y && mouse.y <= quad.max.y;
			if (hovered && ImGui::IsMouseClicked(ImGuiMouseButton_Right))
			{
				frontend.Select(index);
				frontend.OpenContextMenu();
			}
			else if (hovered && ImGui::IsMouseClicked(ImGuiMouseButton_Left))
			{
				if (selectedCard)
					frontend.ActivateSelection();
				else
					frontend.Select(index);
			}
		}

		const LibraryItem& item = catalogue[frontend.State().SelectedIndex()];
		std::string subtitle;
		if (!item.metadata.developer.empty())
			subtitle = item.metadata.developer;
		if (!item.format.empty())
			subtitle += (subtitle.empty() ? "" : "  •  ") + item.format;
		if (!item.region.empty())
			subtitle += (subtitle.empty() ? "" : "  •  ") + item.region;
		if (!subtitle.empty())
		{
			const std::string fitted = Ellipsize(context, 12.5f, subtitle,
				std::max(200.0f, context.layoutUnitsW * 0.52f));
			const ImVec2 size = TextSizeUnits(context, 12.5f, fitted);
			DrawText(draw, context, 12.5f,
				{ (context.layoutUnitsW - size.x) * 0.5f, context.layoutUnitsH - 118.0f },
				Color(theme.textSecondary), fitted);
		}
		DrawHomeStatus(draw, context, theme, item);

		DrawTitlePill(draw, context, frontend, theme, deltaSeconds);
		if (carousel.showPositionIndicator)
			DrawCarouselPosition(draw, context, frontend, theme);
	}

	void BeginPendingLaunch(const ShellLayoutContext& context, Frontend& frontend,
		const ShellPresentation& presentation)
	{
		if (m_launch.active)
			return;
		const auto pending = frontend.PendingLaunchItem();
		if (!pending)
			return;
		const auto found = std::find_if(frontend.Catalogue().begin(),
			frontend.Catalogue().end(), [pending](const LibraryItem& item) {
				return item.id == *pending;
			});
		if (found == frontend.Catalogue().end())
		{
			(void)frontend.CommitPendingLaunch();
			return;
		}
		const std::size_t index = static_cast<std::size_t>(
			std::distance(frontend.Catalogue().begin(), found));
		m_launch.active = true;
		m_launch.committed = false;
		m_launch.timer = 0.0f;
		m_launch.from = frontend.Configuration().catalogueLayout == CatalogueLayoutMode::PremiumCarousel3D ?
			CarouselCenterRect(context, frontend.Configuration().premiumCarousel) :
			GridRect(context, index % frontend.State().ItemsPerPage());
		m_launch.texture = PreferredArtwork(*found, presentation);
	}

	void DrawLaunchAnimation(ImDrawList* draw,
		const ShellLayoutContext& context, Frontend& frontend,
		const ShellTheme& theme, float deltaSeconds)
	{
		if (!m_launch.active)
			return;
		m_launch.timer += deltaSeconds;
		constexpr float zoomDuration = 0.45f;
		constexpr float holdDuration = 0.35f;
		constexpr float fadeDuration = 0.25f;
		constexpr float blackDuration = 0.30f;
		const float targetSide = std::min(m_launch.from.w, m_launch.from.h) * 1.8f;
		const UnitRect target{
			(context.layoutUnitsW - targetSide) * 0.5f,
			(context.layoutUnitsH - targetSide) * 0.5f,
			targetSide,
			targetSide,
		};
		const float zoom = Ease(ShellEasing::OutCubic,
			std::clamp(m_launch.timer / zoomDuration, 0.0f, 1.0f));
		const UnitRect rect{
			m_launch.from.x + (target.x - m_launch.from.x) * zoom,
			m_launch.from.y + (target.y - m_launch.from.y) * zoom,
			m_launch.from.w + (target.w - m_launch.from.w) * zoom,
			m_launch.from.h + (target.h - m_launch.from.h) * zoom,
		};
		const float fadeStart = zoomDuration + holdDuration;
		const float fade = std::clamp(
			(m_launch.timer - fadeStart) / fadeDuration, 0.0f, 1.0f);
		const ImVec2 viewportMax{
			context.originPx.x + context.viewportPx.x,
			context.originPx.y + context.viewportPx.y,
		};
		draw->AddRectFilled(context.originPx, viewportMax,
			IM_COL32(0, 0, 0, static_cast<int>(128.0f * zoom)));
		if (m_launch.texture)
			DrawTexture(draw, context, m_launch.texture, rect, 24.0f, 1.0f - fade);
		else
		{
			DrawShellPanelPx(draw, RectMinPx(context, rect), RectMaxPx(context, rect),
				theme, 24.0f * context.uiScale, 1.0f - fade);
		}
		if (!m_launch.committed && m_launch.timer >= fadeStart)
		{
			m_launch.committed = true;
			(void)frontend.CommitPendingLaunch();
		}
		const float black = std::clamp(
			(m_launch.timer - fadeStart - fadeDuration) / blackDuration,
			0.0f, 1.0f);
		if (black > 0.0f)
		{
			draw->AddRectFilled(context.originPx, viewportMax,
				IM_COL32(0, 0, 0, static_cast<int>(255.0f * black)));
		}
		if (m_launch.timer > fadeStart + fadeDuration + blackDuration + 0.15f)
			m_launch = {};
	}

	void DrawCatalogue(ImDrawList* draw, const ShellLayoutContext& context,
		Frontend& frontend, const ShellPresentation& presentation, const ShellTheme& theme,
		float deltaSeconds)
	{
		const std::size_t page = frontend.State().SelectedPage();
		if (page != m_lastPage)
		{
			m_lastPage = page;
			m_pageAppearTime = 0.0f;
		}
		m_pageAppearTime += deltaSeconds;

		const std::size_t pageSize = frontend.State().ItemsPerPage();
		const std::size_t first = page * pageSize;
		const std::size_t visibleCount = frontend.Catalogue().empty() ? 1 :
			std::min(pageSize, frontend.Catalogue().size() - std::min(first, frontend.Catalogue().size()));
		for (std::size_t localIndex = 0; localIndex < visibleCount; ++localIndex)
		{
			const std::size_t globalIndex = first + localIndex;
			const bool hasItem = globalIndex < frontend.Catalogue().size();
			const bool focused = frontend.FocusArea() == ShellFocusArea::Catalogue &&
				frontend.State().SelectedIndex() == globalIndex;
			float& focusAmount = m_focusAmounts[globalIndex];
			focusAmount += ((focused ? 1.0f : 0.0f) - focusAmount) *
				std::clamp(deltaSeconds * (focused ? 10.0f : 8.0f), 0.0f, 1.0f);

			const std::size_t column = localIndex % frontend.State().Columns();
			const std::size_t row = localIndex / frontend.State().Columns();
			const float delay = static_cast<float>(column + row) /
				static_cast<float>((frontend.State().Columns() - 1) + (frontend.State().Rows() - 1)) * 0.40f;
			const float appear = Ease(ShellEasing::OutExpo,
				std::clamp((m_pageAppearTime - delay) / 0.40f, 0.0f, 1.0f));
			if (appear <= 0.01f)
				continue;

			const UnitRect base = GridRect(context, localIndex);
			const float focusScale = std::clamp(
				Ease(ShellEasing::OutBack, focusAmount), 0.0f, 1.0f);
			const float scale = appear * (1.0f + 0.20f * focusScale);
			UnitRect rect = base;
			rect.w *= scale;
			rect.h *= scale;
			rect.x += (base.w - rect.w) * 0.5f;
			rect.y += (base.h - rect.h) * 0.5f;
			DrawShellPanelPx(draw, RectMinPx(context, rect), RectMaxPx(context, rect), theme,
				16.0f * context.uiScale, appear);

			TextureHandle artwork;
			if (hasItem)
				artwork = PreferredArtwork(frontend.Catalogue()[globalIndex], presentation);
			if (!artwork && !hasItem)
				artwork = Texture(frontend.Configuration().placeholderArtwork, true);
			if (artwork)
				DrawTextureContained(draw, context, artwork, rect.Shrunk(hasItem ? 8.0f : 18.0f),
					13.0f, hasItem ? appear : appear * 0.42f);
			else if (!hasItem)
			{
				const ImVec2 plusSize = TextSizeUnits(context, 42.0f, "+");
				DrawText(draw, context, 42.0f,
					{ rect.x + (rect.w - plusSize.x) * 0.5f,
					  rect.y + (rect.h - plusSize.y) * 0.5f },
					ColorWithAlpha(theme.textSecondary, 0.55f * appear), "+");
			}

			const bool hovered = !IsWidgetInputCaptured() &&
				base.Contains(UnitPointFromPx(context, ImGui::GetIO().MousePos));
			if (hovered && ImGui::IsMouseClicked(ImGuiMouseButton_Right) && hasItem)
			{
				frontend.Select(globalIndex);
				frontend.OpenContextMenu();
			}
			else if (hovered && ImGui::IsMouseClicked(ImGuiMouseButton_Left))
			{
				frontend.Select(globalIndex);
				if (hasItem)
					frontend.ActivateSelection();
				else
					m_host.Execute(HostCommand::AddContent);
			}
		}

		DrawTitlePill(draw, context, frontend, theme, deltaSeconds);
		DrawPageIndicator(draw, context, frontend.State().SelectedPage(),
			frontend.State().PageCount(), theme);
	}

	void DrawTitlePill(ImDrawList* draw, const ShellLayoutContext& context,
		const Frontend& frontend, const ShellTheme& theme, float deltaSeconds)
	{
		std::string title = frontend.Configuration().emptyCatalogueLabel;
		if (frontend.FocusArea() == ShellFocusArea::LeftRail &&
			frontend.LeftRailIndex() < frontend.Configuration().leftRail.size())
		{
			title = frontend.Configuration().leftRail[frontend.LeftRailIndex()].label;
		}
		else if (frontend.FocusArea() == ShellFocusArea::RightRail &&
			frontend.RightRailIndex() < frontend.Configuration().rightRail.size())
		{
			title = frontend.Configuration().rightRail[frontend.RightRailIndex()].label;
		}
		else if (!frontend.Catalogue().empty() &&
			frontend.State().SelectedIndex() < frontend.Catalogue().size())
		{
			title = frontend.Catalogue()[frontend.State().SelectedIndex()].name;
		}
		const bool changed = title != m_title;
		if (changed)
		{
			m_title = title;
			m_titleReveal.SetImmediate(0.32f);
			m_titleReveal.Set(1.0f, 0.18f);
		}
		constexpr float padding = 48.0f;
		const float maximumWidth = std::max(54.0f, context.layoutUnitsW - 64.0f);
		ImFont* titleFont = FontOr(m_titleFont);
		const float naturalWidth = std::ceil(TextSizeUnits(context, 24.0f, title, titleFont).x) + padding;
		const float targetWidth = std::min(maximumWidth, naturalWidth);
		const float targetX = (context.layoutUnitsW - targetWidth) * 0.5f;
		if (m_titleWidth.value <= 0.5f)
		{
			m_titleWidth.SetImmediate(std::min(targetWidth, 54.0f));
			m_titleX.SetImmediate((context.layoutUnitsW - m_titleWidth.value) * 0.5f);
		}
		if (changed || std::abs(m_titleWidth.target - targetWidth) >= 0.01f ||
			std::abs(m_titleX.target - targetX) >= 0.01f)
		{
			m_titleWidth.Set(targetWidth, 0.22f);
			m_titleX.Set(targetX, 0.22f);
		}
		m_titleWidth.Update(deltaSeconds);
		m_titleX.Update(deltaSeconds);
		m_titleReveal.Update(deltaSeconds);
		const UnitRect rect{ m_titleX.value, context.layoutUnitsH - 90.0f,
			m_titleWidth.value, 42.0f };
		DrawShellPanelPx(draw, RectMinPx(context, rect), RectMaxPx(context, rect), theme,
			21.0f * context.uiScale, 0.92f);
		const std::string fitted = Ellipsize(context, 24.0f, title,
			std::max(4.0f, rect.w - padding), titleFont);
		const ImVec2 textSize = TextSizeUnits(context, 24.0f, fitted, titleFont);
		draw->PushClipRect(RectMinPx(context, rect), RectMaxPx(context, rect), true);
		DrawText(draw, context, 24.0f,
			{ rect.x + (rect.w - textSize.x) * 0.5f,
			  rect.y + (rect.h - textSize.y) * 0.5f +
				  (1.0f - m_titleReveal.value) * 3.0f },
			ColorWithAlpha(theme.textPrimary, m_titleReveal.value), fitted, titleFont);
		draw->PopClipRect();
	}

	void DrawPageIndicator(ImDrawList* draw, const ShellLayoutContext& context,
		std::size_t currentPage, std::size_t pageCount, const ShellTheme& theme)
	{
		if (pageCount <= 1)
			return;
		constexpr float dotRadius = 4.0f;
		constexpr float gap = 14.0f;
		const float dotsWidth = static_cast<float>(pageCount) * dotRadius * 2.0f +
			static_cast<float>(pageCount - 1) * gap;
		const UnitRect panel{ (context.layoutUnitsW - (dotsWidth + 36.0f)) * 0.5f,
			context.layoutUnitsH - 35.0f, dotsWidth + 36.0f, 28.0f };
		DrawShellPanelPx(draw, RectMinPx(context, panel), RectMaxPx(context, panel), theme,
			14.0f * context.uiScale, 0.70f);
		for (std::size_t index = 0; index < pageCount; ++index)
		{
			const ImVec2 center = ToPx(context,
				panel.x + 18.0f + static_cast<float>(index) * (dotRadius * 2.0f + gap) + dotRadius,
				panel.y + panel.h * 0.5f);
			draw->AddCircleFilled(center, dotRadius * context.uiScale,
				index == currentPage ? Color(theme.pageIndicatorActive) : Color(theme.pageIndicator), 18);
		}
	}

	void DrawActionHints(ImDrawList* draw, const ShellLayoutContext& context,
		const FrontendConfiguration& configuration, const ShellTheme& theme)
	{
		if (configuration.actionHints.empty())
			return;
		constexpr float gap = 19.0f;
		float totalWidth = 0.0f;
		for (const ActionHint& hint : configuration.actionHints)
		{
			totalWidth += TextSizeUnits(context, 11.0f, hint.button).x +
				TextSizeUnits(context, 10.5f, hint.label).x + 18.0f + gap;
		}
		float x = (context.layoutUnitsW - totalWidth) * 0.5f;
		const float y = context.layoutUnitsH - 35.0f;
		for (const ActionHint& hint : configuration.actionHints)
		{
			const ImVec2 keySize = TextSizeUnits(context, 11.0f, hint.button);
			const UnitRect key{ x, y - 4.0f, keySize.x + 14.0f, 24.0f };
			DrawShellPanelPx(draw, RectMinPx(context, key), RectMaxPx(context, key), theme,
				6.0f * context.uiScale, 0.80f);
			DrawText(draw, context, 11.0f, { key.x + 7.0f, y }, Color(theme.textPrimary), hint.button);
			x = key.right() + 7.0f;
			DrawText(draw, context, 10.5f, { x, y + 1.0f }, Color(theme.textSecondary), hint.label);
			x += TextSizeUnits(context, 10.5f, hint.label).x + gap;
		}
	}

	void DrawCursor(ImDrawList* draw, const ShellLayoutContext& context,
		const Frontend& frontend, const ShellTheme& theme, float deltaSeconds)
	{
		UnitRect target;
		switch (frontend.FocusArea())
		{
		case ShellFocusArea::LeftRail:
			target = RailRect(context, true, frontend.LeftRailIndex(),
				frontend.Configuration().leftRail.size());
			break;
		case ShellFocusArea::RightRail:
			target = RailRect(context, false, frontend.RightRailIndex(),
				frontend.Configuration().rightRail.size());
			break;
		case ShellFocusArea::Catalogue:
		default:
			target = frontend.Configuration().catalogueLayout == CatalogueLayoutMode::PremiumCarousel3D ?
				CarouselCenterRect(context, frontend.Configuration().premiumCarousel) :
				GridRect(context, frontend.State().SelectedIndex() % frontend.State().ItemsPerPage());
			break;
		}
		m_cursor.MoveTo(target, 17.0f, 0.09f);
		m_cursor.Update(deltaSeconds);
		m_cursor.Draw(context, draw, theme);
	}

	void Draw(Frontend& frontend, const ShellPresentation& presentation,
		float deltaSeconds)
	{
		const ImGuiViewport* viewport = ImGui::GetMainViewport();
		if (!viewport)
			return;
		const ShellLayoutContext context = MakeShellLayout(viewport->WorkSize, viewport->WorkPos);
		const auto& themes = ShellThemes();
		const ShellTheme& theme = presentation.customTheme ? *presentation.customTheme :
			themes[std::clamp(presentation.themeIndex, 0,
				static_cast<int>(themes.size()) - 1)];

		ImGui::SetNextWindowPos(viewport->WorkPos);
		ImGui::SetNextWindowSize(viewport->WorkSize);
		constexpr ImGuiWindowFlags flags = ImGuiWindowFlags_NoDecoration |
			ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoSavedSettings |
			ImGuiWindowFlags_NoBringToFrontOnFocus | ImGuiWindowFlags_NoBackground;
		m_normalFont = ResolveFrontendTextFont(
			presentation.normalFont ? presentation.normalFont : presentation.font);
		m_mediumFont = ResolveFrontendTextFont(
			presentation.mediumFont ? presentation.mediumFont : m_normalFont);
		m_titleFont = ResolveFrontendTextFont(
			presentation.titleFont ? presentation.titleFont : m_mediumFont);
		m_smallFont = ResolveFrontendTextFont(
			presentation.smallFont ? presentation.smallFont : m_normalFont);
		ImFont* frontendFont = FontOr(m_normalFont);
		if (frontendFont)
			ImGui::PushFont(frontendFont);
		ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, { 0.0f, 0.0f });
		ImGui::Begin("##uwp-imgui-frontend", nullptr, flags);
		ImDrawList* draw = ImGui::GetWindowDrawList();
		const float dt = std::clamp(deltaSeconds, 0.0f, 1.0f / 15.0f);
		DrawBackground(draw, context, frontend, presentation, theme, dt);
		DrawTopHud(draw, context, frontend.Configuration(), presentation, theme);
		DrawRail(draw, context, frontend, theme, true);
		DrawRail(draw, context, frontend, theme, false);
		if (frontend.Configuration().catalogueLayout == CatalogueLayoutMode::PremiumCarousel3D)
			DrawPremiumCarousel(draw, context, frontend, presentation, theme, dt);
		else
			DrawCatalogue(draw, context, frontend, presentation, theme, dt);
		DrawCursor(draw, context, frontend, theme, dt);
		if (presentation.showActionHints)
			DrawActionHints(draw, context, frontend.Configuration(), theme);
		const auto notifications = frontend.Notifications();
		DrawNotificationCards(ImGui::GetForegroundDrawList(), context,
			theme, notifications);
		BeginPendingLaunch(context, frontend, presentation);
		DrawLaunchAnimation(draw, context, frontend, theme, dt);
		if (const LibraryItem* contextItem = frontend.ContextItem())
		{
			TitleOptionsModel model;
			model.title = contextItem->name;
			if (contextItem->contentId != 0)
			{
				char titleId[17]{};
				std::snprintf(titleId, sizeof(titleId), "%016llX",
					static_cast<unsigned long long>(contextItem->contentId));
				model.titleId = titleId;
			}
			model.subtitle = contextItem->platform;
			model.metadata = contextItem->metadata;
			model.actions = MakeDefaultTitleOptionActions(contextItem->favorite);
			char resolution[16]{};
			std::snprintf(resolution, sizeof(resolution), "%.1fx", contextItem->resolutionScale);
			model.information = {
				{ "Console mode", contextItem->consoleMode },
				{ "Resolution", resolution },
				{ "Update", contextItem->updateActive ? "Active" : "Not installed" },
				{ "Mods", contextItem->modsActive ? "Enabled" : "Disabled" },
			};
			if (const MediaAsset* cover = FindMedia(*contextItem, MediaKind::Cover2D))
				model.artwork = Texture(cover->path);
			const UnitRect panel = CenteredPanel(context, 1080.0f, 610.0f, 28.0f);
			const TitleOptionsResult options = DrawTitleOptionsView(context,
				ImGui::GetForegroundDrawList(), panel, m_titleOptions, model, frontendFont);
			if (options.closeRequested)
				frontend.CloseContextMenu();
			else if (options.activatedAction)
				frontend.ActivateContextAction(*options.activatedAction);
		}
		ImGui::End();
		ImGui::PopStyleVar();
		if (frontendFont)
			ImGui::PopFont();
	}

private:
	IFrontendHost& m_host;
	std::map<std::filesystem::path, TextureHandle> m_textures;
	std::vector<FloatingShape> m_shapes;
	ImVec2 m_shapeArea{};
	std::map<std::size_t, float> m_focusAmounts;
	TitleOptionsState m_titleOptions;
	ImFont* m_normalFont = nullptr;
	ImFont* m_mediumFont = nullptr;
	ImFont* m_titleFont = nullptr;
	ImFont* m_smallFont = nullptr;
	float m_carouselVisualIndex = 0.0f;
	bool m_carouselInitialized = false;
	std::size_t m_lastPage = static_cast<std::size_t>(-1);
	float m_pageAppearTime = 0.0f;
	std::string m_title;
	AnimatedFloat m_titleX;
	AnimatedFloat m_titleWidth;
	AnimatedFloat m_titleReveal;
	CursorAnimation m_cursor;
	struct LaunchAnimation
	{
		bool active = false;
		bool committed = false;
		float timer = 0.0f;
		UnitRect from;
		TextureHandle texture;
	} m_launch;
};

ShellRenderer::ShellRenderer(IFrontendHost& host)
	: m_impl(std::make_unique<Impl>(host))
{
}

ShellRenderer::~ShellRenderer() = default;
ShellRenderer::ShellRenderer(ShellRenderer&&) noexcept = default;
ShellRenderer& ShellRenderer::operator=(ShellRenderer&&) noexcept = default;

void ShellRenderer::Draw(Frontend& frontend, const ShellPresentation& presentation,
	float deltaSeconds)
{
	m_impl->Draw(frontend, presentation, deltaSeconds);
}

void ShellRenderer::ClearTextures()
{
	m_impl->ClearTextures();
}
}
