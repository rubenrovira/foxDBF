using System.Numerics;

namespace CrossVault.FoxDbf.Query;

/// <summary>
/// A record-number BITMAP over a table's physical records (plan §D8, faithful to
/// CodeBase <c>m4map.c</c>'s F4FLAG record bitmaps). Bit <c>i</c> (0-based) stands
/// for physical record index <c>i</c> (1-based recno <c>i + 1</c>).
/// </summary>
/// <remarks>
/// <para>
/// Roaring-style ADAPTIVE storage: each instance is backed by EITHER a SPARSE
/// container (a sorted array of set indices, ideal when only a handful of bits are
/// set) OR a DENSE container (a <c>ulong[]</c> word array, ideal when many bits are
/// set), chosen by a density threshold and converted on demand. A cheap
/// <see cref="Not"/>/flip flag lets a complement be taken in O(1) without rewriting
/// storage; read paths honour it lazily, mutators normalise it away first.
/// </para>
/// <para>
/// Boolean combinations (<see cref="And"/>, <see cref="Or"/>, <see cref="AndNot"/>)
/// are container-aware: when an operand is sparse the cost is proportional to the
/// OPERAND sizes (a merge / membership probe), not to the record universe. The dense
/// path remains the correct fallback for mixed/flipped/dense operands.
/// </para>
/// </remarks>
public sealed class RecordBitmap
{
    // Exactly one container is active:
    //   _words != null  -> DENSE  (word array; _sparse unused)
    //   _words == null  -> SPARSE (sorted unique indices in _sparse[0.._sparseCount))
    private ulong[]? _words;
    private int[]? _sparse;
    private int _sparseCount;

    private readonly int _recordCount;

    // Cheap NOT: when true the LOGICAL set is the COMPLEMENT of the stored set.
    private bool _flipped;

    /// <summary>
    /// DIAGNOSTICS ONLY (test instrumentation): the number of word-/element-level
    /// "work units" the most recent boolean combination (<see cref="And"/>,
    /// <see cref="Or"/>, <see cref="AndNot"/>) performed. For the adaptive structure
    /// this is proportional to the OPERAND sizes (sparse merge/probe), not to the
    /// record universe (<see cref="RecordCount"/>); only the dense fallback scales with
    /// the universe. Not thread-safe; single-threaded test use only.
    /// </summary>
    public static long DiagnosticWorkUnits;

    /// <summary>Create an all-clear bitmap covering <paramref name="recordCount"/> records.</summary>
    public RecordBitmap(int recordCount)
    {
        if (recordCount < 0) recordCount = 0;
        _recordCount = recordCount;
        // Starts SPARSE-empty: no universe-sized allocation up front.
    }

    /// <summary>The number of records (bits) this bitmap covers.</summary>
    public int RecordCount => _recordCount;

    // ---- internals ---------------------------------------------------------

    private int WordCount => (_recordCount + 63) >> 6;

    /// <summary>
    /// Sparse→dense crossover: keep sparse while a sorted-index array is cheaper than a
    /// full word array (≈ when the set is below ~1/32 of the universe).
    /// </summary>
    private int DenseThreshold => WordCount * 2;

    private bool IsSparseUnflipped => _words is null && !_flipped;

    /// <summary>Mask of the valid bits in the final word (guards the unused high tail).</summary>
    private ulong LastMask
    {
        get
        {
            int rem = _recordCount & 63;
            return rem == 0 ? ulong.MaxValue : (1ul << rem) - 1;
        }
    }

    /// <summary>The logical word at <paramref name="i"/> (flip applied, tail masked). DENSE only.</summary>
    private ulong LogicalWord(int i)
    {
        ulong w = _flipped ? ~_words![i] : _words![i];
        if (i == _words!.Length - 1) w &= LastMask;
        return w;
    }

    private static int BinarySearch(int[]? a, int len, int value)
    {
        int lo = 0, hi = len - 1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            int v = a![mid];
            if (v == value) return mid;
            if (v < value) lo = mid + 1; else hi = mid - 1;
        }
        return ~lo;
    }

    private void EnsureSparseCapacity(int n)
    {
        if (_sparse is null) { _sparse = new int[Math.Max(4, n)]; return; }
        if (_sparse.Length < n)
        {
            int cap = _sparse.Length * 2;
            if (cap < n) cap = n;
            Array.Resize(ref _sparse, cap);
        }
    }

    /// <summary>Convert the (unflipped) sparse container to a dense word array.</summary>
    private void EnsureDense()
    {
        if (_words is not null) return;
        int wc = WordCount;
        var w = new ulong[wc];
        for (int k = 0; k < _sparseCount; k++)
        {
            int e = _sparse![k];
            int wi = e >> 6;
            if ((uint)wi < (uint)wc) w[wi] |= 1ul << (e & 63); // out-of-universe bits have no home
        }
        _words = w;
        _sparse = null;
        _sparseCount = 0;
    }

    /// <summary>Materialise the flip into stored bits so mutators can ignore it.</summary>
    private void Normalize()
    {
        if (!_flipped) return;
        if (_words is not null)
        {
            for (int i = 0; i < _words.Length; i++) _words[i] = ~_words[i];
            _flipped = false;
            return;
        }
        // Flipped sparse: the logical set is the complement -> materialise to dense.
        int wc = WordCount;
        var w = new ulong[wc];
        for (int i = 0; i < wc; i++) w[i] = ulong.MaxValue;
        if (wc > 0) w[wc - 1] &= LastMask;
        for (int k = 0; k < _sparseCount; k++)
        {
            int e = _sparse![k];
            int wi = e >> 6;
            if ((uint)wi < (uint)wc) w[wi] &= ~(1ul << (e & 63));
        }
        _words = w;
        _sparse = null;
        _sparseCount = 0;
        _flipped = false;
    }

    /// <summary>Build a dense word array of the LOGICAL set (flip applied, tail masked).</summary>
    private ulong[] ToLogicalWords()
    {
        int wc = WordCount;
        var w = new ulong[wc];
        if (_words is not null)
        {
            for (int i = 0; i < wc; i++) w[i] = _flipped ? ~_words[i] : _words[i];
        }
        else if (!_flipped)
        {
            for (int k = 0; k < _sparseCount; k++)
            {
                int e = _sparse![k];
                int wi = e >> 6;
                if ((uint)wi < (uint)wc) w[wi] |= 1ul << (e & 63);
            }
        }
        else
        {
            for (int i = 0; i < wc; i++) w[i] = ulong.MaxValue;
            for (int k = 0; k < _sparseCount; k++)
            {
                int e = _sparse![k];
                int wi = e >> 6;
                if ((uint)wi < (uint)wc) w[wi] &= ~(1ul << (e & 63));
            }
        }
        if (wc > 0) w[wc - 1] &= LastMask;
        return w;
    }

    // ---- population --------------------------------------------------------

    /// <summary>The number of set bits (population count).</summary>
    public int Count
    {
        get
        {
            if (_words is not null)
            {
                int c = 0;
                for (int i = 0; i < _words.Length; i++)
                    c += BitOperations.PopCount(LogicalWord(i));
                return c;
            }
            if (!_flipped) return _sparseCount;
            // Complement is bounded to [0, RecordCount): count only the in-universe members.
            return _recordCount - InUniverseSparseCount();
        }
    }

    /// <summary>How many sorted sparse indices fall within [0, RecordCount).</summary>
    private int InUniverseSparseCount()
    {
        if (_sparseCount == 0) return 0;
        if (_sparse![_sparseCount - 1] < _recordCount) return _sparseCount; // common fast path
        int idx = BinarySearch(_sparse, _sparseCount, _recordCount);
        return idx >= 0 ? idx : ~idx; // first position whose value >= RecordCount
    }

    // ---- mutators ----------------------------------------------------------

    /// <summary>Set the single bit at <paramref name="index"/> (0-based).</summary>
    public void Set(int index)
    {
        // Bound to the universe: out-of-range indices are never stored, so Set/Get/
        // Enumerate/Count stay mutually consistent and agree with the dense path.
        if ((uint)index >= (uint)_recordCount) return;
        if (_flipped) Normalize();
        if (_words is not null)
        {
            int wi = index >> 6;
            if ((uint)wi < (uint)_words.Length) _words[wi] |= 1ul << (index & 63);
            return;
        }
        int found = BinarySearch(_sparse, _sparseCount, index);
        if (found >= 0) return; // idempotent
        int pos = ~found;
        EnsureSparseCapacity(_sparseCount + 1);
        Array.Copy(_sparse!, pos, _sparse!, pos + 1, _sparseCount - pos);
        _sparse![pos] = index;
        _sparseCount++;
        if (DenseThreshold > 0 && _sparseCount >= DenseThreshold) EnsureDense();
    }

    /// <summary>Whether the bit at <paramref name="index"/> (0-based) is set.</summary>
    public bool Get(int index)
    {
        // A complement is bounded to its own universe: nothing outside [0, RecordCount)
        // is ever set. This guard makes the flipped-sparse path and the flipped-dense
        // final-word tail agree, and keeps the And()/AndNot() sparse fast-paths identical
        // to DenseCombine for operands of differing RecordCount. (Also covers index < 0.)
        if ((uint)index >= (uint)_recordCount) return false;
        if (_words is not null)
        {
            int wi = index >> 6;
            if ((uint)wi >= (uint)_words.Length) return false; // beyond the dense universe
            ulong w = _flipped ? ~_words[wi] : _words[wi];
            return (w & (1ul << (index & 63))) != 0;
        }
        bool present = BinarySearch(_sparse, _sparseCount, index) >= 0;
        return _flipped ? !present : present;
    }

    /// <summary>Set every bit (the universe — all records).</summary>
    public void SetAll()
    {
        // Full set == complement of the empty set: O(1), no allocation.
        _words = null;
        _sparse = null;
        _sparseCount = 0;
        _flipped = true;
    }

    /// <summary>Clear every bit.</summary>
    public void Clear()
    {
        _words = null;
        _sparse = null;
        _sparseCount = 0;
        _flipped = false;
    }

    /// <summary>
    /// Set every bit in the inclusive index range
    /// <paramref name="startInclusive"/>..<paramref name="endInclusive"/> (0-based),
    /// clamped to the bitmap bounds; a reversed/empty range sets nothing.
    /// </summary>
    public void SetRange(int startInclusive, int endInclusive)
    {
        int s = Math.Max(0, startInclusive);
        int e = Math.Min(_recordCount - 1, endInclusive);
        if (s > e) return;
        Normalize();
        EnsureDense();

        int sw = s >> 6, ew = e >> 6;
        if (sw == ew)
        {
            _words![sw] |= MaskRange(s & 63, e & 63);
            return;
        }
        _words![sw] |= MaskRange(s & 63, 63);
        for (int i = sw + 1; i < ew; i++) _words[i] = ulong.MaxValue;
        _words[ew] |= MaskRange(0, e & 63);
    }

    /// <summary>Clear the single bit at <paramref name="index"/> (0-based).</summary>
    public void ClearBit(int index)
    {
        if (index < 0) return;
        Normalize();
        if (_words is not null)
        {
            int wi = index >> 6;
            if ((uint)wi < (uint)_words.Length) _words[wi] &= ~(1ul << (index & 63));
            return;
        }
        int found = BinarySearch(_sparse, _sparseCount, index);
        if (found < 0) return;
        Array.Copy(_sparse!, found + 1, _sparse!, found, _sparseCount - found - 1);
        _sparseCount--;
    }

    /// <summary>Bits <paramref name="lo"/>..<paramref name="hi"/> within one 64-bit word.</summary>
    private static ulong MaskRange(int lo, int hi)
    {
        ulong all = hi == 63 ? ulong.MaxValue : (1ul << (hi + 1)) - 1;
        ulong low = (1ul << lo) - 1;
        return all & ~low;
    }

    // ---- boolean combinations ---------------------------------------------

    /// <summary>Returns a new bitmap = this INTERSECT <paramref name="other"/>.</summary>
    public RecordBitmap And(RecordBitmap other)
    {
        DiagnosticWorkUnits = 0;
        int rc = Math.Max(_recordCount, other._recordCount);

        if (IsSparseUnflipped || other.IsSparseUnflipped)
        {
            // Drive by a sparse-unflipped operand (the smaller one if both qualify),
            // probing membership in the other: cost ∝ that operand's size.
            RecordBitmap drv, test;
            if (IsSparseUnflipped && other.IsSparseUnflipped)
            {
                if (_sparseCount <= other._sparseCount) { drv = this; test = other; }
                else { drv = other; test = this; }
            }
            else if (IsSparseUnflipped) { drv = this; test = other; }
            else { drv = other; test = this; }

            var buf = new int[drv._sparseCount];
            int n = 0;
            for (int k = 0; k < drv._sparseCount; k++)
            {
                DiagnosticWorkUnits++;
                int e = drv._sparse![k];
                if (test.Get(e)) buf[n++] = e; // ascending: drv._sparse is sorted
            }
            return BuildSparseAscending(rc, buf, n);
        }

        return DenseCombine(other, Op.And, rc);
    }

    /// <summary>Returns a new bitmap = this UNION <paramref name="other"/>.</summary>
    public RecordBitmap Or(RecordBitmap other)
    {
        DiagnosticWorkUnits = 0;
        int rc = Math.Max(_recordCount, other._recordCount);

        if (IsSparseUnflipped && other.IsSparseUnflipped)
        {
            int[]? a = _sparse; int an = _sparseCount;
            int[]? b = other._sparse; int bn = other._sparseCount;
            var buf = new int[an + bn];
            int n = 0, i = 0, j = 0;
            while (i < an && j < bn)
            {
                DiagnosticWorkUnits++;
                int x = a![i], y = b![j];
                if (x < y) { buf[n++] = x; i++; }
                else if (x > y) { buf[n++] = y; j++; }
                else { buf[n++] = x; i++; j++; }
            }
            while (i < an) { DiagnosticWorkUnits++; buf[n++] = a![i++]; }
            while (j < bn) { DiagnosticWorkUnits++; buf[n++] = b![j++]; }
            return BuildSparseAscending(rc, buf, n);
        }

        return DenseCombine(other, Op.Or, rc);
    }

    /// <summary>
    /// Returns a new bitmap = this AND NOT <paramref name="other"/> (set difference:
    /// the bits set here that are NOT set in <paramref name="other"/>).
    /// </summary>
    public RecordBitmap AndNot(RecordBitmap other)
    {
        DiagnosticWorkUnits = 0;
        int rc = Math.Max(_recordCount, other._recordCount);

        if (IsSparseUnflipped)
        {
            // Result ⊆ this: keep our elements that are absent from other. Cost ∝ |this|.
            var buf = new int[_sparseCount];
            int n = 0;
            for (int k = 0; k < _sparseCount; k++)
            {
                DiagnosticWorkUnits++;
                int e = _sparse![k];
                if (!other.Get(e)) buf[n++] = e;
            }
            return BuildSparseAscending(rc, buf, n);
        }

        return DenseCombine(other, Op.AndNot, rc);
    }

    private enum Op { And, Or, AndNot }

    private RecordBitmap DenseCombine(RecordBitmap other, Op op, int rc)
    {
        ulong[] aw = ToLogicalWords();
        ulong[] bw = other.ToLogicalWords();
        int wc = (rc + 63) >> 6;
        var rw = new ulong[wc];
        for (int i = 0; i < wc; i++)
        {
            DiagnosticWorkUnits++;
            ulong a = i < aw.Length ? aw[i] : 0ul;
            ulong b = i < bw.Length ? bw[i] : 0ul;
            rw[i] = op switch
            {
                Op.And => a & b,
                Op.Or => a | b,
                _ => a & ~b, // AndNot
            };
        }
        var r = new RecordBitmap(rc);
        if (wc > 0) rw[wc - 1] &= r.LastMask;
        r._words = rw;
        return r;
    }

    /// <summary>Wrap an ascending, unique <c>buf[0..n)</c> as a new bitmap, dense if dense pays off.</summary>
    private static RecordBitmap BuildSparseAscending(int rc, int[] buf, int n)
    {
        var r = new RecordBitmap(rc);
        if (r.DenseThreshold > 0 && n >= r.DenseThreshold)
        {
            var w = new ulong[r.WordCount];
            for (int k = 0; k < n; k++)
            {
                int e = buf[k];
                w[e >> 6] |= 1ul << (e & 63);
            }
            r._words = w;
        }
        else
        {
            r._sparse = buf;
            r._sparseCount = n;
        }
        return r;
    }

    /// <summary>Returns a new bitmap = the COMPLEMENT of this within the record universe.</summary>
    public RecordBitmap Not()
    {
        var r = new RecordBitmap(_recordCount);
        if (_words is not null)
        {
            r._words = (ulong[])_words.Clone();
        }
        else if (_sparseCount > 0)
        {
            r._sparse = new int[_sparseCount];
            Array.Copy(_sparse!, r._sparse, _sparseCount);
            r._sparseCount = _sparseCount;
        }
        r._flipped = !_flipped;
        return r;
    }

    /// <summary>Enumerate the set bit indices (0-based) in ascending order.</summary>
    public IEnumerable<int> Enumerate()
    {
        if (_words is not null)
        {
            for (int i = 0; i < _words.Length; i++)
            {
                ulong w = LogicalWord(i);
                while (w != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(w);
                    yield return (i << 6) + bit;
                    w &= w - 1;
                }
            }
        }
        else if (!_flipped)
        {
            for (int k = 0; k < _sparseCount; k++)
                yield return _sparse![k];
        }
        else
        {
            // Flipped sparse: enumerate the complement in ascending order.
            int k = 0;
            for (int i = 0; i < _recordCount; i++)
            {
                if (k < _sparseCount && _sparse![k] == i) { k++; continue; }
                yield return i;
            }
        }
    }
}
