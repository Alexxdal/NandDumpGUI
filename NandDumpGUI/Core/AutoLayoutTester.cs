using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace NandDumpGUI.Core
{
    public sealed record AutoLayoutCandidate(
        int PageSize,
        int OobSize,
        int SectorSize,
        int OobChunk,
        long Offset,
        double Score)
    {
        public int RawPage => PageSize + OobSize;
        public int Steps => PageSize / SectorSize;

        public override string ToString()
            => $"page={PageSize} oob={OobSize} sector={SectorSize} chunk={OobChunk} raw={RawPage} offset={Offset} score={Score:F3}";
    }

    public sealed record AutoLayoutBest(
        NandLayout Layout,
        long Offset,
        QuickTestCandidate BestParams,
        QuickTestStats Stats,
        double LayoutScore);

    public sealed record AutoLayoutResult(
        AutoLayoutBest Best,
        IReadOnlyList<AutoLayoutBest> Top);

    public static class AutoLayoutTester
    {
        // Layout comuni (espandibile)
        private static readonly (int Page, int[] Oobs)[] CommonPageOob =
        {
            (  512, new[] { 16 }),
            ( 2048, new[] { 64, 128 }),
            ( 4096, new[] { 128, 224, 256 }),
            ( 8192, new[] { 256, 448, 640 }),
            (16384, new[] { 512, 1024 }),
        };

        private static readonly int[] CommonSectorSizes = { 512, 1024 };

        private static readonly TransformKind[] DefaultTransforms =
        {
            TransformKind.None,
            TransformKind.Inv,
            TransformKind.Bitrev,
            TransformKind.InvBitrev
        };

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

        private static int CountFF(ReadOnlySpan<byte> s)
        {
            int n = 0;
            foreach (byte b in s) if (b == 0xFF) n++;
            return n;
        }

        private static List<long> PickSamplePages(long npages, int count, int seed)
        {
            if (npages <= 0) return new List<long>();
            if (count <= 0) return new List<long>();

            if (npages <= count)
                return Enumerable.Range(0, (int)npages).Select(i => (long)i).ToList();

            var rng = new Random(seed);
            var set = new HashSet<long>();
            while (set.Count < count)
                set.Add((long)rng.NextInt64(0, npages));

            return set.OrderBy(x => x).ToList();
        }

        private static IEnumerable<long> CandidateOffsets(long fileSize, int rawPage)
        {
            // 1) offset 0 (classico)
            yield return 0;

            // 2) offset = remainder (tipico header)
            long rem = fileSize % rawPage;
            if (rem != 0 && rem < rawPage) yield return rem;

            // 3) qualche offset multiplo di 512 nei primi KB (molti header sono così)
            int max = Math.Min(rawPage, 4096);
            for (int off = 512; off < max; off += 512)
                yield return off;

            // distinct
        }

        private static double ScoreLayout(string inputRaw, AutoLayoutCandidate cand, int pagesToScore, int seed, CancellationToken ct)
        {
            long fsz = new FileInfo(inputRaw).Length;

            if (cand.Offset < 0) return double.NegativeInfinity;
            if (fsz - cand.Offset < cand.RawPage) return double.NegativeInfinity;

            long npages = (fsz - cand.Offset) / cand.RawPage;
            if (npages <= 0) return double.NegativeInfinity;

            var samplePages = PickSamplePages(npages, pagesToScore, seed);

            long ffOob = 0, totOob = 0;
            long ffPage = 0, totPage = 0;

            byte[] buf = new byte[cand.RawPage];

            using var fs = new FileStream(inputRaw, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.RandomAccess);

            foreach (var p in samplePages)
            {
                ct.ThrowIfCancellationRequested();

                fs.Seek(cand.Offset + p * cand.RawPage, SeekOrigin.Begin);
                int got = fs.Read(buf, 0, buf.Length);
                if (got != buf.Length) break;

                var page = buf.AsSpan(0, cand.PageSize);
                var oob = buf.AsSpan(cand.PageSize, cand.OobSize);

                ffPage += CountFF(page);
                totPage += page.Length;

                ffOob += CountFF(oob);
                totOob += oob.Length;
            }

            if (totOob == 0 || totPage == 0) return double.NegativeInfinity;

            // Heuristica: nell’OOB spesso molti 0xFF (più che nel data)
            double rOob = (double)ffOob / totOob;
            double rPage = (double)ffPage / totPage;

            // Score positivo = OOB molto più "vuoto" (FF) del data
            return rOob - rPage;
        }

        private static List<byte[]> LoadSampledPagesAligned(string inputRaw, AutoLayoutCandidate cand, int pagesToSample, int seed, CancellationToken ct)
        {
            long fsz = new FileInfo(inputRaw).Length;
            long npages = (fsz - cand.Offset) / cand.RawPage;
            var samplePages = PickSamplePages(npages, pagesToSample, seed);

            var sampled = new List<byte[]>(samplePages.Count);

            using var fs = new FileStream(inputRaw, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.RandomAccess);

            foreach (var p in samplePages)
            {
                ct.ThrowIfCancellationRequested();

                fs.Seek(cand.Offset + p * cand.RawPage, SeekOrigin.Begin);

                byte[] raw = new byte[cand.RawPage];
                int got = fs.Read(raw, 0, raw.Length);
                if (got != raw.Length) break;

                sampled.Add(raw);
            }

            return sampled;
        }

        private static QuickTestStats EvaluateCandidate(
            BchContext bch,
            NandLayout layout,
            TransformKind tf,
            int oobDataBytes,
            List<byte[]> sampledPages,
            CancellationToken ct)
        {
            long checkedSectors = 0;
            long ok = 0;
            long uncorrectable = 0;
            long bitflips = 0;

            uint[] errloc = new uint[Math.Max(1, bch.T)];

            foreach (var raw in sampledPages)
            {
                ct.ThrowIfCancellationRequested();

                if (raw.Length < layout.RawPage) continue;

                var page = raw.AsSpan(0, layout.PageSize);
                var oob = raw.AsSpan(layout.PageSize, layout.OobSize);

                for (int s = 0; s < layout.EccSteps; s++)
                {
                    var sec = page.Slice(s * layout.SectorSize, layout.SectorSize);
                    var chunk = oob.Slice(s * layout.OobChunk, layout.OobChunk);

                    var oobdata = chunk.Slice(0, oobDataBytes);
                    var eccStored = chunk.Slice(layout.EccOfs, layout.EccLen);

                    // skip erased
                    if (BitTransforms.AllFF(sec) && BitTransforms.AllFF(oobdata) && BitTransforms.AllFF(eccStored))
                        continue;

                    checkedSectors++;

                    byte[] msg = new byte[layout.SectorSize + oobDataBytes];
                    sec.CopyTo(msg.AsSpan(0, layout.SectorSize));
                    if (oobDataBytes > 0) oobdata.CopyTo(msg.AsSpan(layout.SectorSize, oobDataBytes));

                    byte[] ecc = eccStored.ToArray();
                    BitTransforms.ApplyInPlace(ecc.AsSpan(), tf);

                    int nerr = Native.BchNative.bch_decode(
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

        public static Task<AutoLayoutResult> RunAsync(
            string inputRaw,
            IReadOnlyList<uint> candidatePolys,
            IReadOnlyList<int> tCandidates,
            IReadOnlyList<TransformKind>? transforms = null,
            int pagesToScoreLayouts = 64,
            int pagesToSampleForParams = 128,
            int maxLayoutsToTest = 6,
            int seed = 123,
            IProgress<string>? log = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            return Task.Run(() =>
                RunCore(
                    inputRaw,
                    candidatePolys,
                    tCandidates,
                    transforms ?? DefaultTransforms,
                    pagesToScoreLayouts,
                    pagesToSampleForParams,
                    maxLayoutsToTest,
                    seed,
                    log,
                    progress,
                    ct),
                ct);
        }

        private static AutoLayoutResult RunCore(
            string inputRaw,
            IReadOnlyList<uint> candidatePolys,
            IReadOnlyList<int> tCandidates,
            IReadOnlyList<TransformKind> transforms,
            int pagesToScoreLayouts,
            int pagesToSampleForParams,
            int maxLayoutsToTest,
            int seed,
            IProgress<string>? log,
            IProgress<double>? progress,
            CancellationToken ct)
        {
            long fsz = new FileInfo(inputRaw).Length;

            // 1) genera layout candidati + offset, e falli “scorare”
            var scored = new List<AutoLayoutCandidate>();

            foreach (var (page, oobs) in CommonPageOob)
            {
                foreach (var oob in oobs)
                {
                    foreach (var sector in CommonSectorSizes)
                    {
                        if (sector <= 0) continue;
                        if (page % sector != 0) continue;

                        int steps = page / sector;
                        if (steps <= 0) continue;

                        // chunk "per step" (molto comune)
                        if (oob % steps != 0) continue;
                        int chunk = oob / steps;

                        // scarta chunk strani
                        if (chunk < 8 || chunk > 128) continue;

                        int rawPage = page + oob;

                        foreach (var off in CandidateOffsets(fsz, rawPage).Distinct())
                        {
                            ct.ThrowIfCancellationRequested();

                            var cand = new AutoLayoutCandidate(page, oob, sector, chunk, off, Score: 0);
                            double score = ScoreLayout(inputRaw, cand, pagesToScoreLayouts, seed, ct);

                            if (double.IsNegativeInfinity(score)) continue;

                            scored.Add(cand with { Score = score });
                        }
                    }
                }
            }

            if (scored.Count == 0)
                throw new InvalidOperationException("No plausible NAND layouts found. The file may be too small or not a NAND RAW dump.");

            scored = scored
                .OrderByDescending(x => x.Score)
                .Take(Math.Max(2, maxLayoutsToTest * 3)) // tieni un po’ più di margine
                .ToList();

            log?.Report("[QT] Layout candidates (top scores):");
            foreach (var c in scored.Take(10))
                log?.Report("   " + c);

            // Heuristica: score molto basso => probabile dump senza OOB
            if (scored[0].Score < 0.05)
            {
                log?.Report("[QT] WARNING: OOB does not look sparse (0xFF-heavy). This dump may be data-only (no spare/OOB). ECC fixing may be impossible.");
            }

            // 2) per i migliori layout, prova parametri BCH e scegli il migliore globale
            var bestPerLayout = new List<AutoLayoutBest>();

            int testedLayouts = 0;
            foreach (var baseCand in scored.OrderByDescending(x => x.Score).Take(maxLayoutsToTest))
            {
                ct.ThrowIfCancellationRequested();

                testedLayouts++;
                progress?.Report((testedLayouts - 1) * 100.0 / maxLayoutsToTest);

                log?.Report($"[QT] Testing params on layout: {baseCand}");

                // carica campione pagine già allineate per questo layout
                var sampled = LoadSampledPagesAligned(inputRaw, baseCand, pagesToSampleForParams, seed, ct);

                AutoLayoutBest? bestHere = TestParamsForLayout(baseCand, candidatePolys, tCandidates, transforms, sampled, log, ct);
                if (bestHere != null)
                    bestPerLayout.Add(bestHere);
            }

            if (bestPerLayout.Count == 0)
                throw new InvalidOperationException("No working parameter set found. This could mean: wrong layouts, missing OOB, or unsupported ECC scheme.");

            var ranked = bestPerLayout
                .OrderBy(x => x.Stats.UncorrectableRatio)
                .ThenByDescending(x => x.LayoutScore)
                .ThenByDescending(x => x.Stats.Ok)
                .ThenByDescending(x => x.Stats.TotalBitflips)
                .ToList();

            progress?.Report(100);

            return new AutoLayoutResult(ranked[0], ranked.Take(5).ToList());
        }

        private static AutoLayoutBest? TestParamsForLayout(
            AutoLayoutCandidate baseCand,
            IReadOnlyList<uint> candidatePolys,
            IReadOnlyList<int> tCandidates,
            IReadOnlyList<TransformKind> transforms,
            List<byte[]> sampled,
            IProgress<string>? log,
            CancellationToken ct)
        {
            QuickTestCandidate? bestCand = null;
            QuickTestStats bestStats = new(0, 0, long.MaxValue, 0);
            NandLayout bestLayout = default;

            // piccola lista di eccOfs “tipici”
            static IEnumerable<int> CommonEccOfs(int chunk, int eccLen)
            {
                // “end-packed” (molto comune)
                int end = chunk - eccLen;
                if (end >= 0) yield return end;
                if (end - 1 >= 0) yield return end - 1;

                // offset classici (Broadcom spesso 9)
                foreach (int x in new[] { 0, 1, 2, 4, 8, 9, 10, 12, 16 })
                    if (x >= 0 && x + eccLen <= chunk) yield return x;
            }

            foreach (int t in tCandidates.Distinct().Where(x => x > 0).OrderBy(x => x))
            {
                ct.ThrowIfCancellationRequested();

                foreach (uint poly in candidatePolys.Distinct())
                {
                    ct.ThrowIfCancellationRequested();

                    int m;
                    try { m = DegreeFromPoly(poly); }
                    catch { continue; }

                    int eccLen = EccBytes(m, t);
                    if (eccLen <= 0) continue;
                    if (eccLen > baseCand.OobChunk) continue;

                    foreach (int eccOfs in CommonEccOfs(baseCand.OobChunk, eccLen).Distinct())
                    {
                        ct.ThrowIfCancellationRequested();

                        NandLayout layout;
                        try
                        {
                            layout = new NandLayout(
                                baseCand.PageSize,
                                baseCand.OobSize,
                                baseCand.SectorSize,
                                baseCand.OobChunk,
                                eccOfs,
                                eccLen);
                            layout.Validate();
                        }
                        catch
                        {
                            continue;
                        }

                        // oobdata: prova 0 e “fino a prima dell’ECC”
                        var oobDataCandidates = new[] { 0, eccOfs, eccOfs - 1, eccOfs - 2 }
                            .Where(x => x >= 0 && x <= eccOfs)
                            .Distinct()
                            .ToList();

                        if (!IsBchParamPlausible(m, t))
                        {
                            log?.Report($"[QT]   skip invalid BCH: m={m} t={t} poly=0x{poly:X} (m*t > 2^m-1)");
                            continue;
                        }

                        BchContext bch;
                        try
                        {
                            bch = BchContext.Create(m, t, poly, swapBits: false);
                        }
                        catch (InvalidOperationException ex)
                        {
                            log?.Report($"[QT]   bch_init failed: m={m} t={t} poly=0x{poly:X} -> {ex.Message}");
                            continue;
                        }

                        using (bch)
                        {
                            foreach (var tf in transforms)
                            {
                                foreach (int oobData in oobDataCandidates)
                                {
                                    ct.ThrowIfCancellationRequested();

                                    var stats = EvaluateCandidate(bch, layout, tf, oobData, sampled, ct);

                                    // “vince” uncorrectable ratio minore
                                    bool better =
                                        stats.Checked > 0 &&
                                        stats.UncorrectableRatio < bestStats.UncorrectableRatio;

                                    // tie-break
                                    if (!better && stats.Checked > 0 && stats.UncorrectableRatio == bestStats.UncorrectableRatio)
                                        better = stats.Ok > bestStats.Ok;

                                    if (better)
                                    {
                                        bestStats = stats;
                                        bestLayout = layout;
                                        bestCand = new QuickTestCandidate(poly, m, t, false, tf, oobData);

                                        log?.Report($"[QT]   new best: {bestCand} | eccOfs={eccOfs} eccLen={eccLen} => {stats}");
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (bestLayout == null || bestCand == null || bestStats.Checked <= 0)
                return null;

            return new AutoLayoutBest(bestLayout, baseCand.Offset, bestCand, bestStats, baseCand.Score);
        }
    }
}
