using System;
using System.Collections.Generic;
using System.Linq;
using CrossVault.FoxDbf.Query;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Phase B-2 — the <see cref="RecordBitmap"/> as a Roaring-style ADAPTIVE structure
/// (sparse array container vs dense word container, chosen by density). These tests pin
/// the contract that MUST survive the rewrite:
///
/// (1) BEHAVIOURAL EQUIVALENCE — the adaptive bitmap is a byte-for-byte behavioural
///     drop-in for a reference model (<see cref="HashSet{Int32}"/>) across And/Or/Not/
///     AndNot/Set/Count/Enumerate, over many randomized-by-seed patterns: sparse, dense,
///     empty, full, after a NOT/flip, mixed sparse-vs-dense operands, around the density
///     threshold, and on large universes. Enumerate ascending; Count exact; NOT identical.
///
/// (2) SUB-LINEAR sparse ops — on a 5,000,000-record universe an AND/OR of two SMALL sets
///     must touch work proportional to the OPERAND sizes, not the universe. Asserted
///     deterministically via <see cref="RecordBitmap.DiagnosticWorkUnits"/> and via
///     per-thread allocation, both of which the old O(universe/64) dense path blows.
///
/// Pure in-memory algebra — no files. Reference uses a fixed-seed <see cref="Random"/>.
/// </summary>
public sealed class RecordBitmapAdaptiveTests
{
    // ---- reference helpers -------------------------------------------------

    /// <summary>Build a bitmap from a set of indices via the public <c>Set</c> API.</summary>
    private static RecordBitmap From(int recordCount, IEnumerable<int> indices)
    {
        var b = new RecordBitmap(recordCount);
        foreach (int i in indices) b.Set(i);
        return b;
    }

    /// <summary>The reference complement of <paramref name="set"/> within [0, rc).</summary>
    private static SortedSet<int> RefNot(int rc, ISet<int> set)
    {
        var r = new SortedSet<int>();
        for (int i = 0; i < rc; i++) if (!set.Contains(i)) r.Add(i);
        return r;
    }

    /// <summary>Assert a bitmap equals a reference set: Count exact + Enumerate ascending.</summary>
    private static void AssertEquivalent(RecordBitmap bm, IEnumerable<int> reference)
    {
        int[] expected = reference.OrderBy(x => x).ToArray();
        int[] actual = bm.Enumerate().ToArray();

        Assert.Equal(expected.Length, bm.Count);          // Count exact
        Assert.Equal(expected, actual);                   // ascending + identical membership

        // Enumerate must be strictly ascending.
        for (int k = 1; k < actual.Length; k++)
            Assert.True(actual[k] > actual[k - 1], "Enumerate must be strictly ascending");
    }

    /// <summary>
    /// A catalogue of index-pattern KINDS, varied by a seeded <see cref="Random"/>, so we
    /// exercise sparse / dense / empty / full / threshold-boundary / clustered shapes.
    /// </summary>
    private static HashSet<int> Pattern(int rc, int kind, Random rng)
    {
        var s = new HashSet<int>();
        switch (kind)
        {
            case 0: // empty
                break;
            case 1: // full
                for (int i = 0; i < rc; i++) s.Add(i);
                break;
            case 2: // very sparse (a handful)
                for (int n = 0; n < Math.Min(8, rc); n++) s.Add(rng.Next(rc));
                break;
            case 3: // sparse (~1%)
                for (int n = 0; n < Math.Max(1, rc / 100); n++) s.Add(rng.Next(rc));
                break;
            case 4: // ~half, random
                for (int i = 0; i < rc; i++) if (rng.Next(2) == 0) s.Add(i);
                break;
            case 5: // dense (~99%) — i.e. sparse complement
                for (int i = 0; i < rc; i++) s.Add(i);
                for (int n = 0; n < Math.Max(1, rc / 100); n++) s.Remove(rng.Next(rc));
                break;
            case 6: // a clustered run (contiguous), word-boundary-crossing
                {
                    int start = rng.Next(rc);
                    int len = rng.Next(rc + 1);
                    for (int i = start; i < Math.Min(rc, start + len); i++) s.Add(i);
                    break;
                }
            case 7: // around the conversion threshold: a count near common Roaring cutoffs
                {
                    int target = Math.Min(rc, 4096 + rng.Next(-32, 33)); // ~ array/dense boundary
                    while (s.Count < target) s.Add(rng.Next(rc));
                    break;
                }
            default: // scattered fixed stride + jitter
                for (int i = rng.Next(7); i < rc; i += 1 + rng.Next(7)) s.Add(i);
                break;
        }
        // Bound to [0, rc): a 0-size universe holds nothing (rng.Next(0) yields 0, which
        // is out of universe). The bitmap never stores out-of-universe bits, so neither
        // must the reference model it is checked against.
        s.RemoveWhere(x => (uint)x >= (uint)rc);
        return s;
    }

    // ---- (1) BEHAVIOURAL EQUIVALENCE ---------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(99)]
    [InlineData(12345)]
    public void Adaptive_Matches_Reference_For_All_Ops(int seed)
    {
        var rng = new Random(seed);
        // A spread of universe sizes incl. exact-word multiples and the +1/-1 tails.
        int[] universes = { 0, 1, 63, 64, 65, 127, 128, 200, 1000, 4096, 70_000 };

        foreach (int rc in universes)
        {
            for (int ka = 0; ka <= 8; ka++)
            {
                for (int kb = 0; kb <= 8; kb++)
                {
                    var sa = Pattern(rc, ka, rng);
                    var sb = Pattern(rc, kb, rng);
                    var a = From(rc, sa);
                    var b = From(rc, sb);

                    // Set / Count / Enumerate self-consistency.
                    AssertEquivalent(a, sa);
                    AssertEquivalent(b, sb);

                    // AND
                    AssertEquivalent(a.And(b), sa.Intersect(sb));
                    // OR
                    AssertEquivalent(a.Or(b), sa.Union(sb));
                    // AND NOT (set difference) — the genuinely new op.
                    AssertEquivalent(a.AndNot(b), sa.Except(sb));
                    AssertEquivalent(b.AndNot(a), sb.Except(sa));

                    // NOT (complement) on each operand, and that originals are untouched.
                    AssertEquivalent(a.Not(), RefNot(rc, sa));
                    AssertEquivalent(b.Not(), RefNot(rc, sb));
                    AssertEquivalent(a, sa);
                    AssertEquivalent(b, sb);
                }
            }
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(2024)]
    public void Adaptive_Matches_Reference_After_Flip_MixedOperands(int seed)
    {
        var rng = new Random(seed);
        int[] universes = { 64, 200, 1000, 4096, 70_000 };

        foreach (int rc in universes)
        {
            // One sparse operand, one dense operand -> mixed container types in the rewrite.
            var sparse = Pattern(rc, 2, rng);
            var dense = Pattern(rc, 5, rng);
            var a = From(rc, sparse);
            var b = From(rc, dense);

            var notA = a.Not();                 // flipped operand
            var refNotA = RefNot(rc, sparse);

            // flipped AND/OR/ANDNOT against a normal operand and vice versa.
            AssertEquivalent(notA.And(b), refNotA.Intersect(dense));
            AssertEquivalent(notA.Or(b), refNotA.Union(dense));
            AssertEquivalent(notA.AndNot(b), refNotA.Except(dense));
            AssertEquivalent(b.AndNot(notA), dense.Except(refNotA));

            // double NOT round-trips exactly.
            AssertEquivalent(notA.Not(), sparse);

            // flip of a flip combined with another flip.
            var notB = b.Not();
            var refNotB = RefNot(rc, dense);
            AssertEquivalent(notA.And(notB), refNotA.Intersect(refNotB));
            AssertEquivalent(notA.Or(notB), refNotA.Union(refNotB));
        }
    }

    [Fact]
    public void Adaptive_LargeUniverse_Sparse_IsCorrect()
    {
        const int rc = 1_000_000;
        var rng = new Random(777);
        var sa = new HashSet<int>();
        var sb = new HashSet<int>();
        for (int n = 0; n < 500; n++) { sa.Add(rng.Next(rc)); sb.Add(rng.Next(rc)); }

        var a = From(rc, sa);
        var b = From(rc, sb);

        AssertEquivalent(a.And(b), sa.Intersect(sb));
        AssertEquivalent(a.Or(b), sa.Union(sb));
        AssertEquivalent(a.AndNot(b), sa.Except(sb));

        // Complement of a sparse set is huge: assert by Count + sampled membership,
        // not by materialising ~1M ints.
        var na = a.Not();
        Assert.Equal(rc - sa.Count, na.Count);
        foreach (int i in sa) Assert.False(na.Get(i));
        for (int probe = 0; probe < 1000; probe++)
        {
            int i = rng.Next(rc);
            Assert.Equal(!sa.Contains(i), na.Get(i));
        }
    }

    // ---- (1b) DIFFERENT-recordCount operands (out-of-universe regression) --

    /// <summary>
    /// Boolean combinations of two operands whose <c>RecordCount</c> universes DIFFER,
    /// exercising the SPARSE FAST-PATH (the buggy path). A sparse-unflipped driver over a
    /// LARGE universe And/AndNot a flipped (Not) probe over a SMALL universe: the fast-path
    /// probes <c>test.Get(e)</c> for driver elements e that lie OUTSIDE the probe's universe
    /// (e &gt;= probe.RecordCount). A complement MUST report nothing set outside its own
    /// [0, rc); without the universe bounds check in <see cref="RecordBitmap.Get"/> the
    /// flipped probe returns true for every out-of-universe index, so the fast-path keeps
    /// those driver bits and diverges from the dense path (e.g. {5,15} rc=20 AND Not({3})
    /// rc=10 yields {5,15} instead of the correct {5}).
    ///
    /// The driver universe is large and the driver set tiny, so the driver stays SPARSE
    /// (well under <c>DenseThreshold</c>) and <see cref="RecordBitmap.And"/> takes the
    /// sparse fast-path rather than the always-correct dense fallback — asserted via
    /// <see cref="RecordBitmap.DiagnosticWorkUnits"/>. This test FAILS before the Get()
    /// bounds fix and PASSES after.
    /// </summary>
    [Theory]
    [InlineData(10, 5_000)]
    [InlineData(64, 100_000)]
    [InlineData(65, 70_000)]
    [InlineData(100, 50_000)]
    public void DifferentRecordCounts_SparseFastPath_FlippedProbe_MatchesBoundedReference(int rcSmall, int rcLarge)
    {
        // Driver: a handful of explicit elements STRADDLING the probe's universe edge so
        // some probes land out-of-universe (>= rcSmall). Tiny count over a huge universe
        // keeps it sparse-unflipped (DenseThreshold = 2 * (rcLarge/64) >> our 8 elements).
        var driverSet = new HashSet<int>
        {
            1, 3, rcSmall - 1,            // inside the probe universe
            rcSmall, rcSmall + 1,         // first indices OUTSIDE the probe universe
            rcSmall * 2, rcLarge / 2, rcLarge - 1, // deep outside
        };
        var driver = From(rcLarge, driverSet);

        // Probe: a complement (Not) over the SMALL universe; its logical set is most of
        // [0, rcSmall) and NOTHING at/after rcSmall.
        var baseSet = new HashSet<int> { 2, 5 };
        var probe = From(rcSmall, baseSet).Not();
        var probeSet = RefNot(rcSmall, baseSet);          // bounded reference complement

        // Reference over the bounded universes (HashSet/LINQ).
        var refAnd = driverSet.Intersect(probeSet);
        var refAndNot = driverSet.Except(probeSet);

        // AND via the sparse fast-path, then prove the fast-path (not DenseCombine) ran:
        // fast-path work == driver element count; DenseCombine would be ~rcLarge/64 words.
        RecordBitmap.DiagnosticWorkUnits = 0;
        var gotAnd = driver.And(probe);
        Assert.True(
            RecordBitmap.DiagnosticWorkUnits <= driverSet.Count,
            $"expected the sparse fast-path (<= {driverSet.Count} work units) but saw " +
            $"{RecordBitmap.DiagnosticWorkUnits} — the dense fallback ran, so the bug is not exercised.");
        AssertEquivalent(gotAnd, refAnd);

        RecordBitmap.DiagnosticWorkUnits = 0;
        var gotAndNot = driver.AndNot(probe);
        Assert.True(RecordBitmap.DiagnosticWorkUnits <= driverSet.Count, "AndNot must take the sparse fast-path");
        AssertEquivalent(gotAndNot, refAndNot);

        // Reverse direction and OR stay correct over the differing universes too.
        AssertEquivalent(probe.And(driver), probeSet.Intersect(driverSet));
        AssertEquivalent(driver.Or(probe), driverSet.Union(probeSet));
    }

    // ---- (2) SUB-LINEAR sparse ops (deterministic) -------------------------

    private const int Huge = 5_000_000;                 // universe; dense path = ~78125 words
    private static readonly int[] SetA = { 10, 200, 5_000, 64_001, 1_000_000 };
    private static readonly int[] SetB = { 200, 5_000, 5_001, 1_000_000, 4_999_999 };

    [Fact]
    public void And_OfTwoSmallSets_OnHugeUniverse_IsSubLinear()
    {
        var a = From(Huge, SetA);
        var b = From(Huge, SetB);

        RecordBitmap.DiagnosticWorkUnits = 0;
        var r = a.And(b);

        // Still correct.
        AssertEquivalent(r, SetA.Intersect(SetB));

        // Work must be proportional to the ~5 operand bits, NOT the universe.
        // Dense path touches Huge/64 == 78125 words; assert far below that.
        Assert.True(
            RecordBitmap.DiagnosticWorkUnits < 2_000,
            $"AND touched {RecordBitmap.DiagnosticWorkUnits} work units on a {Huge}-record universe; " +
            "expected work proportional to the operand sizes (a handful of bits), not the universe.");
    }

    [Fact]
    public void Or_OfTwoSmallSets_OnHugeUniverse_IsSubLinear()
    {
        var a = From(Huge, SetA);
        var b = From(Huge, SetB);

        RecordBitmap.DiagnosticWorkUnits = 0;
        var r = a.Or(b);

        AssertEquivalent(r, SetA.Union(SetB));

        Assert.True(
            RecordBitmap.DiagnosticWorkUnits < 2_000,
            $"OR touched {RecordBitmap.DiagnosticWorkUnits} work units on a {Huge}-record universe; " +
            "expected work proportional to the operand sizes, not the universe.");
    }

    [Fact]
    public void And_OfTwoSmallSets_OnHugeUniverse_DoesNotAllocateUniverseSizedArray()
    {
        var a = From(Huge, SetA);
        var b = From(Huge, SetB);

        // Isolate the allocation of the AND alone (operands already built/measured out).
        long before = GC.GetAllocatedBytesForCurrentThread();
        var r = a.And(b);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Keep r reachable so the JIT cannot elide its allocation.
        Assert.Equal(SetA.Intersect(SetB).OrderBy(x => x).ToArray(), r.Enumerate().ToArray());

        // The dense path allocates a ulong[Huge/64] ≈ 625 KB result word array per AND.
        // An adaptive AND of a handful of bits must allocate a tiny fraction of that.
        Assert.True(
            allocated < 200_000,
            $"AND allocated {allocated} bytes on a {Huge}-record universe; the dense path's " +
            "~625 KB universe-sized array must not be allocated for a few-bit operand.");
    }
}
