using Ryujinx.Graphics.GAL;

namespace Ryujinx.Graphics.D3D11;

internal static class D3D11PresentationOrientation
{
    // Preserve the established native ABI: it receives the complement of the
    // GAL crop flag. Viewport/guest orientation has already been resolved by GAL.
    internal static bool NativeFlag(ImageCrop crop) => !crop.FlipY;
}
