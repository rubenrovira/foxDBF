using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// §12.3 — COUNT is CHEAPER than FindRecords (the value). Deterministic, NOT timing.
///
/// TWO deterministic proofs:
///   (A) EXACT (fully-optimized) query — e.g. ID = x, or a numeric range window: the count-only path
///       reads NO full record buffers. Under SET DELETED ON it may read a 1-byte deletion flag per
///       candidate (the CDX can index deleted rows), which is acceptable; what it must NOT do is read
///       the whole record. <see cref="QueryOptimizer.FindRecords"/> on the SAME query reads/materializes
///       more (at least the surviving candidate records). A read-classifying stream proves it by
///       distinguishing FULL-record reads (≈ RecordLength bytes) from flag-only reads (1 byte).
///   (B) RESIDUAL query — an indexed candidate set plus a non-optimizable residual term: the count-only
///       path still iterates only the candidates evaluating the residual, but allocates NO recno list.
///       <see cref="GC.GetAllocatedBytesForCurrentThread"/> (an EXACT per-thread counter) proves Count
///       allocates strictly less than FindRecords by at least the recno List's backing storage.
///
/// SAFETY: every fixture is a TEMP file, deleted on dispose. No committed fixture is touched.
/// </summary>
public sealed class QueryCountCheaperTests : IClassFixture<QueryCountCheaperTests.BigTable>
{
    private readonly BigTable _fx;
    public QueryCountCheaperTests(BigTable fx) => _fx = fx;

    // ============================================================ read-classifying stream

    /// <summary>
    /// A pass-through <see cref="Stream"/> that classifies each PHYSICAL record access by SIZE. A record
    /// read seeks to its absolute data-region offset (<c>HeaderLength + index*RecordLength</c>,
    /// <see cref="SeekOrigin.Begin"/>) and then reads; the FIRST read after such a seek tells us whether
    /// it pulled a FULL record (≈ <see cref="RecordLength"/> bytes) or just the 1-byte deletion flag.
    /// </summary>
    private sealed class ClassifyingStream : Stream
    {
        private readonly Stream _inner;
        public long DataStart { get; set; } = long.MaxValue;
        public int RecordLength { get; set; } = int.MaxValue;

        /// <summary>Reads of (about) a whole record buffer since the last <see cref="Reset"/>.</summary>
        public int FullRecordReads { get; private set; }
        /// <summary>Small (deletion-flag-sized) reads at a record offset since the last <see cref="Reset"/>.</summary>
        public int FlagReads { get; private set; }

        private bool _pendingDataSeek;

        public ClassifyingStream(Stream inner) => _inner = inner;

        public void Reset() { FullRecordReads = 0; FlagReads = 0; _pendingDataSeek = false; }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long pos = _inner.Seek(offset, origin);
            if (origin == SeekOrigin.Begin && offset >= DataStart) _pendingDataSeek = true;
            return pos;
        }

        private void Classify(int requested)
        {
            if (!_pendingDataSeek) return;
            _pendingDataSeek = false;
            // A full-record read requests (about) RecordLength bytes; a deletion-flag probe requests 1.
            if (requested >= RecordLength) FullRecordReads++;
            else FlagReads++;
        }

        public override int Read(byte[] buffer, int offset, int count) { int n = _inner.Read(buffer, offset, count); Classify(count); return n; }
        public override int Read(Span<byte> buffer) { int n = _inner.Read(buffer); Classify(buffer.Length); return n; }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private static (DbfTable table, CdxFile cdx, ClassifyingStream counter) OpenCounted(string dbf, string cdxPath)
    {
        var fs = new FileStream(dbf, FileMode.Open, FileAccess.Read, FileShare.Read);
        var counter = new ClassifyingStream(fs);
        var table = DbfTable.Open(counter, leaveOpen: false);
        var cdx = CdxFile.Open(cdxPath, table); // the CDX is a SEPARATE stream — index walks never touch the dbf counter
        counter.DataStart = table.HeaderLength;
        counter.RecordLength = table.RecordLength;
        counter.Reset();
        return (table, cdx, counter);
    }

    // ============================================================ (A) EXACT → no full-record reads

    /// <summary>
    /// A single-candidate EXACT equality (ID = 1234, SET DELETED ON). Counting it must read ZERO full
    /// records (a 1-byte deletion-flag check is acceptable), while FindRecords on the same query reads
    /// at least the one surviving record. The count is still exactly 1.
    /// </summary>
    [Fact]
    public void ExactEquality_Count_ReadsNoFullRecords_FindRecordsReadsMore()
    {
        var (table, cdx, counter) = OpenCounted(_fx.Dbf, _fx.Cdx);
        try
        {
            counter.Reset();
            int n = QueryOptimizer.Count(table, cdx, "ID = 1234"); // default ctx = SET DELETED ON
            int countFullReads = counter.FullRecordReads;

            Assert.Equal(1, n); // correctness anchor
            Assert.Equal(0, countFullReads); // EXACT count reads no whole records (flag-only is allowed)

            counter.Reset();
            var find = QueryOptimizer.FindRecords(table, cdx, "ID = 1234");
            int findFullReads = counter.FullRecordReads;

            Assert.Single(find.RecordNumbers);
            Assert.True(findFullReads >= 1,
                $"FindRecords should materialize the survivor record; full reads={findFullReads}");
            Assert.True(countFullReads < findFullReads,
                $"Count ({countFullReads}) must read fewer full records than FindRecords ({findFullReads})");
        }
        finally { cdx.Dispose(); table.Dispose(); }
    }

    /// <summary>
    /// An EXACT numeric range WINDOW (ID in [1000, 1010] → 11 candidates, SET DELETED ON). Counting it
    /// reads ZERO full records; FindRecords materializes the 11 survivors.
    /// </summary>
    [Fact]
    public void ExactRangeWindow_Count_ReadsNoFullRecords_FindRecordsReadsTheSlice()
    {
        var (table, cdx, counter) = OpenCounted(_fx.Dbf, _fx.Cdx);
        try
        {
            const string filter = "ID >= 1000 AND ID <= 1010";

            counter.Reset();
            int n = QueryOptimizer.Count(table, cdx, filter);
            int countFullReads = counter.FullRecordReads;

            Assert.Equal(11, n);
            Assert.Equal(0, countFullReads);

            counter.Reset();
            var find = QueryOptimizer.FindRecords(table, cdx, filter);
            int findFullReads = counter.FullRecordReads;

            Assert.Equal(11, find.RecordNumbers.Count);
            Assert.True(countFullReads < findFullReads,
                $"Count ({countFullReads}) must read fewer full records than FindRecords ({findFullReads})");
        }
        finally { cdx.Dispose(); table.Dispose(); }
    }

    /// <summary>
    /// An EXACT single candidate that is DELETED (recno 4999, SET DELETED ON). The count is 0; the
    /// count path may probe the 1-byte deletion flag but must read NO full record.
    /// </summary>
    [Fact]
    public void ExactDeletedCandidate_Count_IsZero_AndReadsNoFullRecord()
    {
        var (table, cdx, counter) = OpenCounted(_fx.Dbf, _fx.Cdx);
        try
        {
            counter.Reset();
            int n = QueryOptimizer.Count(table, cdx, "ID = 4999"); // recno 4999 is deleted in the fixture

            Assert.Equal(0, n);
            Assert.Equal(0, counter.FullRecordReads);
        }
        finally { cdx.Dispose(); table.Dispose(); }
    }

    // ============================================================ (B) RESIDUAL → no recno list

    /// <summary>
    /// A RESIDUAL query (AMOUNT &gt;= 0 narrows to an all-row candidate set, CODE = 'Y' is the
    /// non-optimizable residual). Both paths iterate the SAME candidates and read the same records, so
    /// the ONLY allocation difference is FindRecords' recno List. Count must allocate strictly less, by
    /// at least that list's backing storage (4 bytes per matched recno). Allocation is measured with the
    /// EXACT per-thread counter after warm-up, so the comparison is deterministic.
    /// </summary>
    [Fact]
    public void ResidualQuery_Count_AvoidsRecnoListAllocation()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);

        const string filter = "AMOUNT >= 0 AND CODE = 'Y'"; // indexed candidate (all rows) + residual CODE

        // Anchor: the count is correct and there really are many matches (so the list dominates the gap).
        int expected = QueryOptimizer.FindRecords(table, cdx, filter).RecordNumbers.Count;
        Assert.Equal(expected, QueryOptimizer.Count(table, cdx, filter));
        Assert.True(expected >= 1000, $"need a large match set to make the list gap clear; got {expected}");

        // Warm up both paths (JIT, any first-call caches) so they are excluded from the measurement.
        _ = QueryOptimizer.Count(table, cdx, filter);
        _ = QueryOptimizer.FindRecords(table, cdx, filter);

        long countBytes = Measure(() => QueryOptimizer.Count(table, cdx, filter));
        long findBytes = Measure(() => QueryOptimizer.FindRecords(table, cdx, filter));

        Assert.True(countBytes < findBytes,
            $"Count allocated {countBytes} bytes; FindRecords allocated {findBytes} — Count must allocate less (no recno list)");
        Assert.True(findBytes - countBytes >= (long)expected * sizeof(int),
            $"the allocation gap ({findBytes - countBytes}) should cover the recno list ({(long)expected * sizeof(int)} bytes)");
    }

    private static long Measure(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        long after = GC.GetAllocatedBytesForCurrentThread();
        return after - before;
    }

    // ============================================================ fixture

    /// <summary>
    /// A 5000-row temp table: ID (Integer, tag), AMOUNT (Numeric, tag), CODE (Character, UNINDEXED).
    /// Recno 4999 is deleted (a deleted indexable candidate). Built once; deleted on dispose.
    /// </summary>
    public sealed class BigTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount => 5000;

        public BigTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_count_cheaper_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "big.dbf");
            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("AMOUNT", 'N', 10, 2),
                new DbfColumnDef("CODE", 'C', 4),
            };
            using (var w = DbfWriter.Create(Dbf, cols))
            {
                for (int i = 1; i <= RowCount; i++)
                    w.AppendRecord(new object?[] { i, i % 100, (i % 10 == 0) ? "X" : "Y" });
                w.Delete(4998); // recno 4999 — a deleted indexable candidate
                w.CreateTag(new CdxTagDefinition("IDTAG", "ID"));
                w.CreateTag(new CdxTagDefinition("AMTTAG", "AMOUNT"));
            }
            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
