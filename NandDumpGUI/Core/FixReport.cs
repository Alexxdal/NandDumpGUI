namespace NandDumpGUI.Core
{
    public readonly record struct FixReport(
        long TotalPages,
        long ErasedSkippedSectors,
        long CheckedSectors,
        long UncorrectableSectors,
        long PagesTouched,
        long TotalBitflips)
    {
        public double UncorrectableRatio =>
            CheckedSectors <= 0 ? 0.0 : (double)UncorrectableSectors / CheckedSectors;
    }
}
