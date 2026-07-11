using System;
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
/// microVFP P3 batch 3 — field / record / index introspection + manipulation: ISBLANK() / BLANK,
/// NDX() / FLDLIST(), SYS(21|22|2021), KEYMATCH() (non-destructive), CLOSE INDEXES, SET BLOCKSIZE,
/// SET NOCPTRANS, SET TEXTMERGE [DELIMITERS], APPEND MEMO … FROM, REPLACE FROM ARRAY, COPY TAG /
/// COPY INDEXES round-trips, GETNEXTMODIFIED() over the real TableBuffer, SETFLDSTATE() bookkeeping.
///
/// Written TESTS-FIRST: every case pins a VFP9-verified PRG-level contract (the expected value of each
/// assertion was captured from the VFP9 runtime — see the sibling Internal oracle class for the pins).
/// RED until the interpreter is wired.
///
/// SAFETY: every case builds a FRESH synthetic table in its own throwaway temp dir (never a committed
/// fixture); the dir is removed on dispose. Public-safe — synthetic data only, no VFP9 runtime here.
/// </summary>
public sealed class MicroVfpP3FieldIdxTests
{
    // ─────────────────────────── synthetic-table scaffolding ───────────────────────────

    private sealed class Bench : IDisposable
    {
        public string Dir { get; }
        public VfpSession Session { get; private set; }
        public VfpInterpreter Interp { get; private set; }

        public Bench()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_p3fi_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Session = new VfpSession();
            Session.OpenDirectory(Dir);
            Interp = new VfpInterpreter(Session);
        }

        /// <summary>Create a fresh table with <paramref name="cols"/> and append <paramref name="rows"/>
        /// (each an object?[] positional row).</summary>
        public void Create(string name, DbfColumnDef[] cols, params object?[][] rows)
        {
            using var w = DbfWriter.Create(Path.Combine(Dir, name + ".dbf"), cols);
            foreach (var r in rows) w.AppendRecord(r);
            w.Flush();
        }

        public void Run(string prg) => Interp.Execute(prg);
        public decimal Num(string expr) => Interp.EvalExpression(expr).AsNumber;
        public string Str(string expr) => Interp.EvalExpression(expr).AsString;
        public bool Bool(string expr) => Interp.EvalExpression(expr).AsLogical;

        /// <summary>Drop + re-open a fresh session over the SAME dir (proves an index file written in one
        /// session is re-openable in the next).</summary>
        public void Reopen()
        {
            Session.Dispose();
            Session = new VfpSession();
            Session.OpenDirectory(Dir);
            Interp = new VfpInterpreter(Session);
        }

        public string File(string name) => Path.Combine(Dir, name);

        public void Dispose()
        {
            try { Session.Dispose(); } catch { }
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private static DbfColumnDef[] BlankCols() => new[]
    {
        new DbfColumnDef("cc", 'C', 6),
        new DbfColumnDef("nn", 'N', 6, 2),
        new DbfColumnDef("dd", 'D'),
        new DbfColumnDef("ll", 'L'),
        new DbfColumnDef("ii", 'I'),
    };

    // ─────────────────────────── (1) ISBLANK — literals ───────────────────────────

    [Fact]
    public void IsBlank_Literals_MatchVfp9()
    {
        using var b = new Bench();
        Assert.True(b.Bool("ISBLANK('')"));         // empty character → blank
        Assert.True(b.Bool("ISBLANK('   ')"));      // all-spaces character → blank
        Assert.False(b.Bool("ISBLANK('abc')"));     // non-empty character → not blank
        Assert.False(b.Bool("ISBLANK(0)"));         // numeric 0 → NOT blank (the whole point vs EMPTY())
        Assert.False(b.Bool("ISBLANK(.F.)"));       // logical .F. → not blank
        Assert.True(b.Bool("ISBLANK({})"));         // empty date → blank
    }

    // ─────────────────────────── (2) ISBLANK — the per-type FIELD matrix ───────────────────────────

    [Fact]
    public void IsBlank_FieldMatrix_MatchesVfp9()
    {
        using var b = new Bench();
        // rec1 real values; rec2 will be BLANKed; rec3 will be REPLACEd with empty/0/{}/.F.
        b.Create("btab", BlankCols(),
            new object?[] { "abc", 12.5m, new DateOnly(2026, 7, 4), true, 7 },
            new object?[] { "xyz", 99.9m, new DateOnly(2020, 1, 1), true, 5 },
            new object?[] { "zzz", 1m, new DateOnly(2021, 2, 2), true, 9 });
        b.Run("USE btab");

        // rec1 — everything has a real value ⇒ nothing blank.
        b.Run("GO 1");
        foreach (var f in new[] { "cc", "nn", "dd", "ll", "ii" })
            Assert.False(b.Bool($"ISBLANK({f})"), $"rec1 {f} should not be blank");

        // rec2 — BLANK writes the type's empty bytes (all-spaces for C/N/D/L, all-zero for I).
        b.Run("GO 2\nBLANK");
        Assert.True(b.Bool("ISBLANK(cc)"), "rec2 cc BLANKed → blank");
        Assert.True(b.Bool("ISBLANK(nn)"), "rec2 nn BLANKed → blank");
        Assert.True(b.Bool("ISBLANK(dd)"), "rec2 dd BLANKed → blank");
        Assert.True(b.Bool("ISBLANK(ll)"), "rec2 ll BLANKed → blank");
        Assert.False(b.Bool("ISBLANK(ii)"), "rec2 ii (binary integer) → NEVER blank");

        // rec3 — REPLACE with the empty VALUE of each type: numeric 0 and logical .F. are NOT blank
        //         (they carry a written value), but '' and {} decode to the blank bytes ⇒ blank.
        b.Run("GO 3\nREPLACE cc WITH '', nn WITH 0, dd WITH {}, ll WITH .F., ii WITH 0");
        Assert.True(b.Bool("ISBLANK(cc)"), "rec3 cc='' → blank");
        Assert.False(b.Bool("ISBLANK(nn)"), "rec3 nn=0 → NOT blank");
        Assert.True(b.Bool("ISBLANK(dd)"), "rec3 dd={} → blank");
        Assert.False(b.Bool("ISBLANK(ll)"), "rec3 ll=.F. → NOT blank");
        Assert.False(b.Bool("ISBLANK(ii)"), "rec3 ii=0 → not blank");
    }

    // ─────────────────────────── (3) BLANK FIELDS <list> ───────────────────────────

    [Fact]
    public void Blank_FieldsList_OnlyBlanksNamedFields()
    {
        using var b = new Bench();
        b.Create("btab", BlankCols(),
            new object?[] { "abc", 12.5m, new DateOnly(2026, 7, 4), true, 7 });
        b.Run("USE btab\nGO 1\nBLANK FIELDS nn");
        Assert.True(b.Bool("ISBLANK(nn)"), "nn was BLANKed");
        Assert.False(b.Bool("ISBLANK(cc)"), "cc was NOT in the FIELDS list → untouched");
        Assert.Equal("abc", b.Str("ALLTRIM(cc)"));
    }

    // ─────────────────────────── (4) NDX() — standalone .idx enumeration ───────────────────────────

    [Fact]
    public void Ndx_EnumeratesOpenStandaloneIdxFiles()
    {
        using var b = new Bench();
        b.Create("ntab", new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("nm", 'C', 6) },
            new object?[] { 3, "cc" }, new object?[] { 1, "aa" }, new object?[] { 2, "bb" });
        b.Run("USE ntab\nINDEX ON id TO alpha\nINDEX ON nm TO beta");
        // Both .idx are now open (INDEX ON … TO opens + controls). NDX walks them in open order.
        Assert.Equal("alpha", Path.GetFileNameWithoutExtension(b.Str("NDX(1)")).ToLowerInvariant());
        Assert.Equal("beta", Path.GetFileNameWithoutExtension(b.Str("NDX(2)")).ToLowerInvariant());
        Assert.Equal("", b.Str("NDX(3)"));   // past the end ⇒ empty string (safe for loops).
    }

    // ─────────────────────────── (5) FLDLIST() — SET FIELDS list (unmodelled ⇒ "") ───────────────────────────

    [Fact]
    public void FldList_WithoutSetFields_IsEmpty()
    {
        using var b = new Bench();
        b.Create("ftab", new[] { new DbfColumnDef("alpha", 'C', 5), new DbfColumnDef("beta", 'N', 4) });
        b.Run("USE ftab");
        // VFP9: FLDLIST() returns the SET FIELDS list; with no SET FIELDS it is "" (verified vs the VFP9 runtime).
        Assert.Equal("", b.Str("FLDLIST()"));
        Assert.Equal("", b.Str("FLDLIST(1)"));
    }

    // ─────────────────────────── (6) SYS(21) / SYS(22) / SYS(2021) ───────────────────────────

    [Fact]
    public void Sys21_22_2021_ControllingIndexIntrospection()
    {
        using var b = new Bench();
        b.Create("stab", new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("nm", 'C', 10) },
            new object?[] { 3, "cee" }, new object?[] { 1, "aay" }, new object?[] { 2, "bee" });
        // idtag = tag 1, nmtag = tag 2 (creation order). INDEX ON makes nmtag the controlling order.
        b.Run("USE stab\nINDEX ON id TAG idtag\nINDEX ON UPPER(nm) FOR id>0 TAG nmtag");

        Assert.Equal("2", b.Str("SYS(21)"));         // controlling = nmtag (tag #2)
        Assert.Equal("NMTAG", b.Str("SYS(22)"));     // controlling tag name
        Assert.Equal("", b.Str("SYS(2021, 1)"));     // idtag has no FOR filter
        Assert.Equal("ID>0", b.Str("SYS(2021, 2)")); // nmtag FOR expression (ALL-CAPS, like FOR())

        b.Run("SET ORDER TO idtag");
        Assert.Equal("1", b.Str("SYS(21)"));
        Assert.Equal("IDTAG", b.Str("SYS(22)"));

        b.Run("SET ORDER TO");                        // natural order — no controlling index.
        Assert.Equal("0", b.Str("SYS(21)"));
        Assert.Equal("", b.Str("SYS(22)"));
    }

    // ─────────────────────────── (7) KEYMATCH() — non-destructive ───────────────────────────

    [Fact]
    public void KeyMatch_DoesNotMovePointer()
    {
        using var b = new Bench();
        b.Create("ktab", new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("nm", 'C', 4) },
            new object?[] { 10, "a" }, new object?[] { 20, "b" }, new object?[] { 30, "c" });
        b.Run("USE ktab\nINDEX ON id TAG idtag\nGO 2");

        Assert.True(b.Bool("KEYMATCH(30, 1)"));       // 30 exists in tag 1
        Assert.Equal(2m, b.Num("RECNO()"));            // …but the pointer did NOT move.
        Assert.False(b.Bool("KEYMATCH(999, 1)"));     // 999 absent
        Assert.Equal(2m, b.Num("RECNO()"));            // …still parked on rec 2.
    }

    [Fact]
    public void InterpreterGeneralCollatedKeyMatch_HitAndMissDoNotMovePointer()
    {
        using var b = new Bench();
        b.Create("ktab", new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("nm", 'C', 8) },
            new object?[] { 10, "Alpha" }, new object?[] { 20, "Beta" }, new object?[] { 30, "Gamma" });
        b.Run("USE ktab\nSET COLLATE TO GENERAL\nINDEX ON nm TAG nmtag\nGO 2");

        Assert.True(b.Bool("KEYMATCH('alpha', 1)"));
        Assert.Equal(2m, b.Num("RECNO()"));
        Assert.False(b.Bool("KEYMATCH('missing', 1)"));
        Assert.Equal(2m, b.Num("RECNO()"));
    }

    // ─────────────────────────── (8) CLOSE INDEXES — non-structural only ───────────────────────────

    [Fact]
    public void CloseIndexes_ClosesNonStructural_KeepsStructural()
    {
        using var b = new Bench();
        b.Create("ctab", new[] { new DbfColumnDef("id", 'I') },
            new object?[] { 5 }, new object?[] { 3 });
        // structural idtag (in ctab.cdx) + standalone extra.idx (INDEX ON … TO controls it).
        b.Run("USE ctab\nINDEX ON id TAG idtag\nINDEX ON id TO extra");
        Assert.Equal(2m, b.Num("TAGCOUNT()"));         // structural tag + standalone idx
        Assert.Equal("EXTRA", b.Str("ORDER()"));

        b.Run("CLOSE INDEXES");
        Assert.Equal(1m, b.Num("TAGCOUNT()"));         // only the structural tag survives
        Assert.Equal("", b.Str("NDX(1)"));             // the standalone idx is closed
        Assert.Equal("", b.Str("ORDER()"));            // its controlling order reverted to natural
    }

    // ─────────────────────────── (9) SET BLOCKSIZE — subsequently-created .fpt ───────────────────────────

    [Fact]
    public void SetBlockSize_AffectsSubsequentlyCreatedMemoFile()
    {
        using var b = new Bench();
        Assert.Equal(64m, b.Num("SET('BLOCKSIZE')"));  // VFP default (NUMERIC — VARTYPE "N").

        b.Create("src", new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("mm", 'M') },
            new object?[] { 1, "hello" });
        b.Run("USE src");

        // 33..32767 ⇒ explicit bytes: 128 ⇒ a 128-byte .fpt block.
        b.Run("SET BLOCKSIZE TO 128");
        Assert.Equal(128m, b.Num("SET('BLOCKSIZE')"));
        b.Run("COPY TO out128");
        Assert.Equal(128, FptBlockSize(b.File("out128.fpt")));

        // 1..32 ⇒ multiples of 512: 1 ⇒ a 512-byte block.
        b.Run("SET BLOCKSIZE TO 1");
        b.Run("COPY TO out512");
        Assert.Equal(512, FptBlockSize(b.File("out512.fpt")));
    }

    /// <summary>The u16 big-endian FPT block size stored at file offset 6.</summary>
    private static int FptBlockSize(string fptPath)
    {
        using var fs = System.IO.File.OpenRead(fptPath);
        var h = new byte[8];
        fs.ReadExactly(h, 0, 8);
        return (h[6] << 8) | h[7];
    }

    // ─────────────────────────── (10) SET NOCPTRANS — accepted, inert ───────────────────────────

    [Fact]
    public void SetNoCpTrans_IsAcceptedAndInert()
    {
        using var b = new Bench();
        b.Create("cptab", new[] { new DbfColumnDef("cc", 'C', 6) }, new object?[] { "hi" });
        b.Run("USE cptab\nGO 1");
        // The FoxPro-2.x runtime SET NOCPTRANS command is accepted (not a syntax error) and has no effect
        // on modern persistently-flagged tables (the real semantics live on the column flag).
        b.Run("SET NOCPTRANS TO cc");
        Assert.Equal("hi", b.Str("ALLTRIM(cc)"));
    }

    // ─────────────────────────── (11) SET TEXTMERGE [DELIMITERS] ───────────────────────────

    [Fact]
    public void SetTextMerge_OnOff_And_Delimiters()
    {
        using var b = new Bench();
        Assert.Equal("OFF", b.Str("SET('TEXTMERGE')"));
        b.Run("SET TEXTMERGE ON");
        Assert.Equal("ON", b.Str("SET('TEXTMERGE')"));
        b.Run("SET TEXTMERGE OFF");
        Assert.Equal("OFF", b.Str("SET('TEXTMERGE')"));

        // SET TEXTMERGE DELIMITERS TO … changes the delimiters TEXTMERGE() uses by default.
        b.Run("gcX = 'World'");
        Assert.Equal("Hi World!", b.Str("TEXTMERGE('Hi <<gcX>>!')"));   // default << >>
        b.Run("SET TEXTMERGE DELIMITERS TO '{{', '}}'");
        Assert.Equal("Hi World!", b.Str("TEXTMERGE('Hi {{gcX}}!')"));   // custom delimiters honoured
        b.Run("SET TEXTMERGE DELIMITERS TO");                            // reset to << >>
        Assert.Equal("Hi World!", b.Str("TEXTMERGE('Hi <<gcX>>!')"));
    }

    // ─────────────────────────── (12) APPEND MEMO … FROM [OVERWRITE] ───────────────────────────

    [Fact]
    public void AppendMemo_FromFile_AdditiveThenOverwrite()
    {
        using var b = new Bench();
        System.IO.File.WriteAllText(b.File("memo.txt"), "hello world");
        b.Create("mtab", new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("mm", 'M') },
            new object?[] { 1, "start-" });
        b.Run("USE mtab\nGO 1");

        b.Run("APPEND MEMO mm FROM memo.txt");          // default = additive
        Assert.Equal("start-hello world", b.Str("mm"));

        b.Run("APPEND MEMO mm FROM memo.txt OVERWRITE"); // OVERWRITE = replace
        Assert.Equal("hello world", b.Str("mm"));
    }

    // ─────────────────────────── (13) REPLACE FROM ARRAY — positional row map ───────────────────────────

    [Fact]
    public void ReplaceFromArray_MapsElementsToFieldsPositionally()
    {
        using var b = new Bench();
        b.Create("rtab", new[]
        {
            new DbfColumnDef("id", 'I'), new DbfColumnDef("nm", 'C', 10), new DbfColumnDef("qty", 'N', 5),
        }, new object?[] { 1, "old", 9 });
        b.Run("USE rtab\nGO 1");
        b.Run("DIMENSION aRow(3)\naRow(1) = 7\naRow(2) = 'new'\naRow(3) = 42");
        b.Run("REPLACE FROM ARRAY aRow");
        Assert.Equal(7m, b.Num("id"));
        Assert.Equal("new", b.Str("ALLTRIM(nm)"));
        Assert.Equal(42m, b.Num("qty"));
    }

    // ─────────────────────────── (14) COPY TAG <tag> TO <idx> round-trip ───────────────────────────

    [Fact]
    public void CopyTag_ToStandaloneIdx_SeeksAndIsByteSane()
    {
        using var b = new Bench();
        b.Create("ttab", new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("nm", 'C', 6) },
            new object?[] { 3, "cc" }, new object?[] { 1, "aa" }, new object?[] { 2, "bb" });
        b.Run("USE ttab\nINDEX ON id TAG idtag\nCOPY TAG idtag TO cpidx");
        Assert.True(File.Exists(b.File("cpidx.idx")), "COPY TAG did not create cpidx.idx");

        // byte-sane: our own reader round-trips it in ascending-id order (recnos 2,3,1 for ids 1,2,3).
        using (var idx = IdxFile.Open(b.File("cpidx.idx")))
        {
            var recnos = idx.EnumerateEntries().Select(e => (int)e.RecordNumber).ToArray();
            Assert.Equal(new[] { 2, 3, 1 }, recnos);
        }

        // re-open + use the converted .idx: GO TOP is the smallest id, SEEK finds a key.
        b.Reopen();
        b.Run("USE ttab\nSET INDEX TO cpidx\nSET ORDER TO cpidx\nGO TOP");
        Assert.Equal(1m, b.Num("id"));
        b.Run("=SEEK(2, 'ttab')");
        Assert.True(b.Bool("FOUND()"));
        Assert.Equal(2m, b.Num("id"));
    }

    // ─────────────────────────── (15) COPY INDEXES <idx> TO <cdx> round-trip ───────────────────────────

    [Fact]
    public void CopyIndexes_ToCdxTag_SeeksAfterConversion()
    {
        using var b = new Bench();
        b.Create("itab", new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("nm", 'C', 6) },
            new object?[] { 3, "cc" }, new object?[] { 1, "aa" }, new object?[] { 2, "bb" });
        // Build a standalone byid.idx, then convert it into a NEW tag in more.cdx.
        b.Run("USE itab\nINDEX ON id TO byid\nCOPY INDEXES byid TO more.cdx");
        Assert.True(File.Exists(b.File("more.cdx")), "COPY INDEXES did not create more.cdx");

        b.Reopen();
        b.Run("USE itab\nSET INDEX TO more.cdx\nSET ORDER TO byid\nGO TOP");
        Assert.Equal(1m, b.Num("id"));                 // ascending id ⇒ top is id 1
        b.Run("=SEEK(3, 'itab', 'byid')");
        Assert.True(b.Bool("FOUND()"));
        Assert.Equal(3m, b.Num("id"));
    }

    // ─────────────────────────── (16) GETNEXTMODIFIED — over the real TableBuffer ───────────────────────────

    [Fact]
    public void GetNextModified_WalksBufferedRowsInRecnoOrder()
    {
        using var b = new Bench();
        b.Create("gtab", new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("nm", 'C', 10) },
            new object?[] { 1, "a" }, new object?[] { 2, "b" },
            new object?[] { 3, "c" }, new object?[] { 4, "d" });
        b.Run("USE gtab\n=CURSORSETPROP('Buffering', 5)");
        b.Run("GO 2\nREPLACE nm WITH 'B2'");
        b.Run("GO 4\nREPLACE nm WITH 'D4'");

        Assert.Equal(2m, b.Num("GETNEXTMODIFIED(0)"));   // first modified existing row
        Assert.Equal(4m, b.Num("GETNEXTMODIFIED(2)"));   // next after rec 2
        Assert.Equal(0m, b.Num("GETNEXTMODIFIED(4)"));   // none after rec 4 (no appends yet)

        // TWO buffered APPENDs: after the existing rows, the FIRST append reports as −1; feeding −1 back
        // walks to the SECOND append (−2), then 0. New records are DESCENDING negative recnos walked like
        // existing rows (verified vs the VFP9 runtime: GNM-seq 2,-1,-2,0). A naive constant −1 would loop forever
        // (−1 matches every positive recno when fed back) and never reach the 2nd append.
        b.Run("INSERT INTO gtab (id, nm) VALUES (5, 'e')");
        b.Run("INSERT INTO gtab (id, nm) VALUES (6, 'f')");
        Assert.Equal(-1m, b.Num("GETNEXTMODIFIED(4)"));    // first buffered append
        Assert.Equal(-2m, b.Num("GETNEXTMODIFIED(-1)"));   // second append (feed −1 back)
        Assert.Equal(0m, b.Num("GETNEXTMODIFIED(-2)"));    // appends exhausted

        // Unbuffered ⇒ a catchable VFP error 1596 ("Table buffering is not enabled."), NOT a silent 0.
        b.Run("=TABLEREVERT(.T.)\n=CURSORSETPROP('Buffering', 1)");
        var ex = Assert.Throws<MicroVfpRuntimeException>(() => b.Run("=GETNEXTMODIFIED(0)"));
        Assert.Equal(1596, ex.VfpErrorNumber);
    }

    // ─────────────────────────── (17) SETFLDSTATE — bookkeeping, inert on real tables ───────────────────────────

    [Fact]
    public void SetFldState_ValidatesButIsInertOnRealTables()
    {
        using var b = new Bench();
        b.Create("sftab", new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("nm", 'C', 10) },
            new object?[] { 1, "a" }, new object?[] { 2, "b" });
        b.Run("USE sftab\n=CURSORSETPROP('Buffering', 5)\nGO 1");

        // A valid (field, state) is accepted …
        Assert.True(b.Bool("SETFLDSTATE('nm', 2)"));
        // … but on a REAL table it is inert: GETFLDSTATE keeps deriving from the ACTUAL buffer (s4g395),
        // so an unedited field is still 1 despite the SETFLDSTATE(…,2) call above.
        Assert.Equal(1m, b.Num("GETFLDSTATE('nm')"));
        // An out-of-range field number is rejected.
        Assert.False(b.Bool("SETFLDSTATE(99, 2)"));
    }

    [Fact]
    public void FldState_UnknownNameDoesNotAliasNumericZero()
    {
        using var b = new Bench();
        b.Create("fntab", new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("nm", 'C', 10) },
            new object?[] { 1, "a" });
        b.Run("USE fntab\n=CURSORSETPROP('Buffering', 5)\nGO 1");

        Assert.Equal(1m, b.Num("GETFLDSTATE(0)"));
        Assert.True(b.Bool("SETFLDSTATE(0, 2)"));
        Assert.Equal(1m, b.Num("GETFLDSTATE(0)")); // SETFLDSTATE is inert for a real table.

        var exGet = Assert.Throws<MicroVfpRuntimeException>(() => b.Run("=GETFLDSTATE('typo')"));
        Assert.Equal(11, exGet.VfpErrorNumber);
        var exSet = Assert.Throws<MicroVfpRuntimeException>(() => b.Run("=SETFLDSTATE('typo', 2)"));
        Assert.Equal(11, exSet.VfpErrorNumber);
    }

    // ─────────────────────────── (18) IDXCOLLATE — MACHINE vs GENERAL ───────────────────────────

    [Fact]
    public void IdxCollate_ReportsTagCollation()
    {
        using var b = new Bench();
        b.Create("cotab", new[] { new DbfColumnDef("nm", 'C', 8) },
            new object?[] { "abc" }, new object?[] { "ABD" });
        b.Run("USE cotab\nINDEX ON nm TAG machtag");        // default MACHINE
        b.Run("SET COLLATE TO GENERAL\nINDEX ON nm TAG gentag");
        b.Run("SET COLLATE TO MACHINE");

        Assert.Equal("MACHINE", b.Str("IDXCOLLATE(TAGNO('machtag'))"));
        Assert.Equal("GENERAL", b.Str("IDXCOLLATE(TAGNO('gentag'))"));
    }

    // ─────────────────────────── (19) SET UNIQUE — session default for a clause-less INDEX ───────────────────────────

    [Fact]
    public void SetUnique_On_MakesClauselessIndexUnique()
    {
        using var b = new Bench();
        b.Create("utab", new[] { new DbfColumnDef("id", 'I') },
            new object?[] { 1 }, new object?[] { 1 }, new object?[] { 2 });
        b.Run("USE utab\nSET UNIQUE ON\nINDEX ON id TAG idtag\nSET UNIQUE OFF");
        Assert.True(b.Bool("UNIQUE(TAGNO('idtag'))"));   // built UNIQUE via the session default
    }

    // ─────────────────────────── (20) GET/SETFLDSTATE on an unbuffered area → error 1586 ───────────────────────────

    [Fact]
    public void FldState_OnUnbufferedArea_RaisesError1586()
    {
        using var b = new Bench();
        b.Create("sftab", new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("nm", 'C', 10) },
            new object?[] { 1, "a" });
        b.Run("USE sftab\nGO 1");   // Buffering defaults to 1 (unbuffered)

        // Both GETFLDSTATE and SETFLDSTATE require buffering — on an unbuffered work area the VFP9 runtime
        // raises a CATCHABLE error 1586 ("Function requires row or table buffering mode."), not a silent 1.
        var exG = Assert.Throws<MicroVfpRuntimeException>(() => b.Run("=GETFLDSTATE('nm')"));
        Assert.Equal(1586, exG.VfpErrorNumber);
        var exS = Assert.Throws<MicroVfpRuntimeException>(() => b.Run("=SETFLDSTATE('nm', 2)"));
        Assert.Equal(1586, exS.VfpErrorNumber);
    }

    // ─────────────────────────── (21) REPLACE FROM ARRAY — 2-D uses row 1 only, no spill ───────────────────────────

    [Fact]
    public void ReplaceFromArray_TwoDimArray_UsesRow1Only_NoSpill()
    {
        using var b = new Bench();
        b.Create("rtab", new[]
        {
            new DbfColumnDef("id", 'I'), new DbfColumnDef("nm", 'C', 6), new DbfColumnDef("qty", 'I'),
        }, new object?[] { 100, "orig", 200 });
        b.Run("USE rtab\nGO 1");

        // A 2×2 array — row 1 = (7,'new'), row 2 = (9,'zzz'). Against 3 fields, only row 1's TWO elements
        // map (id, nm); qty must stay 200. The pre-fix code used the FLATTENED length (4) with a linear
        // subscript, spilling row 2's element (9) into qty — this pins that it no longer does.
        b.Run("DIMENSION aRow(2,2)");
        b.Run("aRow(1,1) = 7\naRow(1,2) = 'new'\naRow(2,1) = 9\naRow(2,2) = 'zzz'");
        b.Run("REPLACE FROM ARRAY aRow");
        Assert.Equal(7m, b.Num("id"));
        Assert.Equal("new", b.Str("ALLTRIM(nm)"));
        Assert.Equal(200m, b.Num("qty"));   // untouched — NOT 9 (no row-2 spill)
    }

    // ─────────────────────────── (22) REPLACE FROM ARRAY — scoped (row-per-record) form rejected ───────────────────────────

    [Fact]
    public void ReplaceFromArray_ScopedForm_IsRejected()
    {
        using var b = new Bench();
        b.Create("rtab", new[] { new DbfColumnDef("id", 'I') },
            new object?[] { 1 }, new object?[] { 2 });
        b.Run("USE rtab\nGO 1\nDIMENSION aRow(1)\naRow(1) = 9");

        // The multi-record scope form maps one array ROW per record — microVFP does not model REPLACE
        // scope iteration, so it is REJECTED (a FLAG) rather than silently writing array row 1 into only
        // the current record. Covers both the trailing bare scope (ALL) and a FOR filter.
        Assert.Throws<MicroVfpRuntimeException>(() => b.Run("REPLACE FROM ARRAY aRow ALL"));
        Assert.Throws<MicroVfpRuntimeException>(() => b.Run("REPLACE FROM ARRAY aRow FOR id>0"));
        // The current-record (unscoped) form still works.
        b.Run("GO 1\nREPLACE FROM ARRAY aRow");
        Assert.Equal(9m, b.Num("id"));
    }

    // ─────────────────────────── (23) REPLACE datetime WITH {} round-trips blank + empty ───────────────────────────

    [Fact]
    public void ReplaceDateTime_WithEmptyLiteral_RoundTripsBlankAndEmpty()
    {
        using var b = new Bench();
        b.Create("dttab", new[] { new DbfColumnDef("tt", 'T') },
            new object?[] { new DateTime(2026, 7, 4, 10, 30, 0) });
        b.Run("USE dttab\nGO 1");
        Assert.False(b.Bool("EMPTY(tt)"));     // a real datetime is neither empty …
        Assert.False(b.Bool("ISBLANK(tt)"));   // … nor blank.

        // REPLACE tt WITH {} must write the all-zero empty sentinel (mirrors the D-field {} blank fix) —
        // EMPTY()/ISBLANK() flip to .T. and it round-trips as empty, NOT a real {^0001-01-01}. Verified vs
        // the VFP9 runtime (DT-blank:BE).
        b.Run("REPLACE tt WITH {}");
        Assert.True(b.Bool("EMPTY(tt)"));      // {} datetime → empty
        Assert.True(b.Bool("ISBLANK(tt)"));    // {} datetime → blank
    }

    // ─────────────────────────── (24) APPEND MEMO — binary content copied byte-for-byte ───────────────────────────

    [Fact]
    public void AppendMemo_BinaryContent_IsCopiedByteForByte()
    {
        using var b = new Bench();
        // Bytes a UTF-8 text read would MANGLE: a UTF-8 BOM (EF BB BF), an embedded NUL, and high bytes
        // (FF, 80) that are not valid standalone UTF-8 — File.ReadAllText stripped the BOM and emitted
        // U+FFFD replacement chars. The fix reads raw bytes and copies them 1:1 (hackfox s4g066).
        byte[] payload = { 0xEF, 0xBB, 0xBF, 0x00, 0xFF, 0x80, 0x41 };
        System.IO.File.WriteAllBytes(b.File("bin.dat"), payload);
        b.Create("mtab", new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("mm", 'M', binary: true) },
            new object?[] { 1, "" });
        b.Run("USE mtab\nGO 1");
        b.Run("APPEND MEMO mm FROM bin.dat");

        // A NOCPTRANS memo round-trips via Latin1 (byte k ⇒ char U+00xx), so every byte is recoverable.
        Assert.Equal(payload.Length, (int)b.Num("LEN(mm)"));
        Assert.Equal(0xEF, (int)b.Num("ASC(SUBSTR(mm,1,1))"));   // BOM byte preserved, not stripped
        Assert.Equal(0x00, (int)b.Num("ASC(SUBSTR(mm,4,1))"));   // embedded NUL preserved
        Assert.Equal(0xFF, (int)b.Num("ASC(SUBSTR(mm,5,1))"));   // high byte preserved, not U+FFFD
        Assert.Equal(0x80, (int)b.Num("ASC(SUBSTR(mm,6,1))"));
    }

    // ─────────────────────────── (25) APPEND MEMO — AS nCodePage is rejected (FLAG) ───────────────────────────

    [Fact]
    public void AppendMemo_AsCodePage_IsRejected()
    {
        using var b = new Bench();
        System.IO.File.WriteAllText(b.File("memo.txt"), "hi");
        b.Create("mtab", new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("mm", 'M') },
            new object?[] { 1, "" });
        b.Run("USE mtab\nGO 1");
        // The AS nCodePage source-codepage conversion is unsupported (without AS, VFP copies 1:1) — it is
        // explicitly rejected rather than silently dropped.
        Assert.Throws<MicroVfpRuntimeException>(() => b.Run("APPEND MEMO mm FROM memo.txt AS 1252"));
    }
}
