using System;
using System.Collections.Generic;
using System.IO;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.MicroVfp;

/// <summary>
/// microVFP WRITE HOT-PATH accelerators (project-review 5.5). Four caches that turn a per-statement
/// SCAN+REPLACE / APPEND loop from O(n · filesize) into O(n):
/// <list type="number">
///   <item>a CACHED <see cref="DbfWriter"/> per open <c>.dbf</c> path — the writer is kept OPEN across
///     statements instead of re-opened (open+lock+write+flush+dispose) per REPLACE/DELETE. Opening a
///     <c>.dbf</c> read/write is the dominant cost (≈8&#160;ms measured vs ≈80&#160;µs for update+flush on an
///     already-open writer); caching removes it. The writer holds a <see cref="LockMode.Shared"/>
///     (<see cref="FileShare.ReadWrite"/>) handle — the SAME share the transient per-write writer took —
///     and NO byte-range lock is held between statements (they are per-mutation, released in
///     <c>WithLock</c>'s finally), so a concurrent VFP's RLOCK/FLOCK/USE behaviour is unchanged. The
///     area's READ handles are still refreshed by <c>ReopenFileAreas</c> after every write, so sibling
///     (USE..AGAIN) visibility is byte-for-byte identical.</item>
///   <item>a bounded PARSE cache for <see cref="Execute(string)"/> — the same snippet source re-lexes once.</item>
///   <item>a bounded compiled-EXPRESSION cache — a repeated DBC RULE/DEFAULT / enforced-write per-row
///     expression parses once and re-evaluates against the moving row.</item>
/// </list>
/// The incremental ordered-cache maintenance (point 3 of 5.5) lives in <c>VfpInterpreter.Navigation</c>
/// and <c>VfpInterpreter.Dml</c>; the record-level RI pre-image (point 2) in <c>VfpInterpreter.Ri</c>.
/// <para>
/// CACHING IS AUTOCOMMIT / PRG-TRANSACTION ONLY. Inside an ADO.NET copy-on-write transaction
/// (<see cref="VfpSession.TxBeginWritePath"/> set) the write path is a redirected private copy the Data
/// layer swaps on commit/rollback, so a persistent handle could straddle a file swap — there we fall back
/// to the transient per-write writer (byte-identical to the pre-5.5 path). A PRG <c>BEGIN TRANSACTION</c>
/// (the <c>_txn</c> snapshot stack) DOES cache: the cached writer is disposed before any whole-file
/// <see cref="RestoreSnapshot"/> rewrite so <c>File.WriteAllBytes</c> is never blocked.
/// </para>
/// </summary>
public sealed partial class VfpInterpreter
{
    // Full-path → the persistent Shared writer open on that .dbf (autocommit / PRG-txn). Keyed by path (not
    // area) so USE..AGAIN siblings share ONE writer and its live _recordCount, and so a write through any
    // area on the file goes through the same handle. OrdinalIgnoreCase to match the session's path compares.
    private readonly Dictionary<string, DbfWriter> _cachedWriters = new(StringComparer.OrdinalIgnoreCase);

    // Bounded parse/expression caches (short-lived session; a full Clear on overflow keeps them bounded
    // without LRU bookkeeping — a repeated source/expression stays hot, a stream of distinct ones just
    // churns without leaking). No global static state.
    private readonly Dictionary<string, PrgProgram> _parseCache = new(StringComparer.Ordinal);
    private const int ParseCacheCap = 512;
    private readonly Dictionary<string, VfpExpression> _exprCache = new(StringComparer.Ordinal);
    private const int ExprCacheCap = 2048;

    /// <summary>A writer to use for one statement's write to <paramref name="path"/>. In autocommit /
    /// PRG-transaction the returned lease wraps the PERSISTENT cached writer (its <see cref="WriterLease.Dispose"/>
    /// is a no-op — the writer stays open); inside an ADO.NET copy-on-write transaction it wraps a FRESH
    /// transient writer the lease disposes (the pre-5.5 behaviour). Callers still <c>Flush()</c> after the
    /// write, so persistence is identical either way.</summary>
    private WriterLease LeaseWriter(string path)
    {
        if (Session.TxBeginWritePath is null)
            return new WriterLease(GetOrOpenCachedWriter(path), dispose: false);
        // ADO.NET COW transaction: the Data layer owns the private-copy file lifecycle — never straddle it
        // with a persistent handle.
        return new WriterLease(DbfWriter.Open(path, new DbfOptions { LockMode = LockMode.Shared }), dispose: true);
    }

    private readonly struct WriterLease : IDisposable
    {
        public DbfWriter Writer { get; }
        private readonly bool _dispose;
        public WriterLease(DbfWriter writer, bool dispose) { Writer = writer; _dispose = dispose; }
        public void Dispose() { if (_dispose) Writer.Dispose(); }
    }

    private DbfWriter GetOrOpenCachedWriter(string path)
    {
        string key = Path.GetFullPath(path);
        if (_cachedWriters.TryGetValue(key, out var w)) return w;
        var writer = DbfWriter.Open(path, new DbfOptions { LockMode = LockMode.Shared });
        _cachedWriters[key] = writer;
        return writer;
    }

    /// <summary>Dispose + drop the cached writer for <paramref name="path"/> (if any). MUST run before any
    /// out-of-band rewrite/truncate of the file (whole-file <see cref="RestoreSnapshot"/>, PACK, an
    /// INDEX/REINDEX exclusive re-open) and after a foreign writer (SQL DML append) changed the record count
    /// under it, so a stale handle / cached <c>_recordCount</c> can never survive.</summary>
    private void InvalidateCachedWriter(string? path)
    {
        if (path is null) return;
        string key = Path.GetFullPath(path);
        if (_cachedWriters.Remove(key, out var w))
        {
            try { w.Dispose(); } catch { /* best-effort release */ }
        }
    }

    /// <summary>Dispose + drop every cached writer whose path is no longer riding an OPEN work area — the
    /// post-USE/close prune, so closing (or repurposing) the last area on a file releases its writer handle
    /// (the WriterReleased* pins: after USE/switch the <c>.dbf</c> must be FileShare.None-openable).</summary>
    private void PruneCachedWriters()
    {
        if (_cachedWriters.Count == 0) return;
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var wa in Session.OpenAreas)
            if (wa.Table.SourcePath is { } sp) live.Add(Path.GetFullPath(sp));
        List<string>? drop = null;
        foreach (var key in _cachedWriters.Keys)
            if (!live.Contains(key)) (drop ??= new()).Add(key);
        if (drop is null) return;
        foreach (var key in drop)
            if (_cachedWriters.Remove(key, out var w))
            {
                try { w.Dispose(); } catch { /* best-effort */ }
            }
    }

    /// <summary>Dispose EVERY cached writer (CLEAR ALL, and the session-disposing hook).</summary>
    private void DisposeAllCachedWriters()
    {
        foreach (var w in _cachedWriters.Values)
        {
            try { w.Dispose(); } catch { /* best-effort */ }
        }
        _cachedWriters.Clear();
    }

    /// <summary>Read a whole file into memory with <see cref="FileShare.ReadWrite"/> so a snapshot can be
    /// taken WHILE a cached writer holds the file open (plain <c>File.ReadAllBytes</c> requests
    /// FileShare.Read, which a live read/write handle denies). Returns null when the file is absent /
    /// unreadable (same fail-soft contract as the old <c>try{ReadAllBytes}catch{}</c>).</summary>
    private static byte[]? ReadAllBytesShared(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long len = fs.Length;
            var buf = new byte[len];
            int off = 0;
            while (off < buf.Length)
            {
                int n = fs.Read(buf, off, buf.Length - off);
                if (n <= 0) break;
                off += n;
            }
            return off == buf.Length ? buf : buf[..off];
        }
        catch { return null; }
    }

    // ── parse / expression caches (pathology 4: re-lex per Execute, re-parse per row) ──

    /// <summary>Parse <paramref name="source"/> to a <see cref="PrgProgram"/>, reusing a cached AST for an
    /// identical source string (the AST is immutable data the tree-walk only READS, so reuse is safe and
    /// also warms each <c>PrgExpr</c>'s lazily-parsed expression). Bounded.</summary>
    private PrgProgram ParseProgramCached(string source)
    {
        if (_parseCache.TryGetValue(source, out var prog)) return prog;
        prog = PrgParser.Parse(source);
        if (_parseCache.Count >= ParseCacheCap) _parseCache.Clear();
        _parseCache[source] = prog;
        return prog;
    }

    /// <summary>Parse <paramref name="normalized"/> (already run through <see cref="MicroVfpExprRewrite.Normalize"/>)
    /// to a reusable <see cref="VfpExpression"/>, cached by the normalized text so a DBC RULE/DEFAULT or an
    /// enforced-write per-row predicate parses once and re-evaluates against the moving row. Bounded.</summary>
    private VfpExpression ParseExpressionCached(string normalized)
    {
        if (_exprCache.TryGetValue(normalized, out var expr)) return expr;
        expr = VfpExpression.Parse(normalized);
        if (_exprCache.Count >= ExprCacheCap) _exprCache.Clear();
        _exprCache[normalized] = expr;
        return expr;
    }
}
