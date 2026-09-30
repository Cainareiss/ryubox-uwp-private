using Ryujinx.Uwp.CoreHost;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

D3D11FrameComposer.CropRectangle scaledCrop = D3D11FrameComposer.NormalizeCrop(
    1920, 1080, 0, 0, 1280, 720);
Assert(scaledCrop.WasNormalized, "Logical 1280x720 crop was not normalized for a 1920x1080 texture.");
Assert(scaledCrop.Right == 1920 && scaledCrop.Bottom == 1080, "Normalized crop does not cover the full scaled texture.");

D3D11FrameComposer.CropRectangle intentionalCrop = D3D11FrameComposer.NormalizeCrop(
    1920, 1080, 160, 90, 1760, 990);
Assert(!intentionalCrop.WasNormalized, "Intentional centered crop was expanded incorrectly.");

byte[] source =
[
    255, 0, 0, 0,       0, 255, 0, 64,
    0, 0, 255, 128,     255, 255, 255, 192,
];
byte[] frame = [];
byte[] flipped = D3D11FrameComposer.Compose(
    source, 2, 2, 2, 2,
    0, 0, 0, 0,
    false, true, true, 2, 2,
    ref frame);
Assert(flipped[0] == 255 && flipped[1] == 0 && flipped[2] == 0, "FlipY=true did not preserve the presentation-order top row.");
Assert(flipped[8] == 0 && flipped[9] == 0 && flipped[10] == 255, "FlipY=true produced the wrong presentation-order bottom row.");
for (int alpha = 3; alpha < flipped.Length; alpha += 4)
{
    Assert(flipped[alpha] == 255, "Composed gameplay surface contains transparent pixels.");
}

byte[] reversedFrame = [];
byte[] reversed = D3D11FrameComposer.Compose(
    source, 2, 2, 2, 2,
    0, 0, 0, 0,
    false, false, true, 2, 2,
    ref reversedFrame);
Assert(reversed[0] == 0 && reversed[1] == 0 && reversed[2] == 255, "FlipY=false did not apply the SurfaceFlinger row correction.");
Assert(reversed[8] == 255 && reversed[9] == 0 && reversed[10] == 0, "FlipY=false produced the wrong corrected bottom row.");

byte[] wideSource =
[
    10, 20, 30, 0,
    40, 50, 60, 0,
];
byte[] letterboxFrame = [];
byte[] letterboxed = D3D11FrameComposer.Compose(
    wideSource, 2, 1, 2, 2,
    0, 0, 0, 0,
    false, false, false, 2, 1,
    ref letterboxFrame);
for (int alpha = 3; alpha < letterboxed.Length; alpha += 4)
{
    Assert(letterboxed[alpha] == 255, "Letterbox bars must be opaque to hide the launcher.");
}

Console.WriteLine("PASS: scaled crop normalization");
Console.WriteLine("PASS: intentional crop preservation");
Console.WriteLine("PASS: Ryujinx ImageCrop FlipY contract");
Console.WriteLine("PASS: opaque gameplay surface and letterbox bars");
