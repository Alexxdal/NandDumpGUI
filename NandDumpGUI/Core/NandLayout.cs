using System;

namespace NandDumpGUI.Core
{
    public readonly record struct NandLayout(
        int PageSize,
        int OobSize,
        int SectorSize,
        int OobChunk,
        int EccOfs,
        int EccLen)
    {
        public int RawPage => PageSize + OobSize;
        public int EccSteps => PageSize / SectorSize;

        public void Validate()
        {
            if (PageSize <= 0) throw new ArgumentException("PageSize non valido.");
            if (OobSize < 0) throw new ArgumentException("OobSize non valido.");
            if (SectorSize <= 0) throw new ArgumentException("SectorSize non valido.");
            if (PageSize % SectorSize != 0) throw new ArgumentException("PageSize deve essere multiplo di SectorSize.");

            if (OobChunk <= 0) throw new ArgumentException("OobChunk non valido.");
            if (EccOfs < 0 || EccLen <= 0) throw new ArgumentException("ECC_OFS/ECC_LEN non validi.");
            if (EccOfs + EccLen > OobChunk) throw new ArgumentException("ECC_OFS+ECC_LEN supera OobChunk.");

            if (OobChunk * EccSteps > OobSize)
                throw new ArgumentException("OobChunk * EccSteps supera la OOB totale (OobSize).");
        }
    }
}