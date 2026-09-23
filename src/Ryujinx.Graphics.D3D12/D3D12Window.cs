using System;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;

namespace Ryujinx.Graphics.D3D12;

public sealed class D3D12Window : IWindow
{
    private readonly D3D12Renderer _renderer;
    private int _width, _height;
    private bool _vsync = true, _linear;
    private bool _suspended;
    private float _filterLevel;
    private int _presentCounter;
    private static readonly bool TracePresentation =
        Environment.GetEnvironmentVariable("RYUBOX_D3D12_TRACE_PRESENTATION") == "1";
    internal D3D12Window(D3D12Renderer renderer, uint width, uint height) { _renderer = renderer; _width = (int)width; _height = (int)height; }
    public void Present(ITexture texture, ImageCrop crop, Action callback)
    {
        if (_suspended)
        {
            callback?.Invoke();
            return;
        }
        var image = _renderer.Texture(texture); _renderer.StopCounters(); _renderer.EnsureCommands();
        _renderer.LastPresentedTexture = image;
        if (TracePresentation) try
        {
            string line = $"[Present] image={image.Width}x{image.Height} fmt={image.Info.Format} id={image.Native?.Id} crop=({crop.Left},{crop.Top},{crop.Right},{crop.Bottom})\n";
            System.IO.File.AppendAllText(System.IO.Path.Combine(D3D12Pipeline.LogDir, "draw_targets.txt"), line);
        }
        catch {}
        _renderer.Device.Clear(null, 0, 0, 0, 1);
        int left = crop.Left;
        int top = crop.Top;
        int right = crop.Right;
        int bottom = crop.Bottom;

        if (left == 0 && right == 0)
        {
            right = image.Width;
        }
        if (top == 0 && bottom == 0)
        {
            bottom = image.Height;
        }

        // When a 1920x1080 buffer only renders into a 1280x720 viewport (common in Handheld mode,
        // or when a title allocates 1080p but renders a 720p guest scene with crop=(0,0,0,0) or (0,0,1280,720)),
        // crop the active 1280x720 area so it fills the host window cleanly without black borders.
        if (image.Width == 1920 && image.Height == 1080 && left == 0 && top == 0 &&
            crop.Right == 1280 && crop.Bottom == 720)
        {
            int pWidth = (_renderer.Pipeline as D3D12Pipeline)?.Last1080pRenderWidth ?? 0;
            int pHeight = (_renderer.Pipeline as D3D12Pipeline)?.Last1080pRenderHeight ?? 0;

            bool is1280x720Crop = crop.Right == 1280 && crop.Bottom == 720;
            bool isFullBufferCrop = (crop.Right == 0 || crop.Right == 1920) && (crop.Bottom == 0 || crop.Bottom == 1080);
            bool targetHad720pViewport = (pWidth == 1280 && pHeight == 720) || (pWidth > 0 && pWidth <= 1280 && pHeight <= 720);

            if (is1280x720Crop || (isFullBufferCrop && (targetHad720pViewport || pWidth == 0)))
            {
                right = 1280;
                bottom = 720;
            }
        }

        left = Math.Clamp(left, 0, image.Width - 1);
        right = Math.Clamp(right, left + 1, image.Width);
        top = Math.Clamp(top, 0, image.Height - 1);
        bottom = Math.Clamp(bottom, top + 1, image.Height);

        if (TracePresentation) Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
            $"[D3D12 Present DEBUG] image={image.Width}x{image.Height}, crop=({crop.Left},{crop.Top},{crop.Right},{crop.Bottom}), clamped=({left},{top},{right},{bottom}), flipY={crop.FlipY}, win={_width}x{_height}");
        if (TracePresentation)
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(D3D12Pipeline.LogDir, "present_debug.txt"), $"image={image.Width}x{image.Height}, crop=({crop.Left},{crop.Top},{crop.Right},{crop.Bottom}), clamped=({left},{top},{right},{bottom}), flipY={crop.FlipY}, win={_width}x{_height}\n"); } catch {}

        float aspect = crop.AspectRatioX > 0 && crop.AspectRatioY > 0 ? crop.AspectRatioX / crop.AspectRatioY : (right - left) / (float)(bottom - top);
        float w = _width, h = _height;
        if (!crop.IsStretched) { if (w / h > aspect) w = h * aspect; else h = w / aspect; }
        float x = (_width - w) / 2, y = (_height - h) / 2;
        // GAL presentation coordinates use a bottom-left guest origin while
        // the D3D12 presentation target is top-left. The baseline conversion
        // is therefore a vertical inversion; the guest FlipY flag cancels it.
        // Passing crop.FlipY through directly inverted the normal path and
        // was visible in Rivals of Aether (and in the asymmetric presentation
        // regression) even when the 3D render target itself was correct.
        bool flipY = !crop.FlipY;
        var source = new Extents2DF(crop.FlipX ? right : left, flipY ? bottom : top,
            crop.FlipX ? left : right, flipY ? top : bottom);
        _renderer.Blit(image, null, source, new(x, y, x + w, y + h), _linear);
        _renderer.Present(_vsync); callback?.Invoke();

        int pIndex = System.Threading.Interlocked.Increment(ref _presentCounter);
        if (TracePresentation && (pIndex == 1 || pIndex == 60 || pIndex == 120 || pIndex % 300 == 0))
        {
            try
            {
                using var pSpan = image.GetData(0, 0);
                var span = pSpan.Get();
                int imgColored = 0;
                long imgEnergy = 0;
                for (int i = 0; i < span.Length; i += 64)
                {
                    if (span[i] != 0 || span[i + 1] != 0 || span[i + 2] != 0)
                    {
                        imgColored++;
                        imgEnergy += span[i] + span[i + 1] + span[i + 2];
                    }
                }
                string logLine = $"[Present #{pIndex}] fmt={image.Info.Format}, w={image.Width}, h={image.Height}, colored={imgColored}/{span.Length / 64}, energy={imgEnergy}\n";
                System.IO.File.AppendAllText(System.IO.Path.Combine(D3D12Pipeline.LogDir, "present_image_check.txt"), logLine);
            }
            catch (Exception ex)
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(D3D12Pipeline.LogDir, "present_image_check.txt"),
                    $"[Present #{pIndex}] Exception reading image: {ex.Message}\n");
            }
        }
    }
    public void SetSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            // Transient zero size during UWP minimization or suspension. Defer resize until positive.
            _suspended = true;
            return;
        }
        if (_width == width && _height == height)
        {
            _suspended = false;
            return;
        }
        _renderer.Flush(); _renderer.Device.Resize((uint)width, (uint)height); _width = width; _height = height;
        _suspended = false;
    }
    public void ChangeVSyncMode(VSyncMode mode)
    {
        if (mode == VSyncMode.Custom) throw new NotSupportedException("Custom refresh pacing has not been implemented for D3D12.");
        _vsync = mode == VSyncMode.Switch;
    }
    public void SetAntiAliasing(AntiAliasing mode)
    { if (mode != AntiAliasing.None) throw new NotSupportedException("D3D12 post-process AA is not implemented."); }
    public void SetScalingFilter(ScalingFilter filter)
    {
        if (filter is not (ScalingFilter.Nearest or ScalingFilter.Bilinear)) throw new NotSupportedException("D3D12 supports nearest and bilinear scaling.");
        _linear = filter == ScalingFilter.Bilinear;
    }
    public void SetScalingFilterLevel(float level)
    {
        if (!float.IsFinite(level)) throw new ArgumentOutOfRangeException(nameof(level));
        _filterLevel = level; // Stored for filter selection; nearest/bilinear have no strength parameter.
    }
    public void SetColorSpacePassthrough(bool enabled)
    { if (enabled) throw new NotSupportedException("Color-space passthrough needs the format/color-space extension."); }
}
