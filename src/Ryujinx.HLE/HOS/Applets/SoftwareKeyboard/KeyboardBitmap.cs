using System;

namespace Ryujinx.HLE.HOS.Applets.SoftwareKeyboard;

// Compact original 5x7 Latin glyphs for the emergency keyboard surface.
// Unavailable glyphs are visibly marked, never removed from the actual input.
internal static class KeyboardBitmap
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private static readonly string[] Glyphs =
    [
        "0E11111F111111", "1E11111E11111E", "0E11101010110E", "1E11111111111E",
        "1F10101E10101F", "1F10101E101010", "0E11101711110F", "1111111F111111",
        "0E04040404040E", "0702020212120C", "11121418141211", "1010101010101F",
        "111B1515111111", "11191513111111", "0E11111111110E", "1E11111E101010",
        "0E11111115120D", "1E11111E141211", "0F10100E01011E", "1F040404040404",
        "1111111111110E", "11111111110A04", "11111115151B11", "11110A040A1111",
        "11110A04040404", "1F01020408101F", "0E11131519110E", "040C040404040E",
        "0E11010204081F", "1E01010601011E", "02060A121F0202", "1F10101E01011E",
        "0E10101E11110E", "1F010204080808", "0E11110E11110E", "0E11110F01010E",
    ];

    internal static void Validate(uint width, uint height, uint pitch, uint size)
    {
        if (width == 0 || height == 0 || width > 8192 || height > 8192 ||
            pitch < (ulong)width * 4 || size < (ulong)pitch * height || size > 64 * 1024 * 1024)
            throw new ArgumentException("Invalid inline keyboard surface layout.");
    }

    internal static byte[] Render(uint width, uint height, uint pitch, uint size, string input,
        int cursorBegin, int cursorEnd, bool cursor, bool accept, bool cancel, bool enabled)
    {
        Validate(width, height, pitch, size);
        byte[] data = new byte[checked((int)size)];
        int w = (int)width, h = (int)height, rowPitch = (int)pitch;
        int top = Math.Max(0, h - 240);
        void Rect(int x, int y, int rw, int rh, byte r, byte g, byte b)
        {
            for (int py = Math.Max(0, y); py < Math.Min(h, y + rh); ++py)
            for (int px = Math.Max(0, x); px < Math.Min(w, x + rw); ++px)
            {
                int p = py * rowPitch + px * 4;
                data[p] = r; data[p + 1] = g; data[p + 2] = b; data[p + 3] = 255;
            }
        }
        void Text(string text, int x, int y, byte color)
        {
            foreach (char original in text)
            {
                char c = char.ToUpperInvariant(original);
                int index = Alphabet.IndexOf(c);
                for (int row = 0; row < 7; ++row)
                {
                    int bits = index >= 0 ? Convert.ToInt32(Glyphs[index].Substring(row * 2, 2), 16) :
                        c == ' ' ? 0 : row is 0 or 6 ? 31 : 17;
                    for (int col = 0; col < 5; ++col)
                        if ((bits & (1 << (4 - col))) != 0) Rect(x + col * 2, y + row * 2, 2, 2, color, color, color);
                }
                x += 12;
                if (x >= w - 12) break;
            }
        }
        Rect(0, top, w, h - top, 24, 24, 28);
        Text("RYUBOX  KEYBOARD", 24, top + 16, 240);
        Text("USE THE KEYBOARD TO INPUT TEXT", 24, top + 44, enabled ? (byte)220 : (byte)120);
        Rect(20, top + 76, Math.Max(0, w - 40), 44, 90, 90, 100);
        Rect(22, top + 78, Math.Max(0, w - 44), 40, 8, 8, 12);
        input ??= string.Empty;
        int begin = Math.Clamp(cursorBegin, 0, input.Length), end = Math.Clamp(cursorEnd, begin, input.Length);
        int visible = Math.Max(1, (w - 64) / 12);
        int first = Math.Max(0, end - visible);
        int selectedStart = Math.Max(begin, first), selectedEnd = Math.Min(end, first + visible);
        Rect(30 + (selectedStart - first) * 12, top + 82, Math.Max(0, selectedEnd - selectedStart) * 12, 30, 40, 90, 150);
        Text(input.Substring(first, Math.Min(visible, input.Length - first)), 30, top + 89, enabled ? (byte)255 : (byte)120);
        if (enabled && cursor) Rect(30 + (end - first) * 12, top + 82, 2, 30, 255, 255, 255);
        Rect(24, top + 150, 136, 32, accept ? (byte)40 : (byte)24, accept ? (byte)120 : (byte)24, 28);
        Rect(180, top + 150, 136, 32, cancel ? (byte)120 : (byte)24, 24, 28);
        Text("A  ACCEPT", 30, top + 159, 240);
        Text("B  CANCEL", 186, top + 159, 240);
        return data;
    }
}
