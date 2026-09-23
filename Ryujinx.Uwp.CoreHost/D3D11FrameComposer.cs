using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Uwp.CoreHost
{
    internal static class D3D11FrameComposer
    {
        internal readonly struct CropRectangle
        {
            public int Left { get; }
            public int Top { get; }
            public int Right { get; }
            public int Bottom { get; }
            public bool WasNormalized { get; }

            public CropRectangle(int left, int top, int right, int bottom, bool wasNormalized)
            {
                Left = left;
                Top = top;
                Right = right;
                Bottom = bottom;
                WasNormalized = wasNormalized;
            }
        }

        public static CropRectangle NormalizeCrop(
            int sourceWidth,
            int sourceHeight,
            int cropLeft,
            int cropTop,
            int cropRight,
            int cropBottom,
            bool expandResolutionScaledCrop = true)
        {
            bool emptyCrop = cropLeft == 0 && cropTop == 0 && cropRight == 0 && cropBottom == 0;
            int left = emptyCrop ? 0 : Math.Clamp(cropLeft, 0, sourceWidth - 1);
            int right = emptyCrop ? sourceWidth : Math.Clamp(cropRight, left + 1, sourceWidth);
            int top = emptyCrop ? 0 : Math.Clamp(cropTop, 0, sourceHeight - 1);
            int bottom = emptyCrop ? sourceHeight : Math.Clamp(cropBottom, top + 1, sourceHeight);
            bool normalized = false;

            // Resolution-scaled presentation textures may be 1920x1080 while
            // SurfaceFlinger still describes its logical handheld crop as
            // 1280x720. Applying that logical rectangle directly crops away a
            // third of each axis and produces the large zoom seen on Xbox.
            // Expand only a full-origin crop with the same aspect and uniform
            // scale, preserving intentional letterbox/sub-rectangle crops.
            if (expandResolutionScaledCrop && left == 0 && top == 0 && (right < sourceWidth || bottom < sourceHeight))
            {
                float scaleX = sourceWidth / (float)right;
                float scaleY = sourceHeight / (float)bottom;
                bool uniformScale = MathF.Abs(scaleX - scaleY) <= 0.01f;
                bool sameAspect = MathF.Abs(
                    sourceWidth / (float)sourceHeight - right / (float)bottom) <= 0.001f;

                if (uniformScale && sameAspect && scaleX > 1f && scaleX <= 4f)
                {
                    right = sourceWidth;
                    bottom = sourceHeight;
                    normalized = true;
                }
                else if (right < sourceWidth && scaleX > 1f && scaleX <= 4f)
                {
                    // Some textures carry up to 16 alignment rows after a scaled
                    // 16:9 image (for example 1920x1088 with a logical 1280x720
                    // crop). Scale the visible crop but never include that padding.
                    int scaledBottom = (int)MathF.Round(bottom * scaleX);
                    int alignmentRows = sourceHeight - scaledBottom;
                    if (scaledBottom > top && alignmentRows >= 0 && alignmentRows <= 16)
                    {
                        right = sourceWidth;
                        bottom = scaledBottom;
                        normalized = true;
                    }
                }
                else if (bottom < sourceHeight && scaleY > 1f && scaleY <= 4f)
                {
                    int scaledRight = (int)MathF.Round(right * scaleY);
                    int alignmentColumns = sourceWidth - scaledRight;
                    if (scaledRight > left && alignmentColumns >= 0 && alignmentColumns <= 16)
                    {
                        right = scaledRight;
                        bottom = sourceHeight;
                        normalized = true;
                    }
                }
            }

            return new CropRectangle(left, top, right, bottom, normalized);
        }

        public static byte[] Compose(
            ReadOnlySpan<byte> source,
            int sourceWidth,
            int sourceHeight,
            int targetWidth,
            int targetHeight,
            int cropLeft,
            int cropTop,
            int cropRight,
            int cropBottom,
            bool flipX,
            bool flipY,
            bool stretched,
            float aspectRatioX,
            float aspectRatioY,
            bool preserveLegacyVerticalOrder,
            bool expandResolutionScaledCrop,
            ref byte[] reusableFrame)
        {
            CropRectangle crop = NormalizeCrop(
                sourceWidth,
                sourceHeight,
                cropLeft,
                cropTop,
                cropRight,
                cropBottom,
                expandResolutionScaledCrop);

            // A logical 1280x720 crop can mean either a resolution-scaled
            // 1920x1080 image or a 1280x720 image stored in the top-left of a
            // larger allocation. The old alpha classifier was not reliable on
            // Xbox: unrendered memory can have either zero or stale alpha, so
            // it alternated between the correct full surface and the zoomed
            // top-left crop. For an exact, full-origin 16:9 scale the geometry
            // is authoritative; never let the classifier undo that decision.
            bool isFullOriginResolutionScaledCrop =
                expandResolutionScaledCrop &&
                cropLeft == 0 && cropTop == 0 &&
                cropRight > 0 && cropBottom > 0 &&
                cropRight < sourceWidth && cropBottom < sourceHeight &&
                MathF.Abs(sourceWidth / (float)sourceHeight - cropRight / (float)cropBottom) <= 0.001f &&
                MathF.Abs((sourceWidth / (float)cropRight) - (sourceHeight / (float)cropBottom)) <= 0.01f;

            // Keep the classifier for intentional sub-rectangles and
            // alignment-padding cases, but never use it to reintroduce the
            // 1280x720-in-1920x1080 zoom.
            if (crop.WasNormalized &&
                !isFullOriginResolutionScaledCrop &&
                cropRight > cropLeft && cropBottom > cropTop &&
                !HasOpaqueSamplesOutsideLogicalCrop(
                    source,
                    sourceWidth,
                    sourceHeight,
                    cropLeft,
                    cropTop,
                    cropRight,
                    cropBottom))
            {
                int logicalLeft = Math.Clamp(cropLeft, 0, sourceWidth - 1);
                int logicalTop = Math.Clamp(cropTop, 0, sourceHeight - 1);
                int logicalRight = Math.Clamp(cropRight, logicalLeft + 1, sourceWidth);
                int logicalBottom = Math.Clamp(cropBottom, logicalTop + 1, sourceHeight);
                crop = new CropRectangle(
                    logicalLeft,
                    logicalTop,
                    logicalRight,
                    logicalBottom,
                    false);
            }
            int croppedWidth = crop.Right - crop.Left;
            int croppedHeight = crop.Bottom - crop.Top;

            float aspectX = aspectRatioX > 0f ? aspectRatioX : croppedWidth;
            float aspectY = aspectRatioY > 0f ? aspectRatioY : croppedHeight;
            float targetAspect = targetWidth / (float)targetHeight;
            float contentAspect = aspectX / aspectY;
            bool matchesTargetAspect = MathF.Abs(contentAspect - targetAspect) / targetAspect <= 0.005f;
            float ratioX = stretched || matchesTargetAspect
                ? 1f
                : MathF.Min(1f, targetHeight * aspectX / (targetWidth * aspectY));
            float ratioY = stretched || matchesTargetAspect
                ? 1f
                : MathF.Min(1f, targetWidth * aspectY / (targetHeight * aspectX));

            int destinationWidth = Math.Max(1, (int)MathF.Round(targetWidth * ratioX));
            int destinationHeight = Math.Max(1, (int)MathF.Round(targetHeight * ratioY));
            int destinationLeft = (targetWidth - destinationWidth) / 2;
            int destinationTop = (targetHeight - destinationHeight) / 2;
            int requiredLength = checked(targetWidth * targetHeight * 4);
            if (reusableFrame.Length != requiredLength)
            {
                reusableFrame = new byte[requiredLength];
            }

            byte[] result = reusableFrame;
            // Composition swap chains can expose source alpha even with an
            // IGNORE alpha mode on some Xbox system builds. Opaque black bars
            // and opaque game pixels prevent the launcher from bleeding
            // through games such as ACA NEOGEO Metal Slug.
            MemoryMarshal.Cast<byte, uint>(result.AsSpan()).Fill(0xFF000000u);

            bool directWidth = !flipX && croppedWidth == destinationWidth;
            for (int y = 0; y < destinationHeight; y++)
            {
                int sampledY = y * croppedHeight / destinationHeight;
                // D3D11 staging rows are top-down. The caller converts GAL's
                // row-order convention once before entering this compositor.
                // The legacy override is retained only for ABI compatibility;
                // production no longer selects it per title.
                bool reverseY = preserveLegacyVerticalOrder ? !flipY : flipY;
                int sourceY = reverseY
                    ? crop.Bottom - 1 - sampledY
                    : crop.Top + sampledY;
                int targetRowOffset = ((destinationTop + y) * targetWidth + destinationLeft) * 4;

                if (directWidth)
                {
                    int sourceRowOffset = (sourceY * sourceWidth + crop.Left) * 4;
                    source.Slice(sourceRowOffset, destinationWidth * 4)
                        .CopyTo(result.AsSpan(targetRowOffset, destinationWidth * 4));
                    for (int alpha = targetRowOffset + 3; alpha < targetRowOffset + destinationWidth * 4; alpha += 4)
                    {
                        result[alpha] = byte.MaxValue;
                    }
                    continue;
                }

                for (int x = 0; x < destinationWidth; x++)
                {
                    int sampledX = x * croppedWidth / destinationWidth;
                    int sourceX = flipX
                        ? crop.Right - 1 - sampledX
                        : crop.Left + sampledX;
                    int sourceOffset = (sourceY * sourceWidth + sourceX) * 4;
                    int targetOffset = targetRowOffset + x * 4;
                    result[targetOffset] = source[sourceOffset];
                    result[targetOffset + 1] = source[sourceOffset + 1];
                    result[targetOffset + 2] = source[sourceOffset + 2];
                    result[targetOffset + 3] = byte.MaxValue;
                }
            }

            return result;
        }

        private static bool HasOpaqueSamplesOutsideLogicalCrop(
            ReadOnlySpan<byte> source,
            int width,
            int height,
            int cropLeft,
            int cropTop,
            int cropRight,
            int cropBottom)
        {
            const int Grid = 24;
            int sampled = 0;
            int opaque = 0;

            for (int gy = 0; gy < Grid; gy++)
            {
                int y = gy * (height - 1) / (Grid - 1);
                for (int gx = 0; gx < Grid; gx++)
                {
                    int x = gx * (width - 1) / (Grid - 1);
                    bool insideLogicalCrop = x >= cropLeft && x < cropRight &&
                                             y >= cropTop && y < cropBottom;
                    if (insideLogicalCrop)
                    {
                        continue;
                    }

                    sampled++;
                    int offset = (y * width + x) * 4;
                    if (offset + 3 < source.Length && source[offset + 3] >= 128)
                    {
                        opaque++;
                    }
                }
            }

            return sampled != 0 && opaque * 10 >= sampled;
        }
    }
}
