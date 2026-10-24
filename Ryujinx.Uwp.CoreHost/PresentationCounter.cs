namespace Ryujinx.Uwp.CoreHost;

internal static class PresentationCounter
{
    // A recreated/reset native context must not underflow into a huge fake FPS.
    public static ulong Delta(ulong current, ulong previous) => current >= previous ? current - previous : 0;
}
