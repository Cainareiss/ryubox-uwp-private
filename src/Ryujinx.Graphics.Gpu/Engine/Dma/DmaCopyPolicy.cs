namespace Ryujinx.Graphics.Gpu.Engine.Dma
{
    internal static class DmaCopyPolicy
    {
        public static void ValidateLinearRange(ulong address, ulong size, string role)
        {
            // Validate only arithmetic here. Mapping/access permissions remain
            // MemoryManager's responsibility; invalid guest accesses still fault.
            if (size != 0 && (address == ulong.MaxValue || size - 1 > ulong.MaxValue - address))
                throw new System.InvalidOperationException($"DMA {role} address overflow/sentinel: address=0x{address:X16}, size={size}");
        }
        public static int GetConstantPattern(uint remap, uint constA, uint constB, System.Span<byte> pattern)
        {
            int componentBytes = (int)((remap >> 16) & 3) + 1;
            int components = (int)((remap >> 24) & 3) + 1;
            for (int c = 0; c < components; c++)
            {
                uint selector = (remap >> (c * 4)) & 7;
                if (selector is not (4 or 5)) return 0;
                uint value = selector == 4 ? constA : constB;
                for (int b = 0; b < componentBytes; b++)
                    pattern[c * componentBytes + b] = (byte)(value >> (b * 8));
            }
            return componentBytes * components;
        }

        // NVIDIA MAXWELL_DMA_COPY_A (clb0b5.h): DATA_TRANSFER_TYPE occupies bits 1:0.
        // A semaphore-only launch can retain stale copy registers, including size.
        public static bool TransfersData(int launch) => (launch & 3) != 0;

        public static bool TryGetUniformWordFill(uint remap, uint constA, uint constB,
            out uint value, out int wordsPerElement)
        {
            value = 0;
            wordsPerElement = (int)((remap >> 24) & 3) + 1;
            if (((remap >> 16) & 3) != 3) return false; // four-byte components only

            for (int component = 0; component < wordsPerElement; component++)
            {
                uint selector = (remap >> (component * 4)) & 7;
                if (selector is not (4 or 5)) return false;
                uint current = selector == 4 ? constA : constB;
                if (component != 0 && current != value) return false;
                value = current;
            }
            // Inactive destination selectors and source count do not participate
            // in a constant fill. The source address need not be mapped.
            return true;
        }
    }
}
