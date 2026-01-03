using NandDumpGUI.Native;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace NandDumpGUI.Core
{
    public sealed record QuickTestCandidate(
        uint Poly,
        int M,
        int T,
        bool SwapBits,
        TransformKind Transform,
        int OobDataBytes)
    {
        public override string ToString()
            => $"poly=0x{Poly:X} (m={M}) t={T} swapBits={SwapBits} tf={Transform} oobdata={OobDataBytes}";
    }

    public sealed record QuickTestStats(long Checked, long Ok, long Uncorrectable, long TotalBitflips)
    {
        public double UncorrectableRatio => Checked <= 0 ? 1.0 : (double)Uncorrectable / Checked;

        public override string ToString()
            => $"checked={Checked} ok={Ok} uncorrectable={Uncorrectable} ({UncorrectableRatio:P1}) bitflips={TotalBitflips}";
    }

    public sealed record QuickTestResult(
        QuickTestCandidate Best,
        QuickTestStats BestStats,
        IReadOnlyList<(QuickTestCandidate Cand, QuickTestStats Stats)> Ranked);

    public static class QuickTester
    {
        private static int DegreeFromPoly(uint poly)
        {
            if (poly == 0) throw new ArgumentException("poly=0 is invalid");
            return 31 - BitOperations.LeadingZeroCount(poly);
        }

        private static int EccBytes(int m, int t) => (m * t + 7) / 8;

        // bch_init (kernel-style) fails when m*t > (2^m - 1). Filter impossible combos early.
        private static bool IsBchParamPlausible(int m, int t)
        {
            if (t <= 0) return false;
            // Most NAND BCH uses m in [5..15]. Keep it conservative to avoid huge allocations in native.
            if (m < 5 || m > 15) return false;

            long n = (1L << m) - 1;        // code length in bits
            long eccBits = (long)m * t;   // required ecc bits
            return eccBits <= n;
        }


        // SAFE filter: native expects exactly BCH_ECC_BYTES(m,t). If we mismatch, it's dangerous.
        private static bool IsCandidateSafe(uint poly, int t, NandLayout layout)
        {
            int m = DegreeFromPoly(poly);
            if (!IsBchParamPlausible(m, t))
                return false;
            return EccBytes(m, t) == layout.EccLen;
        }

        private static List<long> PickSamplePages(long npages, int count, int seed)
        {
            if (npages <= 0 || count <= 0) return new List<long>();

            if (npages <= count)
                return Enumerable.Range(0, (int)npages).Select(i => (long)i).ToList();

            var rng = new Random(seed);
            var set = new HashSet<long>();
            while (set.Count < count)
            {
                long idx = (long)rng.NextInt64(0, npages);
                set.Add(idx);
            }
            return set.OrderBy(x => x).ToList();
        }

        // Backward-compatible overload: single t
        public static Task<QuickTestResult> RunAsync(
            string inputRaw,
            NandLayout layout,
            int t,
            IReadOnlyList<uint> candidatePolys,
            IReadOnlyList<TransformKind> transforms,
            IReadOnlyList<int> oobDataCandidates,
            int pagesToSample = 256,
            int seed = 123,
            bool trySwapBits = true,
            int maxCandidates = 2000,
            IProgress<string>? log = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            return RunAsync(
                inputRaw, layout,
                candidateTs: new[] { t },
                candidatePolys, transforms, oobDataCandidates,
                pagesToSample, seed, trySwapBits, maxCandidates,
                log, progress, ct);
        }

        // Extended overload: multiple t
        public static Task<QuickTestResult> RunAsync(
            string inputRaw,
            NandLayout layout,
            IReadOnlyList<int> candidateTs,
            IReadOnlyList<uint> candidatePolys,
            IReadOnlyList<TransformKind> transforms,
            IReadOnlyList<int> oobDataCandidates,
            int pagesToSample = 256,
            int seed = 123,
            bool trySwapBits = true,
            int maxCandidates = 2000,
            IProgress<string>? log = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            return Task.Run(() =>
                Run(inputRaw, layout, candidateTs, candidatePolys, transforms, oobDataCandidates,
                    pagesToSample, seed, trySwapBits, maxCandidates, log, progress, ct),
                ct);
        }

        private static QuickTestResult Run(
            string inputRaw,
            NandLayout layout,
            IReadOnlyList<int> candidateTs,
            IReadOnlyList<uint> candidatePolys,
            IReadOnlyList<TransformKind> transforms,
            IReadOnlyList<int> oobDataCandidates,
            int pagesToSample,
            int seed,
            bool trySwapBits,
            int maxCandidates,
            IProgress<string>? log,
            IProgress<double>? progress,
            CancellationToken ct)
        {
            layout.Validate();

            long fsz = new FileInfo(inputRaw).Length;
            if (fsz % layout.RawPage != 0)
                throw new InvalidOperationException($"File size {fsz} is not a multiple of RAW_PAGE={layout.RawPage}.");

            long npages = fsz / layout.RawPage;
            var samplePages = PickSamplePages(npages, pagesToSample, seed);

            log?.Report($"[QT] Sampling {samplePages.Count} pages out of {npages}...");

            // Preload sampled pages into RAM
            var sampled = new List<byte[]>(samplePages.Count);
            using (var fs = new FileStream(inputRaw, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.RandomAccess))
            {
                foreach (var p in samplePages)
                {
                    ct.ThrowIfCancellationRequested();
                    fs.Seek(p * layout.RawPage, SeekOrigin.Begin);

                    byte[] raw = new byte[layout.RawPage];
                    int got = fs.Read(raw, 0, raw.Length);
                    if (got != raw.Length) break;
                    sampled.Add(raw);
                }
            }

            // Normalize candidate lists
            var ts = candidateTs.Where(x => x > 0).Distinct().ToArray();
            var polys = candidatePolys.Distinct().ToArray();
            var tfs = transforms.Distinct().ToArray();
            var oobs = oobDataCandidates.Distinct()
                .Where(v => v >= 0 && v <= layout.EccOfs && v <= layout.OobChunk)
                .OrderBy(v => v)
                .ToArray();

            // Build candidates (SAFE filter: EccBytes(m,t) == layout.EccLen)
            var candidates = new List<QuickTestCandidate>();

            foreach (var poly in polys)
            {
                int m;
                try { m = DegreeFromPoly(poly); }
                catch { continue; }

                foreach (var t in ts)
                {
                    if (!IsCandidateSafe(poly, t, layout))
                        continue;

                    foreach (var swapBits in (trySwapBits ? new[] { false, true } : new[] { false }))
                    {
                        foreach (var tf in tfs)
                        {
                            foreach (var oobData in oobs)
                            {
                                candidates.Add(new QuickTestCandidate(poly, m, t, swapBits, tf, oobData));
                            }
                        }
                    }
                }
            }

            if (candidates.Count == 0)
                throw new InvalidOperationException("No safe candidates. (Try adjusting ECC_LEN in layout or provide compatible polys/t values.)");

            // Cap candidates if too many (keeps quick test “quick”)
            if (candidates.Count > maxCandidates)
            {
                log?.Report($"[QT] Candidate sets = {candidates.Count}, capping to {maxCandidates} for speed...");
                var rng = new Random(seed ^ 0x5A17);
                candidates = candidates.OrderBy(_ => rng.Next()).Take(maxCandidates).ToList();
            }

            log?.Report($"[QT] Testing {candidates.Count} candidate parameter sets...");

            var results = new List<(QuickTestCandidate Cand, QuickTestStats Stats)>(candidates.Count);

            for (int i = 0; i < candidates.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var cand = candidates[i];

                BchContext bch;
                try
                {
                    bch = BchContext.Create(cand.M, cand.T, cand.Poly, swapBits: cand.SwapBits);
                }
                catch (InvalidOperationException ex)
                {
                    log?.Report($"[QT] bch_init failed: m={cand.M} t={cand.T} poly=0x{cand.Poly:X} swapBits={cand.SwapBits} -> {ex.Message}");
                    continue;
                }

                using (bch)
                {
                    var stats = EvaluateCandidate(bch, layout, cand, sampled, ct);

                    results.Add((cand, stats));

                    if ((i & 0x7) == 0)
                        progress?.Report((i + 1) * 100.0 / candidates.Count);

                    log?.Report($"[QT] {cand} => {stats}");
                }
            }

            if (results.Count == 0)
                throw new InvalidOperationException("All BCH candidates failed to initialize (bch_init). Try different polys / t values or check that ECC_LEN matches the real NAND ECC.");

            // Rank: lowest uncorrectable ratio, then highest ok, then highest bitflips
            var ranked = results
                .OrderBy(r => r.Stats.UncorrectableRatio)
                .ThenByDescending(r => r.Stats.Ok)
                .ThenByDescending(r => r.Stats.TotalBitflips)
                .ToList();

            var best = ranked[0];
            log?.Report($"[QT] BEST: {best.Cand} => {best.Stats}");
            progress?.Report(100);

            return new QuickTestResult(best.Cand, best.Stats, ranked);
        }

        private static QuickTestStats EvaluateCandidate(
            BchContext bch,
            NandLayout layout,
            QuickTestCandidate cand,
            List<byte[]> sampledPages,
            CancellationToken ct)
        {
            long checkedSectors = 0;
            long ok = 0;
            long uncorrectable = 0;
            long bitflips = 0;

            uint[] errloc = new uint[Math.Max(1, cand.T)];

            foreach (var raw in sampledPages)
            {
                ct.ThrowIfCancellationRequested();

                var page = raw.AsSpan(0, layout.PageSize);
                var oob = raw.AsSpan(layout.PageSize, layout.OobSize);

                for (int s = 0; s < layout.EccSteps; s++)
                {
                    var sec = page.Slice(s * layout.SectorSize, layout.SectorSize);
                    var chunk = oob.Slice(s * layout.OobChunk, layout.OobChunk);

                    var oobdata = chunk.Slice(0, cand.OobDataBytes);
                    var eccStored = chunk.Slice(layout.EccOfs, layout.EccLen);

                    // skip erased
                    if (BitTransforms.AllFF(sec) && BitTransforms.AllFF(oobdata) && BitTransforms.AllFF(eccStored))
                        continue;

                    checkedSectors++;

                    byte[] msg = new byte[layout.SectorSize + cand.OobDataBytes];
                    sec.CopyTo(msg.AsSpan(0, layout.SectorSize));
                    if (cand.OobDataBytes > 0)
                        oobdata.CopyTo(msg.AsSpan(layout.SectorSize, cand.OobDataBytes));

                    byte[] ecc = eccStored.ToArray();
                    BitTransforms.ApplyInPlace(ecc.AsSpan(), cand.Transform);

                    int nerr = BchNative.bch_decode(
                        bch.Handle,
                        msg,
                        (uint)msg.Length,
                        ecc,
                        null,
                        IntPtr.Zero,
                        errloc);

                    if (nerr >= 0)
                    {
                        ok++;
                        bitflips += nerr;
                    }
                    else
                    {
                        uncorrectable++;
                    }
                }
            }

            return new QuickTestStats(checkedSectors, ok, uncorrectable, bitflips);
        }
    }
}
