using System;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Follow-up regression tests for the code-review findings whose CODE fix had shipped without a
/// dedicated guard: #1 (ALTER crash mid-commit), #6 (CREATE overwrite of a memo table mid-commit),
/// #7 (WritableTarget.Dispose flush-failure surfacing), #9a (OpenWritableTarget stale custom-aliased
/// handle), #9b (CdxFile ctor leak-on-throw). EVERY test runs on a FRESH TEMP DIR / brand-new table —
/// never the committed fixtures. The crash-safety tests use the internal
/// <see cref="DbfWriter.FaultBeforeDbfCommit"/> seam (thread-static, so it cannot trip a parallel test).
/// </summary>
public sealed class ReviewFollowupRegressionTests
{
    private static string FreshTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_review2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  Finding 1 — a fault at the FINAL .dbf commit of an ALTER must roll the ORIGINAL .dbf/.fpt
    //  pair back into place (the documented "original is NEVER destroyed" guarantee). Pre-fix the
    //  backup-staging moves sat OUTSIDE the protected try, so a mid-commit fault plus the finally
    //  deleting the backup could destroy the original.
    // ════════════════════════════════════════════════════════════════════════════════════════
    [Fact]
    public void Finding1_Alter_FaultAtCommit_LeavesRecoverableOriginalPair()
    {
        string dir = FreshTempDir();
        try
        {
            string dbf = Path.Combine(dir, "t.dbf");
            string fpt = Path.ChangeExtension(dbf, ".fpt");
            const string memo = "MEMO BODY THAT MUST SURVIVE A FAILED ALTER 0123456789";
            using (var w = DbfWriter.Create(dbf, new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("NOTE", 'M', 4),
            }))
            {
                w.AppendRecord(1, memo);
                w.AppendRecord(2, null);
            }

            DbfWriter.FaultBeforeDbfCommit = () => throw new IOException("injected ALTER commit fault");
            try
            {
                using var w = DbfWriter.Open(dbf, new DbfOptions { LockMode = LockMode.Exclusive });
                Assert.ThrowsAny<Exception>(() => w.AddColumn(new DbfColumnDef("EXTRA", 'C', 5)));
            }
            finally { DbfWriter.FaultBeforeDbfCommit = null; }

            // The original pair is intact and consistent: it opens, keeps its rows + memo, and the
            // half-applied ALTER (the EXTRA column) is NOT present.
            Assert.True(File.Exists(dbf));
            Assert.True(File.Exists(fpt));
            using var t = DbfTable.Open(dbf);
            Assert.Equal(2, t.RecordCount);
            Assert.Equal(memo, t.GetRecord(0)!.Value.GetString("NOTE"));
            Assert.DoesNotContain(t.Columns, c => c.Name.Equals("EXTRA", StringComparison.OrdinalIgnoreCase));
        }
        finally { Cleanup(dir); }
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  Finding 6 — overwriting an EXISTING memo-bearing table must stage BOTH original files so a
    //  fault at the final .dbf commit restores a consistent pair. Pre-fix only the .dbf was staged;
    //  the new .fpt was committed over the old one with no backup, so a fault left old .dbf + new
    //  .fpt = an orphaned-memo pair.
    // ════════════════════════════════════════════════════════════════════════════════════════
    [Fact]
    public void Finding6_CreateOverwrite_MemoTable_FaultAtCommit_LeavesRecoverablePair()
    {
        string dir = FreshTempDir();
        try
        {
            string dbf = Path.Combine(dir, "memo.dbf");
            string fpt = Path.ChangeExtension(dbf, ".fpt");
            const string original = "ORIGINAL MEMO CONTENT 0123456789";
            using (var w = DbfWriter.Create(dbf, new[]
            {
                new DbfColumnDef("A", 'C', 4),
                new DbfColumnDef("NOTE", 'M', 4),
            }))
                w.AppendRecord("AAAA", original);

            byte[] dbfBefore = File.ReadAllBytes(dbf);
            byte[] fptBefore = File.ReadAllBytes(fpt);

            // Overwrite with a DIFFERENT memo schema, faulting at the final .dbf commit (after the new
            // .fpt was committed and both originals staged) — the rollback must restore the ORIGINAL pair.
            DbfWriter.FaultBeforeDbfCommit = () => throw new IOException("injected create-overwrite fault");
            try
            {
                Assert.ThrowsAny<Exception>(() =>
                {
                    using var w = DbfWriter.Create(dbf, new[]
                    {
                        new DbfColumnDef("X", 'I', 4),
                        new DbfColumnDef("MEMO2", 'M', 4),
                    }, new DbfCreateOptions { Overwrite = true });
                });
            }
            finally { DbfWriter.FaultBeforeDbfCommit = null; }

            // The original .dbf and .fpt are byte-identical to before — a recoverable, consistent pair.
            Assert.True(File.Exists(dbf));
            Assert.True(File.Exists(fpt));
            Assert.Equal(dbfBefore, File.ReadAllBytes(dbf));
            Assert.Equal(fptBefore, File.ReadAllBytes(fpt));

            // …and it still opens as the ORIGINAL schema with its memo content intact (no orphaned pair).
            using var t = DbfTable.Open(dbf);
            Assert.Equal(original, t.GetRecord(0)!.Value.GetString("NOTE"));
            Assert.Contains(t.Columns, c => c.Name.Equals("A", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(t.Columns, c => c.Name.Equals("X", StringComparison.OrdinalIgnoreCase));
        }
        finally { Cleanup(dir); }
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  Finding 7 — WritableTarget.Dispose must SURFACE a Flush() failure, not swallow it. The DML
    //  computes the affected-count before disposal, so a swallowed flush failure would let
    //  INSERT/UPDATE/DELETE report SUCCESS on data that never reached disk. A failing flush must
    //  throw out of Dispose.
    // ════════════════════════════════════════════════════════════════════════════════════════
    [Fact]
    public void Finding7_WritableTarget_Dispose_SurfacesFlushFailure()
    {
        string dir = FreshTempDir();
        try
        {
            using var s = new VfpSession();
            s.OpenDirectory(dir);
            s.Execute("CREATE TABLE t (ID I NOT NULL, NAME C(10) NOT NULL)");

            var wt = s.OpenWritableTarget("t");

            // Force the flush in Dispose to fail by pre-disposing the writer (Flush() then throws
            // ObjectDisposedException). The pre-fix Dispose swallowed it; the fix rethrows as a
            // FoxDbfSqlException so the statement cannot report a phantom success.
            wt.Writer.Dispose();
            Assert.Throws<FoxDbfSqlException>(() => wt.Dispose());
        }
        finally { Cleanup(dir); }
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  Finding 9a — OpenWritableTarget must match an already-open table by its RESOLVED FILE PATH,
    //  not only alias==name, so a write against a table open under a CUSTOM alias releases and
    //  re-opens that area. Pre-fix it matched only the alias, opened a SECOND handle, and left the
    //  aliased area with a STALE read view (cached record count) that misses the new row.
    // ════════════════════════════════════════════════════════════════════════════════════════
    [Fact]
    public void Finding9a_WriteByName_RefreshesCustomAliasedArea()
    {
        string dir = FreshTempDir();
        try
        {
            using var s = new VfpSession();
            s.OpenDirectory(dir);
            s.Execute("CREATE TABLE t (ID I NOT NULL, V I NOT NULL)");
            s.Execute("INSERT INTO t (ID, V) VALUES (1, 10)");

            // Open under a CUSTOM alias (alias != table name), then write by the BASE name.
            s.Execute("USE t AS myalias");
            s.Execute("INSERT INTO t (ID, V) VALUES (2, 20)");

            // Reading THROUGH the aliased area must reflect the new row — pre-fix the stale cached
            // handle still reported a single row.
            var rows = SqlTestSupport.Materialize(s.Execute("SELECT ID FROM myalias")!);
            Assert.Equal(2, rows.Count);
        }
        finally { Cleanup(dir); }
    }

    // ════════════════════════════════════════════════════════════════════════════════════════
    //  Finding 9b — CdxFile's ctor runs BuildDirectory() unguarded; a parse throw must NOT leak the
    //  opened IndexFile/stream. Feeding Open a stream that throws mid-parse must dispose the stream.
    // ════════════════════════════════════════════════════════════════════════════════════════
    [Fact]
    public void Finding9b_CdxFile_Open_DisposesStream_OnParseThrow()
    {
        var stream = new ThrowOnReadStream(length: 1024);
        Assert.ThrowsAny<Exception>(() => CdxFile.Open(stream)); // leaveOpen defaults to false → CdxFile owns it
        Assert.True(stream.Disposed, "CdxFile.Open must dispose the owned stream when BuildDirectory throws.");
    }

    /// <summary>A seekable stream that reports a header-sized length but throws on every read, so
    /// IndexFile.Open succeeds (it reads nothing) and CdxFile's BuildDirectory faults on first read.</summary>
    private sealed class ThrowOnReadStream : Stream
    {
        private long _position;
        public bool Disposed { get; private set; }
        public ThrowOnReadStream(long length) => Length = length;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length { get; }
        public override long Position { get => _position; set => _position = value; }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new IOException("injected read fault mid-parse");
        public override int Read(Span<byte> buffer)
            => throw new IOException("injected read fault mid-parse");

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => Length + offset,
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
