using System;
using System.IO;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Shared scaffolding for the microVFP (P1b) interpreter tests. EVERYTHING runs on TEMP COPIES of the
/// fixtures — NewID / REPLACE / DELETE MUTATE, so the committed <c>Tastrade_VFPData/</c> original is
/// never touched (memory: test-data-and-fixtures + the task SAFETY rule). The counterpart internal-only
/// stored-procedure corpus and the local oracle-executable detection live in a sibling internal-only
/// helper class in CrossVault.FoxDbf.Tests.Internal, not here.
/// </summary>
internal static class MicroVfpTestSupport
{
    /// <summary>The authoritative TasTrade (Microsoft sample) stored-procedure corpus.</summary>
    internal static string TastradeSp => Path.Combine(Fixtures.RepoRoot, "analysis", "tastrade_sp.prg");

    /// <summary>A throwaway temp directory; deleted on Dispose (best-effort).</summary>
    internal sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir(string tag)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "foxdbf_uvfp_" + tag + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>Recursively copies a whole fixture DATABASE directory (e.g. <c>Tastrade_VFPData</c>:
    /// .dbc/.dct/.dcx + every member .dbf/.cdx/.fpt) into a fresh temp directory and returns its path.
    /// The copy is fully writable so NewID/REPLACE/DELETE can mutate it safely.</summary>
    internal static string CopyDatabase(string sourceDir, TempDir into)
    {
        string dest = into.Path;
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            string name = System.IO.Path.GetFileName(file);
            System.IO.File.Copy(file, System.IO.Path.Combine(dest, name), overwrite: true);
        }
        return dest;
    }

    /// <summary>Opens a fresh interpreter over a TEMP COPY of the TasTrade database, with the SP corpus
    /// loaded — VFP defaults (DELETED ON / EXACT OFF / ANSI OFF), matching the oracle .prg.</summary>
    internal static VfpInterpreter NewTastrade(TempDir dir, out VfpSession session)
    {
        string copy = CopyDatabase(Path.Combine(Fixtures.RepoRoot, "Tastrade_VFPData"), dir);
        session = new VfpSession();
        session.OpenDatabase(Path.Combine(copy, "tastrade.dbc"));
        var interp = new VfpInterpreter(session);
        interp.LoadFile(TastradeSp);
        return interp;
    }

    /// <summary>Builds an interpreter over a small in-memory PRG (no data session needed) for the
    /// pure-scoping / control-flow unit tests.</summary>
    internal static VfpInterpreter NewFromSource(string prg, out VfpSession session)
    {
        session = new VfpSession();
        var interp = new VfpInterpreter(session);
        interp.Load(PrgParser.Parse(prg));
        return interp;
    }
}
