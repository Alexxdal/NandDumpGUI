using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NandDumpGUI.Core
{
    public readonly record struct DetectResult(uint Poly, int M, int T, int OobData, TransformKind Transform, string Note);

    public sealed class AutoDetector
    {
        public async Task<DetectResult?> DetectAsync(
            string inputRaw,
            NandLayout layout,
            uint poly,
            int maxPagesToSample,
            IProgress<string>? log,
            CancellationToken ct)
        {
            layout.Validate();

            // campione: leggiamo solo N pagine e creiamo lista di "settori+chunk"
            var samples = await Task.Run(() => BuildSamples(inputRaw, layout, maxPagesToSample, ct), ct);
            if (samples.Count < 50)
            {
                log?.Report("[AutoDetect] Campione troppo piccolo.");
                return null;
            }

            int m = (poly != 0) ? BchCodec.DegreeFromPoly(poly) : GuessM(layout);
            if (m < 0) return null;

            // t candidates: intorno a eccLen*8/m
            int tGuess = (int)Math.Round((layout.EccLen * 8.0) / m);
            var tCandidates = new[] { tGuess - 1, tGuess, tGuess + 1, 2, 4, 8, 16 }.Where(x => x > 0 && x <= 64).Distinct().ToArray();

            var oobCandidates = new[] { 0, Math.Min(4, layout.EccOfs), Math.Min(8, layout.EccOfs), layout.EccOfs }.Distinct().ToArray();
            var tfCandidates = new[] { TransformKind.None, TransformKind.Inv, TransformKind.Bitrev, TransformKind.InvBitrev };

            DetectResult? best = null;
            long bestPenalty = long.MaxValue;

            foreach (int t in tCandidates)
            {
                ct.ThrowIfCancellationRequested();

                if (!IsBchParamPlausible(m, t))
                    continue;

                // quick sanity: eccLen ≈ ceil(m*t/8)
                int eccLenExpected = (m * t + 7) / 8;
                if (eccLenExpected != layout.EccLen)
                    continue;

                BchContext bch;
                try
                {
                    bch = BchContext.Create(m, t, poly, swapBits: false);
                }
                catch (InvalidOperationException ex)
                {
                    log?.Report($"[AutoDetect] bch_init failed: m={m} t={t} poly=0x{poly:X} -> {ex.Message}");
                    continue;
                }

                using (bch)
                {
                    foreach (var oobdata in oobCandidates)
                    {
                        if (oobdata < 0 || oobdata > layout.EccOfs) continue;

                        foreach (var tf in tfCandidates)
                        {
                            var score = ScoreSamples(bch, layout, samples, oobdata, tf);
                            long penalty = score.uncorrectable * 1_000_000L + score.bitflips;

                            log?.Report($"[AutoDetect] t={t} oobdata={oobdata} tf={tf} -> uncor={score.uncorrectable} bitflips={score.bitflips} penalty={penalty}");

                            if (penalty < bestPenalty)
                            {
                                bestPenalty = penalty;
                                best = new DetectResult(poly, m, t, oobdata, tf, $"uncor={score.uncorrectable}, bitflips={score.bitflips}");
                            }
                        }
                    }
                }
            }

            return best;
        }

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

        private static int GuessM(NandLayout layout)
        {
            // euristica: m tipicamente 13/14/15; per ECC_LEN da 7 di solito m=14 con t=4
            // qui scegliamo il più probabile: prova 14, poi 13, poi 15
            return 14;
        }

        private sealed class Sample
        {
            public required byte[] Sector;
            public required byte[] Chunk;
        }

        private static List<Sample> BuildSamples(string inputRaw, NandLayout layout, int maxPages, CancellationToken ct)
        {
            long fsz = new FileInfo(inputRaw).Length;
            if (fsz % layout.RawPage != 0) throw new InvalidOperationException("RAW non multiplo di RawPage.");

            long npages = fsz / layout.RawPage;
            long toRead = Math.Min(npages, maxPages);

            var list = new List<Sample>(layout.EccSteps * (int)toRead);
            byte[] raw = new byte[layout.RawPage];

            using var fin = new FileStream(inputRaw, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);

            for (long p = 0; p < toRead; p++)
            {
                ct.ThrowIfCancellationRequested();

                int got = fin.Read(raw, 0, raw.Length);
                if (got != raw.Length) break;

                var page = raw.AsSpan(0, layout.PageSize);
                var oob = raw.AsSpan(layout.PageSize, layout.OobSize);

                for (int s = 0; s < layout.EccSteps; s++)
                {
                    var sec = page.Slice(s * layout.SectorSize, layout.SectorSize);
                    var chunk = oob.Slice(s * layout.OobChunk, layout.OobChunk);

                    list.Add(new Sample { Sector = sec.ToArray(), Chunk = chunk.ToArray() });
                }
            }
            return list;
        }

        private static (long uncorrectable, long bitflips) ScoreSamples(BchContext bch, NandLayout layout, List<Sample> samples, int oobdata, TransformKind tf)
        {
            long uncor = 0;
            long flips = 0;
            uint[] errloc = new uint[Math.Max(1, bch.T)];

            foreach (var smp in samples)
            {
                var sec = smp.Sector.AsSpan();
                var chunk = smp.Chunk.AsSpan();

                var oobSpan = chunk.Slice(0, oobdata);
                var eccStored = chunk.Slice(layout.EccOfs, layout.EccLen);

                byte[] msg = new byte[layout.SectorSize + oobdata];
                sec.CopyTo(msg.AsSpan(0, layout.SectorSize));
                if (oobdata > 0) oobSpan.CopyTo(msg.AsSpan(layout.SectorSize, oobdata));

                byte[] ecc = eccStored.ToArray();
                BitTransforms.ApplyInPlace(ecc.AsSpan(), tf);

                int nerr = BchCodec.DecodeAndCorrect(bch, msg, ecc, errloc);
                if (nerr < 0) uncor++;
                else flips += nerr;

                if (uncor > 500) break;
            }

            return (uncor, flips);
        }
    }
}
