using System;
using System.Runtime.InteropServices;

namespace NandDumpGUI.Native
{
    internal static class BchNative
    {
        private const string Dll = "bchlib.dll";

        [DllImport(Dll, EntryPoint = "bch_init", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern IntPtr bch_init(int m, int t, uint prim_poly, [MarshalAs(UnmanagedType.I1)] bool swap_bits);

        [DllImport(Dll, EntryPoint = "bch_free", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern void bch_free(IntPtr bch);

        [DllImport(Dll, EntryPoint = "bch_encode", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern void bch_encode(IntPtr bch, byte[] data, uint len, byte[] ecc);

        // C: int bch_decode(bch, data, len, recv_ecc, calc_ecc, syn, errloc)
        [DllImport(Dll, EntryPoint = "bch_decode", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int bch_decode(
            IntPtr bch,
            byte[] data,
            uint len,
            byte[]? recv_ecc,
            byte[]? calc_ecc,
            IntPtr syn,
            uint[] errloc);
    }
}