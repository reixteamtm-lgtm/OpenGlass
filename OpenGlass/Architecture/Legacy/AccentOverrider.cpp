#include "pch.h"
#include "AccentOverrider.hpp"
#include "GlassKernel.hpp"
#include "GlassInfra.hpp"
#include "uDwmProjection.hpp"
#include "dwmcoreProjection.hpp"
#include "Shared.hpp"
#include "GlassEffectBrush.hpp"
#include "GlassReflectionBrush.hpp"
#include "GlassFrameEnhancer.hpp"
#include <atomic>
#include <cstdarg>

using namespace OpenGlass;
namespace OpenGlass::AccentOverrider
{
	// Diagnostics: visible with Sysinternals DebugView (run as administrator, Capture Global Win32).
	std::atomic<int> g_diagBudget{ 400 };
	void DiagLog(const wchar_t* format, ...)
	{
		if (g_diagBudget.fetch_sub(1) <= 0)
		{
			return;
		}
		wchar_t buffer[512]{};
		va_list args;
		va_start(args, format);
		_vsnwprintf_s(buffer, _TRUNCATE, format, args);
		va_end(args);
		OutputDebugStringW(buffer);
	}
	bool IsTaskbarVisual(uDWM::CVisual* visual)
	{
		if (!Shared::g_overrideAccent)
		{
			return false;
		}
		const auto window = uDWM::TryGetWindowFromVisual(visual);
		const auto data = window ? window->GetData() : nullptr;
		const HWND hwnd = data ? data->GetHwnd() : nullptr;
		if (!hwnd)
		{
			return false;
		}
		wchar_t className[64]{};
		GetClassNameW(hwnd, className, ARRAYSIZE(className));
		return !_wcsicmp(className, L"Shell_TrayWnd") || !_wcsicmp(className, L"Shell_SecondaryTrayWnd");
	}
	void DiagWindow(const wchar_t* where, HWND hwnd, UINT state, const wchar_t* decision)
	{
		wchar_t className[128]{};
		if (hwnd)
		{
			GetClassNameW(hwnd, className, ARRAYSIZE(className));
		}
		DiagLog(L"[OG-Accent] %s hwnd=%p class=%s state=%u override=%d -> %s\n", where, hwnd, className, state, Shared::g_overrideAccent ? 1 : 0, decision);
	}

	HRESULT MyCAccent_CloneVisualTree_Pre19041(uDWM::CAccent* This, uDWM::CVisual** clone, bool option1, bool option2, bool option3);
	HRESULT MyCAccent_CloneVisualTree(uDWM::CAccent* This, uDWM::CVisual** clone, UINT options);
	HRESULT MyCAccent_UpdateAccentPolicy(
		uDWM::CAccent* This,
		LPCRECT rect,
		uDWM::ACCENT_POLICY* policy,
		uDWM::CBaseGeometryProxy* geometry
	);
	HRESULT MyCAccent__UpdateSolidFill(
		uDWM::CAccent* This,
		uDWM::CRenderDataVisual* visual,
		UINT color,
		const D2D1_RECT_F& rect,
		float opacity
	);
	void MyCAccent_SetClipRegion(
		uDWM::CAccent* This,
		uDWM::CBaseGeometryProxy* geometry
	);
	void MyCAccent_Destructor(
		uDWM::CAccent* This
	);
	Projection::Detour<uDWM::Symbol_CAccent_UpdateAccentPolicy, &MyCAccent_UpdateAccentPolicy> g_CAccent_UpdateAccentPolicy_Org{};
	Projection::Detour<uDWM::Symbol_CAccent__UpdateSolidFill, &MyCAccent__UpdateSolidFill> g_CAccent__UpdateSolidFill_Org{};
	Projection::Detour<uDWM::Symbol_CAccent_SetClipRegion, &MyCAccent_SetClipRegion> g_CAccent_SetClipRegion_Org{};
	Projection::Detour<uDWM::Symbol_CAccent__CAccent, &MyCAccent_Destructor> g_CAccent_Destructor_Org{};
	Projection::Detour<uDWM::Symbol_CAccent_CloneVisualTree_Pre19041, &MyCAccent_CloneVisualTree_Pre19041> g_CAccent_CloneVisualTree_Pre19041_Org{};
	Projection::Detour<uDWM::Symbol_CAccent_CloneVisualTree, &MyCAccent_CloneVisualTree> g_CAccent_CloneVisualTree_Org{};

	struct CAccentInfo
	{
		winrt::com_ptr<uDWM::CBaseGeometryProxy> clipRegion;
		winrt::com_ptr<uDWM::CRgnGeometryProxy> drawRegion;
		winrt::com_ptr<uDWM::CRenderDataVisual> visual;
		bool visualIsAccent{};
		bool clipRegionWithheld{};
	};
	std::unordered_map<uDWM::CAccent*, CAccentInfo> g_accentClipRegions{};

	HRESULT UpdateDrawGeometry(const CAccentInfo& info, uDWM::CDrawGeometryInstruction* instruction)
	{
		auto& geometry = instruction->GetGeometry();
		if (geometry)
		{
			geometry->Release();
			geometry = nullptr;
		}
		if (info.clipRegion)
		{
			winrt::com_ptr<uDWM::CCombinedGeometryProxy> combined;
			RETURN_IF_FAILED(uDWM::ResourceHelper::CreateCombinedGeometry(
				info.clipRegion.get(), info.drawRegion.get(), D2D1_COMBINE_MODE_INTERSECT, combined.put()));
			geometry = combined.detach();
		}
		else
		{
			geometry = info.drawRegion.get();
			geometry->AddRef();
		}
		return S_OK;
	}

	void RestoreNativeClip(uDWM::CAccent* accent, const CAccentInfo& info)
	{
		// A fill update can create this record without intercepting SetClipRegion.
		if (info.clipRegionWithheld)
		{
			g_CAccent_SetClipRegion_Org(accent, info.clipRegion.get());
		}
	}
}

HRESULT AccentOverrider::MyCAccent_CloneVisualTree_Pre19041(uDWM::CAccent* This, uDWM::CVisual** clone, bool option1, bool option2, bool option3)
{
	const auto overridden = g_accentClipRegions.contains(This) && This->GetAccentPolicy().AccentState == 1;
	if (overridden && !GlassFrameEnhancer::IsGlassOnlyLivePreviewClone())
	{
		// Native Accent cloning recreates our transparent policy but loses its
		// replacement instructions. Capture the rendered subtree instead.
		*clone = nullptr;
		return This->CloneVisualTree_Pre19041(clone, option1, option2, option3);
	}
	HRESULT result;
	if (overridden)
	{
		// Native state-3 cloning omits the background but retains its optional
		// border/shadow atlas. Use that inventory for the glass-only pass.
		auto& policy = This->GetAccentPolicy();
		const auto state = policy.AccentState;
		policy.AccentState = 3;
		const auto restore = wil::scope_exit([&policy, state] { policy.AccentState = state; });
		result = g_CAccent_CloneVisualTree_Pre19041_Org(This, clone, option1, option2, option3);
	}
	else
	{
		result = g_CAccent_CloneVisualTree_Pre19041_Org(This, clone, option1, option2, option3);
	}
	if (SUCCEEDED(result))
	{
		static_cast<uDWM::CAccent*>(*clone)->GetParentVisible() = This->GetParentVisible();
	}
	return result;
}

HRESULT AccentOverrider::MyCAccent_CloneVisualTree(uDWM::CAccent* This, uDWM::CVisual** clone, UINT options)
{
	// Native LivePreview sets bit 3 for glass-only descendants.
	constexpr UINT glassOnlyCloneOption = 1u << 3;
	HRESULT result;
	if (
		uDWM::g_versionInfo.build >= os::build_w11_24h2 &&
		(options & glassOnlyCloneOption) &&
		g_accentClipRegions.contains(This) && This->GetAccentPolicy().AccentState == 1
	)
	{
		// 26100 clones replay the policy even for glass-only captures. Native
		// transparent-gradient state keeps the border/shadow flags without blur.
		auto& policy = This->GetAccentPolicy();
		const auto previousPolicy = policy;
		const auto restore = wil::scope_exit([&policy, previousPolicy] { policy = previousPolicy; });
		policy.AccentState = 2;
		policy.dwGradientColor = 0;
		policy.dwAnimationId = 0;
		result = g_CAccent_CloneVisualTree_Org(This, clone, options);
	}
	else
	{
		result = g_CAccent_CloneVisualTree_Org(This, clone, options);
	}
	if (SUCCEEDED(result))
	{
		// Native cloning omits the visibility gate used by acrylic background updates.
		static_cast<uDWM::CAccent*>(*clone)->GetParentVisible() = This->GetParentVisible();
	}
	return result;
}

HRESULT AccentOverrider::MyCAccent_UpdateAccentPolicy(
	uDWM::CAccent* This,
	LPCRECT rect,
	uDWM::ACCENT_POLICY* policy,
	uDWM::CBaseGeometryProxy* geometry
)
{
	// Windows 11: the taskbar draws its own XAML backdrop and only asks DWM for ACCENT_ENABLE_TRANSPARENTGRADIENT (2),
	// whatever TranslucentTB mode is chosen. Treat state 2 on the taskbar like blur so OpenGlass can put glass behind it
	// (TranslucentTB set to "Clear" hides the XAML backdrop so the glass is visible).
	const bool taskbarGradient = policy->AccentState == 2 && IsTaskbarVisual(This);
	const auto window = Shared::g_overrideAccent && (policy->AccentState == 3 || policy->AccentState == 4 || taskbarGradient) ?
		uDWM::TryGetWindowFromVisual(This) : nullptr;
	const auto data = window ? window->GetData() : nullptr;
	{
		const auto anyWindow = window ? window : uDWM::TryGetWindowFromVisual(This);
		const auto anyData = anyWindow ? anyWindow->GetData() : nullptr;
		const HWND hwnd = anyData ? anyData->GetHwnd() : nullptr;
		const wchar_t* decision =
			!Shared::g_overrideAccent ? L"skip: override off" :
			!(policy->AccentState == 3 || policy->AccentState == 4 || taskbarGradient) ? L"skip: state not 3/4" :
			!window ? L"skip: no top-level window" :
			!data ? L"skip: no window data" :
			!UxMitigation::AllowsAccentOverride(data->GetHwnd(), Shared::g_uxMitigationFlags) ? L"skip: mitigation" :
			L"OVERRIDE";
		DiagWindow(L"UpdateAccentPolicy", hwnd, policy->AccentState, decision);
	}
	if (!data || !UxMitigation::AllowsAccentOverride(data->GetHwnd(), Shared::g_uxMitigationFlags))
	{
		if (const auto it = g_accentClipRegions.find(This); it != g_accentClipRegions.end())
		{
			RestoreNativeClip(This, it->second);
			g_accentClipRegions.erase(it);
		}
		return g_CAccent_UpdateAccentPolicy_Org(This, rect, policy, geometry);
	}

	auto accentPolicy = *policy;
	accentPolicy.AccentState = 1;
	accentPolicy.dwGradientColor = 0;
	if (const auto it = g_accentClipRegions.find(This); it != g_accentClipRegions.end() && it->second.clipRegion)
	{
		// remove accent border atlas
		accentPolicy.AccentFlags &= ~((1 << 5) | (1 << 6) | (1 << 7) | (1 << 8));
	}
	if (taskbarGradient)
	{
		// The Windows 11 taskbar asks for a top border line; with glass (and the dock) it shows as a stray
		// full-width hairline, so drop the left/top/right/bottom border bits.
		accentPolicy.AccentFlags &= ~((1 << 5) | (1 << 6) | (1 << 7) | (1 << 8));
	}

	return g_CAccent_UpdateAccentPolicy_Org(This, rect, &accentPolicy, geometry);
}
HRESULT AccentOverrider::MyCAccent__UpdateSolidFill(
	uDWM::CAccent* This,
	uDWM::CRenderDataVisual* visual,
	UINT color,
	const D2D1_RECT_F& rect,
	float opacity
)
{
	const auto result = g_CAccent__UpdateSolidFill_Org(
		This,
		visual,
		color,
		rect,
		opacity
	);

	DiagLog(L"[OG-Accent] _UpdateSolidFill override=%d state=%u instructions=%d\n", Shared::g_overrideAccent ? 1 : 0, This->GetAccentPolicy().AccentState, visual && !visual->GetInstructions().views().empty() ? 1 : 0);
	if (!Shared::g_overrideAccent)
	{
		return result;
	}

	const auto& accentPolicy = This->GetAccentPolicy();
	if (accentPolicy.AccentState == 2)
	{
		return result;
	}

	const auto window = uDWM::TryGetWindowFromVisual(This);
	if (!window)
	{
		return result;
	}

	auto data = window->GetData();
	if (!data)
	{
		return result;
	}

	if (data->GetAccentPolicy()->AccentState == 1)
	{
		return result;
	}

	if (!UxMitigation::AllowsAccentOverride(data->GetHwnd(), Shared::g_uxMitigationFlags))
	{
		return result;
	}

	auto& accentInfo = g_accentClipRegions[This];
	// Native solid fill can draw on the Accent itself. Retaining it here
	// would prevent its destructor from retiring this cache entry.
	accentInfo.visualIsAccent = visual == This;
	accentInfo.visual.copy_from(accentInfo.visualIsAccent ? nullptr : visual);

	// indicates window should transition on maximized, so we can receive colorization updates
	// this make permanent change to the attribute during its lifetime, until a update to the attribute is made
	// is there any better way to implement it?
	data->GetClientBlurAttributeReference() |= 8;

	winrt::com_ptr<uDWM::CSolidColorLegacyMilBrushProxy> effectBrush{ nullptr };
	const auto status = GlassKernel::GetWindowStatus(window);
	const auto active = status.test(0);
	const auto maximized = status.test(1);

	if (!visual->GetInstructions().views().empty())
	{
		winrt::com_ptr<uDWM::CDrawGeometryInstruction> instruction{ nullptr };
		{
			const auto solidRectangleInstruction = reinterpret_cast<uDWM::CSolidRectangleInstruction*>(visual->GetInstructions().views().front());
			const auto drawRect = RectF::ToRectL(solidRectangleInstruction->GetRectangle());

			RETURN_IF_FAILED(
				uDWM::ResourceHelper::CreateGeometryFromHRGN(
					wil::unique_hrgn{ CreateRectRgnIndirect(&drawRect) }.get(),
					accentInfo.drawRegion.put()
				)
			);
			RETURN_IF_FAILED(
				uDWM::CDesktopManager::GetInstance()->GetCompositor()->CreateSolidColorLegacyMilBrushProxy(
					effectBrush.put()
				)
			);
			auto glassColor = Color::sRGBToscRGB(
				GlassKernel::RealizeWindowColorization(
					GlassKernel::GetBaseColor(Shared::IsTransparencyDisabled(), maximized),
					GlassKernel::GetSourceColor(active),
					GlassKernel::GetColorizationOpacity(active, maximized),
					Shared::IsTransparencyDisabled(),
					false
				).GetEffectivescRGBBlendColor(0.f),
				0.f
			);
			glassColor.a = GlassInfra::AlphaChannelReinterpreter(active, maximized).ToFloat();
			RETURN_IF_FAILED(effectBrush->Update(1.0, glassColor));

			RETURN_IF_FAILED(
				uDWM::CDrawGeometryInstruction::Create(
					effectBrush.get(),
					accentInfo.drawRegion.get(),
					instruction.put()
				)
			);
			RETURN_IF_FAILED(visual->ClearInstructions());
			RETURN_IF_FAILED(visual->AddInstruction(instruction.get()));
		}

		if (
			const auto brush = GlassReflectionBrush::GetOrCreate(
				window,
				2,
				true
			);
			brush
		)
		{
			RETURN_IF_FAILED(
				brush->Update(
					(Shared::g_reflectionPolicy & Shared::ReflectionPolicy::NonClient) ?
					GlassKernel::GetAdjustedReflectionIntensity(active, maximized) :
					0.f,
					GlassReflectionBrush::CalculateTargetViewport(
						visual->GetLocalToParentVisualOffset(window->GetTransformParent()),
						Shared::g_reflectionParallaxIntensity,
						window->IsRTLMirrored(),
						window->GetWidth(),
						visual->GetScale()
					),
					D2D1::RectF(),
					nullptr,
					DWM::MilBrushMappingMode::Absolute,
					DWM::MilBrushMappingMode::Absolute,
					nullptr,
					nullptr,
					DWM::MilStretch::None,
					DWM::MilTileMode::Extend,
					DWM::MilHorizontalAlignment::Left,
					DWM::MilVerticalAlignment::Top,
					nullptr
				)
			);
			RETURN_IF_FAILED(
				uDWM::CDrawGeometryInstruction::Create(
					brush.get(),
					accentInfo.drawRegion.get(),
					instruction.put()
				)
			);
			RETURN_IF_FAILED(visual->AddInstruction(instruction.get()));
		}
	}

	if (
		accentPolicy.IsClipRegionEffective() &&
		accentInfo.clipRegion
	)
	{
		for (auto& instruction : visual->GetInstructions().views())
		{
			RETURN_IF_FAILED(UpdateDrawGeometry(accentInfo, reinterpret_cast<uDWM::CDrawGeometryInstruction*>(instruction)));
		}
	}

	return result;
}
void AccentOverrider::MyCAccent_SetClipRegion(
	uDWM::CAccent* This,
	uDWM::CBaseGeometryProxy* geometry
)
{
	if (!Shared::g_overrideAccent)
	{
		return g_CAccent_SetClipRegion_Org(This, geometry);
	}

	const auto& accentPolicy = This->GetAccentPolicy();
	if (accentPolicy.AccentState == 2)
	{
		return g_CAccent_SetClipRegion_Org(This, geometry);
	}

	const auto window = uDWM::TryGetWindowFromVisual(This);
	if (!window)
	{
		return g_CAccent_SetClipRegion_Org(This, geometry);
	}

	auto data = window->GetData();
	if (!data)
	{
		return g_CAccent_SetClipRegion_Org(This, geometry);
	}

	if (data->GetAccentPolicy()->AccentState == 1)
	{
		return g_CAccent_SetClipRegion_Org(This, geometry);
	}

	if (!UxMitigation::AllowsAccentOverride(data->GetHwnd(), Shared::g_uxMitigationFlags))
	{
		// This native call applies the new clip; do not retain an older withheld one.
		g_accentClipRegions.erase(This);
		return g_CAccent_SetClipRegion_Org(This, geometry);
	}

	auto& accentInfo = g_accentClipRegions[This];
	accentInfo.clipRegion.copy_from(geometry);
	const auto visual = accentInfo.visualIsAccent ? This : accentInfo.visual.get();

	if (
		accentPolicy.IsClipRegionEffective() &&
		visual &&
		accentInfo.drawRegion
	)
	{
		for (auto& instruction : visual->GetInstructions().views())
		{
			THROW_IF_FAILED(UpdateDrawGeometry(accentInfo, reinterpret_cast<uDWM::CDrawGeometryInstruction*>(instruction)));
			THROW_IF_FAILED(visual->UpdateRenderData());
		}
	}
	accentInfo.clipRegionWithheld = true;
	return g_CAccent_SetClipRegion_Org(This, nullptr);
}
void AccentOverrider::MyCAccent_Destructor(
	uDWM::CAccent* This
)
{
	g_accentClipRegions.erase(This);
	return g_CAccent_Destructor_Org(This);
}
void AccentOverrider::Update(GlassEngine::UpdateType type)
{
	if (type & GlassEngine::UpdateType::Backdrop)
	{
		Shared::g_overrideAccent = static_cast<bool>(GlassEngine::GetDwordFromRegistry(L"GlassOverrideAccent"));
		DiagLog(L"[OG-Accent] Update: GlassOverrideAccent=%d uxFlags=0x%08X build=%u\n", Shared::g_overrideAccent ? 1 : 0, Shared::g_uxMitigationFlags, uDWM::g_versionInfo.build);
		for (auto it = g_accentClipRegions.begin(); it != g_accentClipRegions.end();)
		{
			const auto window = uDWM::TryGetWindowFromVisual(it->first);
			const auto data = window ? window->GetData() : nullptr;
			if (!Shared::g_overrideAccent || !data || !UxMitigation::AllowsAccentOverride(data->GetHwnd(), Shared::g_uxMitigationFlags))
			{
				RestoreNativeClip(it->first, it->second);
				it = g_accentClipRegions.erase(it);
			}
			else
			{
				++it;
			}
		}
	}
}

void AccentOverrider::Startup()
{
	if (Shared::g_disabledHooks.test(Shared::DisabledHooks_AccentOverrider))
	{
		DiagLog(L"[OG-Accent] Startup: AccentOverrider disabled by DisabledHooks\n");
		return;
	}
	DiagLog(L"[OG-Accent] Startup: installing CAccent hooks (build %u)\n", uDWM::g_versionInfo.build);

	HookHelper::ApplyInlineHooks(
		std::initializer_list<HookHelper::DetourInfo>
		{
			{ &g_CAccent_UpdateAccentPolicy_Org },
			{ &g_CAccent__UpdateSolidFill_Org },
			{ &g_CAccent_SetClipRegion_Org },
			{ &g_CAccent_Destructor_Org },
			{ &g_CAccent_CloneVisualTree_Pre19041_Org, uDWM::g_versionInfo.build < os::build_w10_2004 },
			{ &g_CAccent_CloneVisualTree_Org, uDWM::g_versionInfo.build >= os::build_w10_2004 },
		}
	);
}

void AccentOverrider::Shutdown()
{
	if (Shared::g_disabledHooks.test(Shared::DisabledHooks_AccentOverrider))
	{
		return;
	}

	HookHelper::ApplyInlineHooks(
		std::initializer_list<HookHelper::DetourInfo>
		{
			{ &g_CAccent_UpdateAccentPolicy_Org },
			{ &g_CAccent__UpdateSolidFill_Org },
			{ &g_CAccent_SetClipRegion_Org },
			{ &g_CAccent_Destructor_Org },
			{ &g_CAccent_CloneVisualTree_Pre19041_Org, uDWM::g_versionInfo.build < os::build_w10_2004 },
			{ &g_CAccent_CloneVisualTree_Org, uDWM::g_versionInfo.build >= os::build_w10_2004 },
		}
	);

}

void AccentOverrider::Cleanup()
{
	g_accentClipRegions.clear();
}
