using GameHelper;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SekhemaHelper
{
    // Current/Max Honour for Smart Weights. Honour has NO fixed offset-chain (find_ptr_to on the model
    // object = 0 everywhere; the honour-bar widget renders the number transiently without storing a
    // pointer to it). So we locate it by SIGNATURE scan + cache.
    //
    // Honour model layout (verified live 2026-06-23, docs §4.7.8):
    //   +0x00 i32 current      +0x04 i32 current (duplicate)   +0x08 i32 max
    //   +0x0C f32 fill (= current/max)   +0x10 f32 fill (smoothed)
    // The redundant current + dual fill floats make the signature specific.
    internal static class HonourReader
    {
        public static int Honour { get; private set; } = -1;
        public static int MaxHonour { get; private set; } = -1;
        public static string Status { get; private set; } = "idle";
        public static int CandidateCount { get; private set; }

        public const int StructBytes = 0x14;
        public const int MaxHonourValue = 100000;

        private static IntPtr cached;
        private static int scanning;
        private static long lastScanTicks;
        private static readonly long RescanCooldown = TimeSpan.FromSeconds(2).Ticks;

        private const int ChunkSize = 8 * 1024 * 1024;
        private const long DensityCheckMinSize = 4 * 1024 * 1024;
        private const int DensitySamples = 16;
        private const int DensitySampleBytes = 8 * 1024;

        // Honour realistic bounds. Min current = 1 (honour 0 ends the trial) — excludes the huge flood of
        // zero/small runs. Min max = 50 (even early floors have hundreds of max honour).
        public const int MinHonourValue = 50;
        public const int MinCurrent = 1;

        // Validate the 5-field honour signature at `b` (StructBytes). Returns current/max on success.
        // STRICT: cur>=1 (no zero flood), cur==cur2, and fill(+0xC) must NEAR-EXACTLY equal cur/max
        // (the game stores it as the computed ratio) — this tight coupling is what makes it specific.
        private static bool ParseSignature(ReadOnlySpan<byte> b, out int cur, out int max)
        {
            cur = 0; max = 0;
            if (b.Length < StructBytes)
                return false;
            int c = BitConverter.ToInt32(b.Slice(0, 4));
            int c2 = BitConverter.ToInt32(b.Slice(4, 4));
            int m = BitConverter.ToInt32(b.Slice(8, 4));
            if (m < MinHonourValue || m > MaxHonourValue) return false;
            if (c < MinCurrent || c > m) return false;
            if (c != c2) return false;
            float fill = BitConverter.ToSingle(b.Slice(0xC, 4));
            float fill2 = BitConverter.ToSingle(b.Slice(0x10, 4));
            // Reject NaN/Inf FIRST — NaN comparisons are always false, so a non-finite fill would slip
            // through every range/tolerance check below and flood the results.
            if (!float.IsFinite(fill) || !float.IsFinite(fill2)) return false;
            if (fill < 0.0005f || fill > 1.001f) return false;
            float r = (float)c / m;
            if (Math.Abs(fill - r) > 0.002f) return false;       // ~equals the computed ratio
            if (Math.Abs(fill * m - c) > 2f) return false;       // fill PRECISELY encodes cur/max
            if (Math.Abs(fill2 - r) > 0.05f) return false;       // smoothed/animated copy
            cur = c; max = m;
            return true;
        }

        private static bool ReadValidate(IntPtr addr, out int cur, out int max)
        {
            cur = 0; max = 0;
            if (addr == IntPtr.Zero) return false;
            var b = Mem.ReadBytes(addr, StructBytes);
            return b.Length >= StructBytes && ParseSignature(b, out cur, out max);
        }

        // Call once per frame from a Sanctum context. Cheap when cached; schedules a background scan
        // otherwise. Honour stays -1 while unresolved OR ambiguous (more than one candidate).
        public static void Poll()
        {
            if (cached != IntPtr.Zero && ReadValidate(cached, out int c, out int m))
            {
                Honour = c; MaxHonour = m;
                Status = $"ok @0x{cached.ToInt64():X} {c}/{m}";
                return;
            }
            cached = IntPtr.Zero; Honour = -1; MaxHonour = -1;

            long now = DateTime.UtcNow.Ticks;
            if (Volatile.Read(ref scanning) != 0) { Status = "scanning…"; return; }
            if (now - lastScanTicks < RescanCooldown) { Status = $"waiting (cooldown){(CandidateCount > 1 ? $", ambiguous {CandidateCount}" : "")}"; return; }
            StartScan();
        }

        private static void StartScan()
        {
            if (Interlocked.CompareExchange(ref scanning, 1, 0) != 0) return;
            lastScanTicks = DateTime.UtcNow.Ticks;
            Status = "scanning…";
            Task.Run(() =>
            {
                try
                {
                    var found = ScanCandidates(out _, out _);
                    CandidateCount = found.Count;
                    // The signature also matches coincidental {N,N,N,1.0f} (full) and tiny-ratio structs.
                    // Honour is the unique PARTIAL bar (cur<max, mid fill). Pin it only when that's unique;
                    // once pinned, the cached address keeps validating even if honour later goes full.
                    var pick = SelectHonour(found);
                    cached = pick?.Addr ?? IntPtr.Zero;
                }
                catch { }
                finally { Volatile.Write(ref scanning, 0); }
            });
        }

        public readonly struct Cand
        {
            public readonly IntPtr Addr; public readonly int Cur, Max; public readonly float Fill;
            public Cand(IntPtr a, int c, int m, float f) { Addr = a; Cur = c; Max = m; Fill = f; }
        }

        // Pick honour from the signature candidates: the UNIQUE partial bar (cur<max, mid fill). Full
        // bars (fill≈1, e.g. {N,N,N,1.0f} coincidences) and tiny-ratio junk are excluded. Returns null
        // if not exactly one — honour then stays unknown (threshold rules skip; effect rules still run).
        public static Cand? SelectHonour(List<Cand> cands)
        {
            Cand? pick = null;
            int n = 0;
            foreach (var c in cands)
                if (c.Cur < c.Max && c.Fill >= 0.01f && c.Fill <= 0.995f) { pick = c; n++; }
            return n == 1 ? pick : null;
        }

        // Hard cap so a too-loose signature can never produce a runaway result / giant dump file.
        public const int MaxCandidates = 4000;

        // Heap scan for all objects matching the honour signature. Parallel + density pre-filter.
        // `capped` = true if the cap was hit (signature still too loose — tighten before trusting).
        public static List<Cand> ScanCandidates(out long elapsedMs, out bool capped)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var hits = new System.Collections.Concurrent.ConcurrentBag<Cand>();
            int count = 0, stop = 0;
            elapsedMs = 0; capped = false;
            if (!Mem.GetModuleRange(out ulong modBase, out ulong modEnd))
                return new List<Cand>();
            long scanned = 0;
            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Math.Min(Environment.ProcessorCount, 8)) };

            Parallel.ForEach(Mem.EnumerateHeapRegions(), po, region =>
            {
                if (Volatile.Read(ref stop) != 0) return;
                if (region.Size >= DensityCheckMinSize && !HasModulePointers(region, modBase, modEnd, ref scanned))
                    return;
                long off = 0;
                while (off < region.Size)
                {
                    if (Volatile.Read(ref stop) != 0) return;
                    // +StructBytes lookahead so a struct starting near the chunk end is fully readable;
                    // emit only hits whose start is in [0, ChunkSize) to avoid double-counting the overlap.
                    int want = (int)Math.Min(ChunkSize + StructBytes, region.Size - off);
                    var buf = Mem.ReadChunk((IntPtr)(region.Base.ToInt64() + off), want);
                    Interlocked.Add(ref scanned, buf.Length);
                    var ints = MemoryMarshal.Cast<byte, int>(buf.AsSpan());
                    int emitLimit = (int)(Math.Min(ChunkSize, want) / 4);   // start must be in this chunk
                    int qMax = Math.Min(emitLimit, ints.Length - 5);        // need ints[q..q+4]
                    long regBase = region.Base.ToInt64() + off;
                    for (int q = 0; q < qMax; q++)
                    {
                        // cheapest, most selective rejects first (kill ~all positions before any float work):
                        int c = ints[q];
                        if (c < MinCurrent) continue;          // cur >= 1 (no zero flood)
                        if (ints[q + 1] != c) continue;        // cur == cur2 (rare in random memory)
                        int m = ints[q + 2];
                        if (m < MinHonourValue || m > MaxHonourValue || c > m) continue;
                        float fill = BitConverter.Int32BitsToSingle(ints[q + 3]);
                        if (!float.IsFinite(fill) || fill < 0.0005f || fill > 1.001f) continue;
                        float r = (float)c / m;
                        if (MathF.Abs(fill - r) > 0.002f) continue;
                        if (MathF.Abs(fill * m - c) > 2f) continue;
                        float fill2 = BitConverter.Int32BitsToSingle(ints[q + 4]);
                        if (!float.IsFinite(fill2) || MathF.Abs(fill2 - r) > 0.05f) continue;

                        hits.Add(new Cand((IntPtr)(regBase + (long)q * 4), c, m, fill));
                        if (Interlocked.Increment(ref count) >= MaxCandidates) { Volatile.Write(ref stop, 1); return; }
                    }
                    off += ChunkSize;
                }
            });

            sw.Stop();
            elapsedMs = sw.ElapsedMilliseconds;
            capped = Volatile.Read(ref stop) != 0;
            return new List<Cand>(hits);
        }

        // DEV: scan + write all candidates to a file (verify honour is uniquely identified by signature).
        public static void DumpCandidates(string outPath)
        {
            var list = ScanCandidates(out long ms, out bool capped);
            var pick = SelectHonour(list);
            var sb = new StringBuilder();
            sb.AppendLine($"# Honour signature scan: candidates={list.Count} elapsedMs={ms}{(capped ? " (CAPPED — signature still too loose)" : "")}");
            sb.AppendLine("# layout +0 cur, +4 cur, +8 max, +0xC fill(=cur/max), +0x10 fill2");
            sb.AppendLine($"# SELECTED honour (unique partial bar) = {(pick.HasValue ? $"0x{pick.Value.Addr.ToInt64():X} {pick.Value.Cur}/{pick.Value.Max}" : "NONE (0 or >1 partial bars)")}");
            sb.AppendLine();
            foreach (var c in list)
            {
                bool isPick = pick.HasValue && pick.Value.Addr == c.Addr;
                sb.AppendLine($"{(isPick ? ">> " : "   ")}addr=0x{c.Addr.ToInt64():X}  {c.Cur}/{c.Max}  fill={c.Fill:F4}");
            }
            System.IO.File.WriteAllText(outPath, sb.ToString());
        }

        // DEV: find heap objects holding both `cur` and `max` within a small window (locate by value).
        private const int PairWindow = 0x100;
        public static void ScanForPair(int cur, int max, string outPath)
        {
            var sb = new StringBuilder();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            sb.AppendLine($"# Honour locator by value: cur={cur} (0x{cur:X}) max={max} (0x{max:X}) window=0x{PairWindow:X}");
            if (!Mem.GetModuleRange(out ulong modBase, out ulong modEnd))
            { sb.AppendLine("# no module"); System.IO.File.WriteAllText(outPath, sb.ToString()); return; }

            var hits = new System.Collections.Concurrent.ConcurrentBag<string>();
            long scanned = 0; int capped = 0; const int MaxHits = 400;
            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Math.Min(Environment.ProcessorCount, 8)) };
            Parallel.ForEach(Mem.EnumerateHeapRegions(), po, region =>
            {
                if (Volatile.Read(ref capped) != 0) return;
                if (region.Size >= DensityCheckMinSize && !HasModulePointers(region, modBase, modEnd, ref scanned)) return;
                long off = 0;
                while (off < region.Size)
                {
                    if (Volatile.Read(ref capped) != 0) return;
                    int want = (int)Math.Min(ChunkSize, region.Size - off);
                    var buf = Mem.ReadChunk((IntPtr)(region.Base.ToInt64() + off), want);
                    Interlocked.Add(ref scanned, buf.Length);
                    if (buf.Length >= 4)
                    {
                        var ints = MemoryMarshal.Cast<byte, int>(buf.AsSpan());
                        for (int i = 0; i < ints.Length; i++)
                        {
                            if (ints[i] != cur) continue;
                            int lo = Math.Max(0, i - PairWindow / 4), hi = Math.Min(ints.Length - 1, i + PairWindow / 4);
                            int maxAt = -1;
                            for (int j = lo; j <= hi; j++) if (ints[j] == max) { maxAt = j; break; }
                            if (maxAt < 0) continue;
                            long curAddr = region.Base.ToInt64() + off + (long)i * 4;
                            long maxAddr = region.Base.ToInt64() + off + (long)maxAt * 4;
                            hits.Add($"cur@0x{curAddr:X}  max@0x{maxAddr:X}  delta={maxAddr - curAddr:+#;-#;0}");
                            if (hits.Count >= MaxHits) { Interlocked.Exchange(ref capped, 1); return; }
                        }
                    }
                    off += ChunkSize;
                }
            });
            sw.Stop();
            sb.AppendLine($"# hits={hits.Count} elapsedMs={sw.ElapsedMilliseconds}" + (capped != 0 ? " (capped)" : ""));
            sb.AppendLine();
            foreach (var h in hits) sb.AppendLine(h);
            System.IO.File.WriteAllText(outPath, sb.ToString());
        }

        private static bool HasModulePointers(Mem.Region region, ulong modBase, ulong modEnd, ref long scanned)
        {
            long step = Math.Max(DensitySampleBytes, region.Size / DensitySamples);
            for (long pos = 0; pos < region.Size; pos += step)
            {
                int want = (int)Math.Min(DensitySampleBytes, region.Size - pos);
                if (want < 8) break;
                var buf = Mem.ReadChunk((IntPtr)(region.Base.ToInt64() + pos), want);
                Interlocked.Add(ref scanned, buf.Length);
                if (buf.Length < 8) continue;
                var longs = MemoryMarshal.Cast<byte, long>(buf.AsSpan());
                for (int i = 0; i < longs.Length; i++)
                {
                    ulong v = (ulong)longs[i];
                    if (v >= modBase && v < modEnd) return true;
                }
            }
            return false;
        }
    }
}
