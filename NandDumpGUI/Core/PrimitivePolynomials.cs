using System;
using System.Collections.Generic;
using System.Linq;

namespace NandDumpGUI.Core
{
    public sealed record PrimitivePolyPreset(string Name, int M, uint Poly, string Notes)
    {
        public string Display => $"{Name}  (m={M}, 0x{Poly:X})";
    }

    public static class PrimitivePolynomials
    {
        // “Classici” (quelli tipici per GF(2^m)) + il tuo 0x5803 usato spesso in ambito NAND Broadcom.
        // Nota: la lista “classica” più comune (Linux) per m=14 è 0x402B.
        public static readonly IReadOnlyList<PrimitivePolyPreset> All = new List<PrimitivePolyPreset>
        {
            new("GF(2^5)  default",  5,  0x25,   "Common primitive polynomial for m=5"),
            new("GF(2^6)  default",  6,  0x43,   "Common primitive polynomial for m=6"),
            new("GF(2^7)  default",  7,  0x83,   "Common primitive polynomial for m=7"),
            new("GF(2^8)  default",  8,  0x11D,  "Very common (CRC/BCH literature)"),
            new("GF(2^9)  default",  9,  0x211,  "Common primitive polynomial for m=9"),
            new("GF(2^10) default",  10, 0x409,  "Common primitive polynomial for m=10"),
            new("GF(2^11) default",  11, 0x805,  "Common primitive polynomial for m=11"),
            new("GF(2^12) default",  12, 0x1053, "Common primitive polynomial for m=12"),
            new("GF(2^13) default",  13, 0x201B, "Common primitive polynomial for m=13"),

            // m=14: due opzioni tipiche
            new("GF(2^14) Linux default", 14, 0x402B, "Linux BCH default for m=14"),
            new("GF(2^14) Broadcom-style", 14, 0x5803, "Often used in NAND ECC contexts (e.g., some Broadcom dumps)"),

            new("GF(2^15) Linux default", 15, 0x8003, "Linux BCH default for m=15"),
        };

        public static PrimitivePolyPreset? FindByPoly(uint poly) =>
            All.FirstOrDefault(p => p.Poly == poly);

        public static PrimitivePolyPreset DefaultForM(int m) =>
            All.FirstOrDefault(p => p.M == m) ?? All.First(p => p.M == 14);
    }
}
