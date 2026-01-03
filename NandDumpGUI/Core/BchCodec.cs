using System;
using NandDumpGUI.Native;

namespace NandDumpGUI.Core
{
    public static class BchCodec
    {
        public static int DegreeFromPoly(uint poly)
        {
            if (poly == 0) throw new ArgumentException("poly=0 non valido");
            int deg = 0;
            uint x = poly;
            while ((x >>= 1) != 0) deg++;
            return deg;
        }

        public static void ApplyErrLoc(byte[] msg, byte[] ecc, uint[] errloc, int nerr)
        {
            int msgBits = msg.Length * 8;

            for (int i = 0; i < nerr; i++)
            {
                uint pos = errloc[i];

                if (pos < msgBits)
                {
                    int byteIdx = (int)(pos / 8);
                    int bitIdx = (int)(pos % 8);
                    msg[byteIdx] ^= (byte)(1 << bitIdx);
                }
                else
                {
                    uint epos = pos - (uint)msgBits;
                    int byteIdx = (int)(epos / 8);
                    int bitIdx = (int)(epos % 8);
                    if ((uint)byteIdx < (uint)ecc.Length)
                        ecc[byteIdx] ^= (byte)(1 << bitIdx);
                }
            }
        }

        public static int DecodeAndCorrect(
            BchContext bch,
            byte[] msg,
            byte[] eccInDecoderDomain,
            uint[] errloc)
        {
            Array.Clear(errloc, 0, errloc.Length);

            int nerr = BchNative.bch_decode(
                bch.Handle,
                msg,
                (uint)msg.Length,
                eccInDecoderDomain,
                null,
                IntPtr.Zero,
                errloc);

            if (nerr > 0)
                ApplyErrLoc(msg, eccInDecoderDomain, errloc, nerr);

            return nerr;
        }

        public static byte[] EncodeEcc(BchContext bch, byte[] msg, int eccLen)
        {
            var ecc = new byte[eccLen];
            BchNative.bch_encode(bch.Handle, msg, (uint)msg.Length, ecc);
            return ecc;
        }
    }
}
