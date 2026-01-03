using System;
using NandDumpGUI.Native;

namespace NandDumpGUI.Core
{
    public sealed class BchContext : IDisposable
    {
        public IntPtr Handle { get; private set; }
        public int M { get; }
        public int T { get; }
        public uint Poly { get; }

        private BchContext(IntPtr handle, int m, int t, uint poly)
        {
            Handle = handle;
            M = m;
            T = t;
            Poly = poly;
        }

        public static BchContext Create(int m, int t, uint poly, bool swapBits = false)
        {
            IntPtr h = BchNative.bch_init(m, t, poly, swapBits);
            if (h == IntPtr.Zero)
                throw new InvalidOperationException($"bch_init fallita (m={m}, t={t}, poly=0x{poly:X}).");

            return new BchContext(h, m, t, poly);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                BchNative.bch_free(Handle);
                Handle = IntPtr.Zero;
            }
            GC.SuppressFinalize(this);
        }
    }
}