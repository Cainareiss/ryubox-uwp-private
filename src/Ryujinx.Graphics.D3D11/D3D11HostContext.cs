using System;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;

namespace Ryujinx.Graphics.D3D11;

public sealed class D3D11HostContext
{
	public nint Device { get; }

	public nint DeviceContext { get; }

	public nint SwapChain { get; private set; }

	public int Width { get; private set; }

	public int Height { get; private set; }

	public Action PresentCallback { get; }

	public Func<int> PresentResultCallback { get; }

	// Optional interval-aware callback keeps existing desktop/test hosts ABI-compatible.
	public Func<int, int> PresentWithSyncIntervalCallback { get; }

	public int PresentSyncInterval { get; set; } = 1;

	public Action<int> DeviceLostCallback { get; }

	public Action<int, int> ResizeCallback { get; }

	public Func<int, int, nint> SwapChainResizeCallback { get; }

	public Func<ShaderStage, string, byte[]> ShaderCompiler { get; }

	public Func<ShaderStage, byte[], byte[]> SpirvCompiler { get; }

	public Func<ShaderStage, byte[], ShaderInfo, byte[]> SpirvCompilerWithLayout { get; }

	public Action<nint, ImageCrop> PresentTextureCallback { get; }

	public Func<nint, ImageCrop, bool, int> PresentTextureTransformCallback { get; }

	public Func<ITexture, ImageCrop, long, string, bool> CompatibilityPresentCallback { get; }

	public Func<bool> ForceCompatibilityPresentCallback { get; }

	public Func<bool> PreferNativeTransformCallback { get; }

	public Func<bool> ForceVerticalFlipCallback { get; }

	public Func<bool> SuppressVerticalFlipCallback { get; }

	public bool IsValid
	{
		get
		{
			if (Device != 0)
			{
				return DeviceContext != 0;
			}
			return false;
		}
	}

	public D3D11HostContext(nint device, nint deviceContext, nint swapChain = 0, int width = 0, int height = 0, Action presentCallback = null, Func<int> presentResultCallback = null, Action<int> deviceLostCallback = null, Action<int, int> resizeCallback = null, Func<int, int, nint> swapChainResizeCallback = null, Func<ShaderStage, string, byte[]> shaderCompiler = null, Func<ShaderStage, byte[], byte[]> spirvCompiler = null, Action<nint, ImageCrop> presentTextureCallback = null, Func<ShaderStage, byte[], ShaderInfo, byte[]> spirvCompilerWithLayout = null, Func<ITexture, ImageCrop, long, string, bool> compatibilityPresentCallback = null, Func<bool> forceCompatibilityPresentCallback = null, Func<nint, ImageCrop, bool, int> presentTextureTransformCallback = null, Func<bool> preferNativeTransformCallback = null, Func<bool> forceVerticalFlipCallback = null, Func<bool> suppressVerticalFlipCallback = null, Func<int, int> presentWithSyncIntervalCallback = null)
	{
		Device = device;
		DeviceContext = deviceContext;
		SwapChain = swapChain;
		Width = width;
		Height = height;
		PresentCallback = presentCallback;
		PresentResultCallback = presentResultCallback;
		PresentWithSyncIntervalCallback = presentWithSyncIntervalCallback;
		DeviceLostCallback = deviceLostCallback;
		ResizeCallback = resizeCallback;
		SwapChainResizeCallback = swapChainResizeCallback;
		ShaderCompiler = shaderCompiler;
		SpirvCompiler = spirvCompiler;
		SpirvCompilerWithLayout = spirvCompilerWithLayout;
		PresentTextureCallback = presentTextureCallback;
		PresentTextureTransformCallback = presentTextureTransformCallback;
		CompatibilityPresentCallback = compatibilityPresentCallback;
		ForceCompatibilityPresentCallback = forceCompatibilityPresentCallback;
		PreferNativeTransformCallback = preferNativeTransformCallback;
		ForceVerticalFlipCallback = forceVerticalFlipCallback;
		SuppressVerticalFlipCallback = suppressVerticalFlipCallback;
	}

	public void SetSize(int width, int height)
	{
		Width = Math.Max(1, width);
		Height = Math.Max(1, height);
		nint num = SwapChainResizeCallback?.Invoke(Width, Height) ?? 0;
		if (num != 0)
		{
			SwapChain = num;
		}
		ResizeCallback?.Invoke(Width, Height);
	}

	public int Present(nint textureResource = 0, ImageCrop crop = default(ImageCrop))
	{
		int num = 0;
		if (textureResource != 0)
		{
			num = CopyTexture(textureResource, crop);
			if (num < 0)
			{
				return num;
			}
		}
		return PresentBackBuffer();
	}

	public int CopyTexture(nint textureResource, ImageCrop crop = default(ImageCrop))
	{
		if (textureResource == 0)
		{
			return -2147024809;
		}
		bool arg = D3D11PresentationOrientation.NativeFlag(crop);
		int num;
		if (PresentTextureTransformCallback != null)
		{
			num = PresentTextureTransformCallback(textureResource, crop, arg);
		}
		else
		{
			PresentTextureCallback?.Invoke(textureResource, crop);
			num = 0;
		}
		if (IsDeviceLostHResult(num))
		{
			DeviceLostCallback?.Invoke(num);
		}
		return num;
	}

	public int PresentBackBuffer()
	{
		int num;
		if (PresentWithSyncIntervalCallback != null)
		{
			num = PresentWithSyncIntervalCallback(PresentSyncInterval);
		}
		else if (PresentResultCallback != null)
		{
			num = PresentResultCallback();
		}
		else
		{
			PresentCallback?.Invoke();
			num = 0;
		}
		if (IsDeviceLostHResult(num))
		{
			DeviceLostCallback?.Invoke(num);
		}
		return num;
	}

	public void NotifyDeviceLost(int hResult)
	{
		if (IsDeviceLostHResult(hResult))
		{
			DeviceLostCallback?.Invoke(hResult);
		}
	}

	public static bool IsDeviceLostHResult(int hResult)
	{
		if ((uint)(hResult - -2005270523) <= 2u || hResult == -2005270496)
		{
			return true;
		}
		return false;
	}

	public void Validate()
	{
		if (!IsValid)
		{
			throw new InvalidOperationException("D3D11HostContext requires non-null ID3D11Device and ID3D11DeviceContext pointers.");
		}
	}
}
