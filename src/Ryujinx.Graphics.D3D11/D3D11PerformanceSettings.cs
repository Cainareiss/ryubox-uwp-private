using System;

namespace Ryujinx.Graphics.D3D11;

internal static class D3D11PerformanceSettings
{
    private const string DeferBarriersSwitch = "Ryujinx.Uwp.D3D11.DeferImplicitBarriers";

    internal static bool ForceSingleSample
    {
        get
        {
            // The UWP/Xbox compatibility presenter consumes a single-sample
            // frame. MSAA render targets are still valid for guest draws, but
            // their resolve/readback is not reliable on the restricted D3D11
            // path and can produce a completely transparent frame. Keep the
            // validated fallback on by default; developers can opt out for a
            // hardware path that provides a reliable MSAA resolve.
            return !string.Equals(
                Environment.GetEnvironmentVariable("RYUJINX_UWP_D3D11_FORCE_SINGLE_SAMPLE"),
                "0",
                StringComparison.Ordinal);
        }
    }

    internal static bool DeferImplicitBarriers
    {
        get
        {
            // Keep a strict mode for troubleshooting synchronization issues
            // without rebuilding the package.
            if (string.Equals(
                    Environment.GetEnvironmentVariable("RYUJINX_UWP_D3D11_STRICT_BARRIERS"),
                    "1",
                    StringComparison.Ordinal))
            {
                return false;
            }

            return AppContext.TryGetSwitch(DeferBarriersSwitch, out bool enabled) && enabled;
        }
    }
}
