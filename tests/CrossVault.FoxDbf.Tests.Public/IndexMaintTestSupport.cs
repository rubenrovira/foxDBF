using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Shared scaffolding for the WRITE-PATH incremental CDX-maintenance suite (project-review finding
/// 5.1). Every helper works on THROWAWAY temp copies only — never a committed fixture — so the
/// whole suite is public-safe (synthetic data, no VFP9 oracle here; the oracle golden lives in the
/// Internal project). The read-back always RE-OPENS the on-disk <c>.cdx</c> through the independent
/// <see cref="CdxFile"/>/<see cref="CdxTag"/> reader (a different code path from the writer), which
/// is what "the index was maintained" must mean.
/// </summary>
internal static class IndexMaintTestSupport
{
    /// <summary>A throwaway temp directory; removed on Dispose (best-effort).</summary>
    internal sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "foxdbf_idxmaint_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>Create a temp table from <paramref name="cols"/>, append <paramref name="rows"/> (one
    /// object?[] per record, physical order), then build a tag for every <paramref name="tags"/> entry
    /// (which advertises + writes the structural <c>.cdx</c>). Returns the .dbf path.</summary>
    internal static string CreateTable(string dir, string name, DbfColumnDef[] cols,
        IEnumerable<object?[]> rows, params CdxTagDefinition[] tags)
    {
        string dbf = System.IO.Path.Combine(dir, name);
        using var w = DbfWriter.Create(dbf, cols, new DbfCreateOptions { Overwrite = true });
        foreach (var r in rows)
            w.AppendRecord(r);
        foreach (var t in tags)
            w.CreateTag(t);
        return dbf;
    }

    /// <summary>Open the .dbf + its structural .cdx, resolve <paramref name="tagName"/>, run
    /// <paramref name="f"/> while the handles are held OPEN (the reader streams pages), return its result.</summary>
    internal static T WithTag<T>(string dbf, string tagName, Func<CdxTag, T> f)
    {
        string cdx = System.IO.Path.ChangeExtension(dbf, ".cdx");
        Assert.True(File.Exists(cdx), $"no structural .cdx beside {dbf}");
        using var table = DbfTable.Open(dbf, new DbfOptions { LockMode = LockMode.Shared });
        using var cdxFile = CdxFile.Open(cdx, table);
        var tag = cdxFile.Tag(tagName) ?? cdxFile.Tag(tagName.ToUpperInvariant());
        Assert.NotNull(tag);
        return f(tag!);
    }

    /// <summary>The tag's entries' record numbers, in index order.</summary>
    internal static int[] Recnos(string dbf, string tag)
        => WithTag(dbf, tag, t => t.EnumerateEntries().Select(e => (int)e.RecordNumber).ToArray());

    /// <summary>The tag's decoded CHARACTER keys (trailing pad trimmed), in index order.</summary>
    internal static string[] StrKeys(string dbf, string tag)
        => WithTag(dbf, tag, t => t.EnumerateEntries()
            .Select(e => (t.DecodeKey(e.Key).AsString ?? string.Empty).TrimEnd()).ToArray());

    /// <summary>SEEK a value through the on-disk tag: its 1-based recno, or null when absent. For a
    /// CHARACTER tag this is an EXACT (blank-padded) match — a full-value probe, NOT a partial/prefix hit
    /// (so e.g. seeking "Smith" does not spuriously land on "Smithson"), which is what these assertions
    /// mean by "the key is / is no longer present".</summary>
    internal static uint? Seek(string dbf, string tag, object value)
        => WithTag(dbf, tag, t => value is string s && t.IsCharacterKey
            ? t.Seek(System.Text.Encoding.Latin1.GetBytes(s).AsSpan(), exact: true)
            : t.Seek(value));

    /// <summary>The number of entries in the tag.</summary>
    internal static int EntryCount(string dbf, string tag)
        => WithTag(dbf, tag, t => t.EnumerateEntries().Count());

    /// <summary>True when a structural .cdx sits beside the table.</summary>
    internal static bool CdxExists(string dbf)
        => File.Exists(System.IO.Path.ChangeExtension(dbf, ".cdx"));

    /// <summary>A microVFP interpreter over a directory (its work-area session), disposed together.</summary>
    internal sealed class Bench : IDisposable
    {
        public VfpSession Session { get; }
        public VfpInterpreter Interp { get; }
        public Bench(string dir)
        {
            Session = new VfpSession();
            Session.OpenDirectory(dir);
            Interp = new VfpInterpreter(Session);
        }
        public void Run(string prg) => Interp.Execute(prg);
        public bool Bool(string expr) => Interp.EvalExpression(expr).AsLogical;
        public decimal Num(string expr) => Interp.EvalExpression(expr).AsNumber;
        public void Dispose() { try { Session.Dispose(); } catch { /* best-effort */ } }
    }
}
