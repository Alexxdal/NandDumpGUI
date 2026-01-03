using System;

namespace NandDumpGUI.Core
{
    public enum TransformKind { None, Inv, Bitrev, InvBitrev }

    public static class BitTransforms
    {
        public static byte BitReverse(byte x)
        {
            x = (byte)((x >> 4) | (x << 4));
            x = (byte)(((x & 0xCC) >> 2) | ((x & 0x33) << 2));
            x = (byte)(((x & 0xAA) >> 1) | ((x & 0x55) << 1));
            return x;
        }

        public static void ApplyInPlace(Span<byte> buf, TransformKind kind)
        {
            if (kind == TransformKind.None) return;

            for (int i = 0; i < buf.Length; i++)
            {
                byte b = buf[i];

                if (kind == TransformKind.Inv || kind == TransformKind.InvBitrev)
                    b ^= 0xFF;

                if (kind == TransformKind.Bitrev || kind == TransformKind.InvBitrev)
                    b = BitReverse(b);

                buf[i] = b;
            }
        }

        public static bool AllFF(ReadOnlySpan<byte> s)
        {
            foreach (var b in s)
                if (b != 0xFF) return false;
            return true;
        }
    }
}