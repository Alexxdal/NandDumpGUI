using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NandDumpGUI.Core
{
    public readonly record struct FixOptions(
        int OobDataBytes,
        TransformKind Transform,
        bool SkipErased,
        bool RewriteEccInRaw);

    public sealed class NandFixer
    {
        // Async wrapper (no Span inside async => no C#13 needed)
        public Task<FixReport> ProcessAsync(
            BchContext bch,
            NandLayout layout,
            FixOptions opt,
            string inputRaw,
            string outputDataOnly,
            string? outputFixedRaw,
            IProgress<double>? progress,
            IProgress<string>? log,
            CancellationToken ct)
        {
            return Task.Run(() => Process(
                bch, layout, opt,
                inputRaw, outputDataOnly, outputFixedRaw,
                progress, log, ct), ct);
        }

        // Real work (sync, can use Span safely)
        private FixReport Process(
            BchContext bch,
            NandLayout layout,
            FixOptions opt,
            string inputRaw,
            string outputDataOnly,
            string? outputFixedRaw,
            IProgress<double>? progress,
            IProgress<string>? log,
            CancellationToken ct)
        {
            layout.Validate();

            long fsz = new FileInfo(inputRaw).Length;
            if (fsz % layout.RawPage != 0)
                throw new InvalidOperationException($"File size {fsz} is not a multiple of RAW_PAGE={layout.RawPage}");

            long npages = fsz / layout.RawPage;

            long pagesTouched = 0;
            long totalBitflips = 0;
            long uncorrectable = 0;
            long erasedSectors = 0;
            long checkedSectors = 0;

            byte[] raw = new byte[layout.RawPage];
            uint[] errloc = new uint[Math.Max(1, bch.T)];

            using var fin = new FileStream(inputRaw, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            using var fout = new FileStream(outputDataOnly, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            using var foutRaw = outputFixedRaw != null
                ? new FileStream(outputFixedRaw, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20)
                : null;

            for (long p = 0; p < npages; p++)
            {
                ct.ThrowIfCancellationRequested();

                int got = fin.Read(raw, 0, raw.Length);
                if (got != raw.Length) break;

                bool changedPage = false;
                var page = raw.AsSpan(0, layout.PageSize);
                var oob = raw.AsSpan(layout.PageSize, layout.OobSize);

                for (int s = 0; s < layout.EccSteps; s++)
                {
                    var sec = page.Slice(s * layout.SectorSize, layout.SectorSize);
                    var chunk = oob.Slice(s * layout.OobChunk, layout.OobChunk);

                    var oobdata = chunk.Slice(0, opt.OobDataBytes);
                    var eccStored = chunk.Slice(layout.EccOfs, layout.EccLen);

                    if (opt.SkipErased &&
                        BitTransforms.AllFF(sec) &&
                        BitTransforms.AllFF(oobdata) &&
                        BitTransforms.AllFF(eccStored))
                    {
                        erasedSectors++;
                        continue;
                    }

                    checkedSectors++;

                    // msg = sector + oobdata bytes
                    byte[] msg = new byte[layout.SectorSize + opt.OobDataBytes];
                    sec.CopyTo(msg.AsSpan(0, layout.SectorSize));
                    if (opt.OobDataBytes > 0)
                        oobdata.CopyTo(msg.AsSpan(layout.SectorSize, opt.OobDataBytes));

                    // ecc in decoder domain
                    byte[] ecc = eccStored.ToArray();
                    BitTransforms.ApplyInPlace(ecc.AsSpan(), opt.Transform);

                    int nerr = BchCodec.DecodeAndCorrect(bch, msg, ecc, errloc);

                    if (nerr < 0)
                    {
                        uncorrectable++;
                        continue;
                    }

                    totalBitflips += nerr;

                    // copy corrected sector back
                    msg.AsSpan(0, layout.SectorSize).CopyTo(sec);

                    // copy corrected oobdata back
                    if (opt.OobDataBytes > 0)
                        msg.AsSpan(layout.SectorSize, opt.OobDataBytes).CopyTo(oobdata);

                    if (opt.RewriteEccInRaw)
                    {
                        var eccCalc = BchCodec.EncodeEcc(bch, msg, layout.EccLen);
                        BitTransforms.ApplyInPlace(eccCalc.AsSpan(), opt.Transform);

                        if (!eccCalc.AsSpan().SequenceEqual(eccStored))
                        {
                            eccCalc.AsSpan().CopyTo(eccStored);
                            changedPage = true;
                        }
                    }
                }

                if (changedPage) pagesTouched++;

                fout.Write(page);

                if (foutRaw != null)
                    foutRaw.Write(raw, 0, raw.Length);

                if ((p & 0x3F) == 0)
                    progress?.Report((p + 1) * 100.0 / npages);
            }

            progress?.Report(100);

            var report = new FixReport(npages, erasedSectors, checkedSectors, uncorrectable, pagesTouched, totalBitflips);

            log?.Report($"[+] Total pages: {report.TotalPages}");
            log?.Report($"[+] Erased skipped sectors: {report.ErasedSkippedSectors}");
            log?.Report($"[+] Checked sectors: {report.CheckedSectors}");
            log?.Report($"[+] Uncorrectable sectors: {report.UncorrectableSectors}");
            log?.Report($"[+] Modified pages: {report.PagesTouched}");
            log?.Report($"[+] Total corrected bitflips: {report.TotalBitflips}");

            return report;
        }
    }
}
