using System;
using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.GAL;

namespace Ryujinx.Graphics.D3D11;

internal sealed class D3D11Window : IWindow, IDisposable
{
	private readonly D3D11HostContext _host;

	private readonly D3D11Pipeline _pipeline;
	private readonly long _telemetrySessionId;

	private VSyncMode _vSyncMode;

	private AntiAliasing _antiAliasing;

	private ScalingFilter _scalingFilter;

	private float _scalingFilterLevel;

	private bool _colorSpacePassthrough;

	private long _presentSequence;

	private string _lastNativeCropLog;

	private string _lastNativeFlagsLog;

	private readonly bool _presentLastRenderTarget = string.Equals(Environment.GetEnvironmentVariable("RYUJINX_UWP_D3D11_PRESENT_LAST_RT"), "1", StringComparison.OrdinalIgnoreCase);

	private readonly bool _presentMatchingRenderTarget = string.Equals(Environment.GetEnvironmentVariable("RYUJINX_UWP_D3D11_PRESENT_MATCHING_RT"), "1", StringComparison.OrdinalIgnoreCase);

	private readonly (int Width, int Height) _presentTargetSize = ParsePresentTargetSize(Environment.GetEnvironmentVariable("RYUJINX_UWP_D3D11_PRESENT_TARGET_SIZE"));

	public D3D11Window(D3D11HostContext host, D3D11Pipeline pipeline)
	{
		_host = host;
		_pipeline = pipeline;
		_telemetrySessionId = UwpD3D11WindowTelemetry.ActiveSessionId;
	}

	private static (int Width, int Height) ParsePresentTargetSize(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return (Width: 0, Height: 0);
		}
		string[] array = value.Split('x', 'X');
		if (array.Length != 2 || !int.TryParse(array[0], out var result) || !int.TryParse(array[1], out var result2) || result <= 0 || result2 <= 0)
		{
			return (Width: 0, Height: 0);
		}
		return (Width: result, Height: result2);
	}

	public void Present(ITexture texture, ImageCrop crop, Action swapBuffersCallback)
	{
		UwpD3D11WindowTelemetry.Enter(_telemetrySessionId);
		nint textureResource = 0;
		try
		{
			D3D11Texture d3D11Texture2;
			if (texture is D3D11Texture d3D11Texture)
			{
				d3D11Texture2 = d3D11Texture;
				if (_presentTargetSize.Width > 0)
				{
					D3D11Texture lastDrawTarget = _pipeline.GetLastDrawTarget(_presentTargetSize.Width, _presentTargetSize.Height);
					if (lastDrawTarget != null && lastDrawTarget.Resource != 0)
					{
						d3D11Texture2 = lastDrawTarget;
						goto IL_00ae;
					}
				}
				if (_presentMatchingRenderTarget)
				{
					D3D11Texture lastDrawTarget2 = _pipeline.GetLastDrawTarget(d3D11Texture.Width, d3D11Texture.Height);
					if (lastDrawTarget2 != null && lastDrawTarget2.Resource != 0)
					{
						d3D11Texture2 = lastDrawTarget2;
						goto IL_00ae;
					}
				}
				if (_presentLastRenderTarget)
				{
					D3D11Texture lastDrawTarget3 = _pipeline.GetLastDrawTarget();
					if (lastDrawTarget3 != null && lastDrawTarget3.Resource != 0)
					{
						d3D11Texture2 = lastDrawTarget3;
					}
				}
				goto IL_00ae;
			}
			goto IL_0456;
			IL_0456:
			UwpD3D11WindowTelemetry.NativeFallback(_telemetrySessionId);
			_host.Present(textureResource, crop);
			swapBuffersCallback?.Invoke();
			return;
			IL_00ae:
			long num = ++_presentSequence;
			Ryujinx.Common.Logging.RuntimeGpuTelemetry.Frame();
			if (num <= 16 || (num & (num - 1)) == 0L || num % 1000 == 0L)
			{
				D3D11DebugTelemetry.Append($"window-present-source: sequence={num}, resource=0x{((IntPtr)d3D11Texture.Resource).ToInt64():X}, srv=0x{((IntPtr)d3D11Texture.ShaderResourceView).ToInt64():X}, writeGeneration={d3D11Texture.WriteGeneration}, renderTargetWrites={_pipeline.GetRenderTargetWriteCount(d3D11Texture.Resource)}, size={d3D11Texture.Width}x{d3D11Texture.Height}, {d3D11Texture.PresentationDescription}; selectedResource=0x{((IntPtr)d3D11Texture2.Resource).ToInt64():X}, selectedSize={d3D11Texture2.Width}x{d3D11Texture2.Height}; " + _pipeline.GetPresentationTelemetry() + "\n");
			}
			if (CanPresentDirect(d3D11Texture2, crop))
			{
				ImageCrop nativeCrop = GetNativeCrop(d3D11Texture2, crop);
				UwpD3D11WindowTelemetry.DirectAttempt(_telemetrySessionId);
				try
				{
					int copyResult = _host.CopyTexture(d3D11Texture2.Resource, nativeCrop);
					if (copyResult >= 0)
					{
						int presentResult = _host.PresentBackBuffer();
						bool presented = presentResult >= 0;
						UwpD3D11WindowTelemetry.DirectCompleted(_telemetrySessionId, presented);
						if (presented)
						{
							swapBuffersCallback?.Invoke();
						}

						// PresentBackBuffer already reports device removal to the
						// host. Do not copy/present the same frame a second time
						// through the compatibility path after a Present failure.
						return;
					}

					UwpD3D11WindowTelemetry.DirectCompleted(_telemetrySessionId, success: false);
				}
				catch
				{
					UwpD3D11WindowTelemetry.DirectCompleted(_telemetrySessionId, success: false);
				}
			}
			UwpD3D11WindowTelemetry.CompatibilityAttempt(_telemetrySessionId);
			bool num3 = _host.CompatibilityPresentCallback?.Invoke(d3D11Texture2, crop, num, $"{d3D11Texture.PresentationDescription}; crop={crop.Left},{crop.Top}-{crop.Right},{crop.Bottom}, flip={crop.FlipX}/{crop.FlipY}, stretched={crop.IsStretched}, aspect={crop.AspectRatioX:F3}:{crop.AspectRatioY:F3}; {_pipeline.GetPresentationTelemetry()}") ?? false;
			if (num3)
			{
				int presentResult = _host.PresentBackBuffer();
				bool presented = presentResult >= 0;
				UwpD3D11WindowTelemetry.CompatibilityCompleted(_telemetrySessionId, presented);
				if (presented)
				{
					swapBuffersCallback?.Invoke();
				}

				// The callback has already uploaded (or deliberately reused)
				// the compatibility frame. A failed swap-chain Present must not
				// fall through and submit it again.
				return;
			}
			UwpD3D11WindowTelemetry.CompatibilityCompleted(_telemetrySessionId, success: false);
			textureResource = d3D11Texture2.Resource;
			goto IL_0456;
		}
		finally
		{
			UwpD3D11WindowTelemetry.Complete(_telemetrySessionId);
		}
	}

	private bool CanPresentDirect(D3D11Texture texture, ImageCrop crop)
	{
		if (_host.PresentTextureTransformCallback == null && _host.PresentTextureCallback == null)
		{
			return false;
		}
		Func<bool> forceCompatibilityPresentCallback = _host.ForceCompatibilityPresentCallback;
		if (forceCompatibilityPresentCallback != null && forceCompatibilityPresentCallback())
		{
			Func<bool> preferNativeTransformCallback = _host.PreferNativeTransformCallback;
			if (preferNativeTransformCallback == null || !preferNativeTransformCallback())
			{
				return false;
			}
		}
		ImageCrop nativeCrop = GetNativeCrop(texture, crop);
		if (texture.Resource != 0 && texture.Width > 0 && texture.Height > 0)
		{
			return !nativeCrop.FlipX;
		}
		return false;
	}

	private ImageCrop GetNativeCrop(D3D11Texture texture, ImageCrop crop)
	{
		ImageCrop result = NormalizeNativeCrop(texture, crop);
		bool value = D3D11PresentationOrientation.NativeFlag(result);
		string text = $"guestOrigin=GAL-resolved; rendererOrigin=D3D11; viewportFlip=resolved-in-GAL; cropFlip={result.FlipY}; profileOverride=none; finalNativeFlag={value}; reason=single-global-complement-v2";
		if (!string.Equals(_lastNativeFlagsLog, text, StringComparison.Ordinal))
		{
			_lastNativeFlagsLog = text;
			D3D11DebugTelemetry.Append("native-present-flags: " + text + "\n");
		}
		return result;
	}

	private ImageCrop NormalizeNativeCrop(D3D11Texture texture, ImageCrop crop)
	{
		if (crop.Left == 0 && crop.Top == 0 && crop.Right == 0 && crop.Bottom == 0)
		{
			return new ImageCrop(0, texture.Width, 0, texture.Height, crop.FlipX, crop.FlipY, crop.IsStretched, crop.AspectRatioX, crop.AspectRatioY);
		}
		if (crop.Left != 0 || crop.Top != 0 || crop.Right <= 0 || crop.Bottom <= 0)
		{
			return crop;
		}
		string text = $"SourceSize={texture.Width}x{texture.Height}; GuestViewport={crop.Right}x{crop.Bottom}; RequestedCrop={crop.Left},{crop.Top}-{crop.Right},{crop.Bottom}; CropCoordinateSpace=GAL/PhysicalResolved; EffectiveCrop={crop.Left},{crop.Top}-{crop.Right},{crop.Bottom}; TargetSize={_host.Width}x{_host.Height}";
		if (!string.Equals(_lastNativeCropLog, text, StringComparison.Ordinal))
		{
			_lastNativeCropLog = text;
			D3D11DebugTelemetry.Append("native-present-crop: " + text + "\n");
		}
		return crop;
	}

	public void SetSize(int width, int height)
	{
		_host.SetSize(width, height);
	}

	public void ChangeVSyncMode(VSyncMode vSyncMode)
	{
		_vSyncMode = vSyncMode;
		// Custom cadence is enforced by the guest VSync scheduler, not a second
		// display-refresh limiter. Never enable tearing flags on a composition chain.
		_host.PresentSyncInterval = vSyncMode == VSyncMode.Switch ? 1 : 0;
	}

	public void SetAntiAliasing(AntiAliasing antialiasing)
	{
		_antiAliasing = antialiasing;
	}

	public void SetScalingFilter(ScalingFilter type)
	{
		_scalingFilter = type;
	}

	public void SetScalingFilterLevel(float level)
	{
		_scalingFilterLevel = level;
	}

	public void SetColorSpacePassthrough(bool colorSpacePassThroughEnabled)
	{
		_colorSpacePassthrough = colorSpacePassThroughEnabled;
	}

	public void Dispose()
	{
	}
}
