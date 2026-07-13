using System;
using System.IO;
using System.Text;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP P3 — the LOW-LEVEL FILE I/O family (MICROVFP_EXTENSIONS_BACKLOG.md §C.2):
/// FOPEN/FCREATE/FCLOSE/FREAD/FWRITE/FGETS/FPUTS/FSEEK/FEOF/FERROR/FFLUSH/FCHSIZE plus the handle-table
/// lifecycle (numbering, invalid-handle conventions, independence from work areas, release on CLOSE ALL /
/// dispose). Every expected value is PINNED to the VFP9 runtime (probed authoritatively — see the sibling
/// oracle test in the Internal project); RED until the interpreter implements the family.
///
/// Byte-orientation: low-level I/O is BYTE-based in VFP under the session code page (Latin1 default) — no
/// BOM, no newline translation beyond the documented FGETS/FPUTS terminator handling. Every file lives in
/// a throwaway temp dir; no fixture, no customer data, no oracle in this public project.
/// </summary>
public sealed class MicroVfpP3FileIoTests
{
    // ─────────────────────────── bare-interpreter harness (no data session needed) ───────────────────────────

    private sealed class H : IDisposable
    {
        public VfpSession Session { get; }
        public VfpInterpreter Interp { get; }
        public MicroVfpTestSupport.TempDir Dir { get; }
        public H()
        {
            Session = new VfpSession();
            Interp = new VfpInterpreter(Session);
            Dir = new MicroVfpTestSupport.TempDir("p3fileio");
        }
        public void Run(string prg) => Interp.Execute(prg);
        public string Str(string e) => Interp.EvalExpression(e).AsString;
        public int Int(string e) => Interp.EvalExpression(e).AsInteger;
        public bool Bool(string e) => Interp.EvalExpression(e).AsLogical;
        public bool IsNull(string e) => Interp.EvalExpression(e).IsNull;
        // A VFP string literal for a filesystem path (single-quoted; temp paths carry no quote chars).
        public string Q(string name) => "'" + Dir.File(name) + "'";
        public void Dispose() { try { Session.Dispose(); } catch { } Dir.Dispose(); }
    }

    // ─────────────────────────── handle numbering + reuse ───────────────────────────

    [Fact]
    public void Handles_Are_Positive_Distinct_And_Reuse_The_Lowest_Freed_Slot()
    {
        using var h = new H();
        // First handle in a fresh interpreter is 1 (microVFP internal convention: lowest-available from 1,
        // never 0, never the -1 error sentinel). VFP's own base (16) is runtime-internal and NOT oracle-compared.
        // Each FCREATE is assigned ONCE (a bare FCREATE call would itself open a new handle).
        h.Run($"n1 = FCREATE({h.Q("a.tmp")})");
        h.Run($"n2 = FCREATE({h.Q("b.tmp")})");
        h.Run($"n3 = FCREATE({h.Q("c.tmp")})");
        Assert.Equal(1, h.Int("n1"));
        Assert.Equal(2, h.Int("n2"));
        Assert.Equal(3, h.Int("n3"));
        h.Run("=FCLOSE(n2)");                                   // free slot 2
        h.Run($"n4 = FCREATE({h.Q("d.tmp")})");
        Assert.Equal(2, h.Int("n4"));                          // reuse the lowest freed slot
    }

    // ─────────────────────────── FCREATE → FWRITE/FPUTS → FCLOSE → FOPEN → FREAD/FGETS round-trip ───────────────────────────

    [Fact]
    public void RoundTrip_Is_Byte_Exact_Including_Crlf_And_Latin1_HighBytes()
    {
        using var h = new H();
        h.Run($"nh = FCREATE({h.Q("rt.bin")})");
        // FPUTS appends CRLF and its return counts it: "Caf"+é (4 bytes) + CRLF = 6.
        Assert.Equal(6, h.Int("FPUTS(nh, 'Caf' + CHR(233))"));
        // FWRITE writes exactly, no terminator, returns the byte count.
        Assert.Equal(2, h.Int("FWRITE(nh, 'XY')"));
        Assert.True(h.Bool("FCLOSE(nh)"));

        // The on-disk bytes are byte-exact under Latin1 (0xE9 = é), CRLF literal, no BOM.
        byte[] onDisk = File.ReadAllBytes(h.Dir.File("rt.bin"));
        Assert.Equal(new byte[] { 0x43, 0x61, 0x66, 0xE9, 0x0D, 0x0A, 0x58, 0x59 }, onDisk);

        // Reopen (default mode 0 = read-only) and read back.
        h.Run($"nr = FOPEN({h.Q("rt.bin")})");
        // FGETS strips the CRLF terminator and returns the 4-char line (é round-trips 1:1).
        Assert.Equal("Café", h.Str("FGETS(nr)"));
        // FREAD reads exactly-n from the position past the consumed CRLF.
        Assert.Equal("XY", h.Str("FREAD(nr, 2)"));
        Assert.True(h.Bool("FEOF(nr)"));
        Assert.True(h.Bool("FCLOSE(nr)"));
    }

    // ─────────────────────────── FWRITE / FPUTS nBytes CAP (never pads) ───────────────────────────

    [Fact]
    public void FWrite_And_FPuts_nBytes_Caps_The_String_And_Never_Pads()
    {
        using var h = new H();
        h.Run($"nh = FCREATE({h.Q("cap.bin")})");
        Assert.Equal(3, h.Int("FWRITE(nh, 'abcde', 3)"));   // truncate to first 3
        Assert.Equal(2, h.Int("FWRITE(nh, 'pq', 5)"));      // len 2 < 5 → only 2 written, NO pad
        Assert.Equal(5, h.Int("FPUTS(nh, 'abcdef', 3)"));   // "abc" + CRLF = 5
        Assert.Equal(4, h.Int("FPUTS(nh, 'xy', 5)"));       // "xy" (no pad) + CRLF = 4
        h.Run("=FCLOSE(nh)");
        byte[] onDisk = File.ReadAllBytes(h.Dir.File("cap.bin"));
        Assert.Equal(Encoding.Latin1.GetBytes("abcpqabc\r\nxy\r\n"), onDisk);
    }

    // ─────────────────────────── FGETS terminator handling (CRLF / lone LF / lone CR) + 254 default ───────────────────────────

    [Fact]
    public void FGets_Strips_Crlf_LoneLf_And_LoneCr_Terminators()
    {
        using var h = new H();
        h.Run($"nh = FCREATE({h.Q("g.bin")})");
        // "aa\nbb\rcc\r\ndd" — lone LF, lone CR, CRLF, then unterminated tail.
        h.Run("=FWRITE(nh, 'aa' + CHR(10) + 'bb' + CHR(13) + 'cc' + CHR(13) + CHR(10) + 'dd')");
        h.Run("=FCLOSE(nh)");
        h.Run($"nr = FOPEN({h.Q("g.bin")})");
        Assert.Equal("aa", h.Str("FGETS(nr)"));   // stops at lone LF
        Assert.Equal("bb", h.Str("FGETS(nr)"));   // stops at lone CR
        Assert.Equal("cc", h.Str("FGETS(nr)"));   // stops at CRLF (both consumed)
        Assert.Equal("dd", h.Str("FGETS(nr)"));   // unterminated tail
        Assert.True(h.Bool("FEOF(nr)"));
        Assert.Equal("", h.Str("FGETS(nr)"));     // past EOF → empty
        h.Run("=FCLOSE(nr)");
    }

    [Fact]
    public void FGets_Default_Limit_Is_254_Bytes()
    {
        using var h = new H();
        h.Run($"nh = FCREATE({h.Q("long.bin")})");
        h.Run("=FWRITE(nh, REPLICATE('A', 300) + CHR(13) + CHR(10) + 'next')");
        h.Run("=FCLOSE(nh)");
        h.Run($"nr = FOPEN({h.Q("long.bin")})");
        // No nBytes → default 254; the 300-char line is truncated with NO terminator consumed.
        Assert.Equal(254, h.Int("LEN(FGETS(nr))"));
        Assert.Equal(254, h.Int("FSEEK(nr, 0, 1)"));   // position stopped exactly at 254
        // The next FGETS continues from 254: remaining 46 'A's up to the CRLF.
        Assert.Equal(46, h.Int("LEN(FGETS(nr))"));
        h.Run("=FCLOSE(nr)");
    }

    // ─────────────────────────── FREAD exact-n / remaining / past-EOF ───────────────────────────

    [Fact]
    public void FRead_Returns_Exactly_N_Or_Remaining_Then_Empty_At_Eof()
    {
        using var h = new H();
        h.Run($"nh = FCREATE({h.Q("r.bin")})");
        h.Run("=FWRITE(nh, 'abcdefghij')");
        h.Run("=FCLOSE(nh)");
        h.Run($"nr = FOPEN({h.Q("r.bin")})");
        Assert.Equal("abc", h.Str("FREAD(nr, 3)"));          // exactly n
        Assert.Equal("defghij", h.Str("FREAD(nr, 100)"));    // only the remaining 7
        Assert.Equal("", h.Str("FREAD(nr, 5)"));             // past EOF → empty
        Assert.True(h.Bool("FEOF(nr)"));
        h.Run("=FCLOSE(nr)");
    }

    // ─────────────────────────── FSEEK positions + return values ───────────────────────────

    [Fact]
    public void FSeek_Returns_The_New_Absolute_Position_For_Every_Relativity()
    {
        using var h = new H();
        h.Run($"nh = FCREATE({h.Q("s.bin")})");
        h.Run("=FWRITE(nh, 'abcdefghij')");                  // 10 bytes
        Assert.Equal(10, h.Int("FSEEK(nh, 0, 2)"));          // 2 = end
        Assert.Equal(0, h.Int("FSEEK(nh, 0, 0)"));           // 0 = begin
        Assert.Equal(3, h.Int("FSEEK(nh, 3, 0)"));           // absolute
        Assert.Equal(5, h.Int("FSEEK(nh, 2, 1)"));           // 1 = current (3+2)
        Assert.Equal(8, h.Int("FSEEK(nh, -2, 2)"));          // from end (10-2)
        Assert.Equal(50, h.Int("FSEEK(nh, 50, 0)"));         // past EOF allowed → returns the offset
        h.Run("=FCLOSE(nh)");
    }

    // ─────────────────────────── FCHSIZE truncate / extend (zero-pad) ───────────────────────────

    [Fact]
    public void FChSize_Truncates_And_Extends_With_Zero_Fill_Returning_The_New_Size()
    {
        using var h = new H();
        h.Run($"nh = FCREATE({h.Q("ch.bin")})");
        h.Run("=FWRITE(nh, 'abcdefghij')");
        h.Run("=FCLOSE(nh)");
        h.Run($"nc = FOPEN({h.Q("ch.bin")}, 2)");            // 2 = read/write
        Assert.Equal(4, h.Int("FCHSIZE(nc, 4)"));            // truncate → "abcd"
        Assert.Equal(8, h.Int("FCHSIZE(nc, 8)"));            // extend → "abcd" + 4×NUL
        h.Run("=FCLOSE(nc)");
        byte[] onDisk = File.ReadAllBytes(h.Dir.File("ch.bin"));
        Assert.Equal(new byte[] { 0x61, 0x62, 0x63, 0x64, 0x00, 0x00, 0x00, 0x00 }, onDisk);
    }

    [Fact]
    public void FChSize_And_FSeek_Return_Positions_Beyond_Int32_As_Numeric()
    {
        const decimal sparseSize = 2_147_483_648m;
        using var h = new H();
        h.Run($"nh = FCREATE({h.Q("sparse.bin")})");

        VfpValue sized = h.Interp.EvalExpression("FCHSIZE(nh, 2147483648)");
        if (sized.AsNumber == -1m && h.Int("FERROR()") != 0)
            throw Xunit.Sdk.SkipException.ForSkip(
                "Filesystem cannot create a sparse file larger than 2 GiB.");

        Assert.Equal(VfpType.Numeric, sized.Type);
        Assert.Equal(sparseSize, sized.AsNumber);
        Assert.Equal(sparseSize, new FileInfo(h.Dir.File("sparse.bin")).Length);

        VfpValue atEnd = h.Interp.EvalExpression("FSEEK(nh, 0, 2)");
        Assert.Equal(VfpType.Numeric, atEnd.Type);
        Assert.Equal(sparseSize, atEnd.AsNumber);
        Assert.Equal(0, h.Int("FERROR()"));
    }

    // ─────────────────────────── FFLUSH ───────────────────────────

    [Fact]
    public void FFlush_Returns_True_For_A_Valid_Handle()
    {
        using var h = new H();
        h.Run($"nh = FCREATE({h.Q("fl.bin")})");
        h.Run("=FWRITE(nh, 'z')");
        Assert.True(h.Bool("FFLUSH(nh)"));
        h.Run("=FCLOSE(nh)");
    }

    // ─────────────────────────── invalid-handle conventions (per-function, VFP-exact) ───────────────────────────

    [Fact]
    public void Invalid_Handle_Follows_The_Per_Function_VFP_Sentinels()
    {
        using var h = new H();
        // 99 was never opened.
        Assert.Equal("", h.Str("FREAD(99, 5)"));      // FREAD  → ""
        Assert.Equal("", h.Str("FGETS(99)"));         // FGETS  → ""
        Assert.Equal(0, h.Int("FWRITE(99, 'x')"));    // FWRITE → 0
        Assert.Equal(0, h.Int("FPUTS(99, 'x')"));     // FPUTS  → 0
        Assert.Equal(0, h.Int("FSEEK(99, 0, 0)"));    // FSEEK  → 0
        Assert.False(h.Bool("FCLOSE(99)"));           // FCLOSE → .F.
        Assert.True(h.Bool("FEOF(99)"));              // FEOF   → .T. (documented bug, no error raised)
        Assert.False(h.Bool("FFLUSH(99)"));           // FFLUSH → .F.
        Assert.Equal(-1, h.Int("FCHSIZE(99, 4)"));    // FCHSIZE → -1
    }

    // ─────────────────────────── FERROR channel (DOS codes; FEOF does NOT touch it) ───────────────────────────

    [Fact]
    public void FError_Reports_The_Last_Low_Level_Code_And_Is_Reset_By_Success()
    {
        using var h = new H();
        Assert.Equal(0, h.Int("FERROR()"));                       // clean start
        Assert.Equal(-1, h.Int($"FOPEN({h.Q("missing_zz.xyz")})"));
        Assert.Equal(2, h.Int("FERROR()"));                       // 2 = file not found
        h.Run($"nh = FCREATE({h.Q("ok.bin")})");                  // a success…
        Assert.Equal(0, h.Int("FERROR()"));                       // …resets FERROR to 0
        h.Run("=FWRITE(nh, 'hello')");
        h.Run("=FCLOSE(nh)");
        h.Run($"nro = FOPEN({h.Q("ok.bin")}, 0)");                // 0 = read-only
        Assert.Equal(0, h.Int("FWRITE(nro, 'X')"));               // write to read-only handle fails
        Assert.Equal(5, h.Int("FERROR()"));                       // 5 = access denied
        h.Run("=FCLOSE(nro)");
        h.Run("=FREAD(99, 3)");                                   // invalid handle
        Assert.Equal(6, h.Int("FERROR()"));                       // 6 = invalid handle
        // FEOF on an invalid handle must NOT alter FERROR (the silent-swallow bug).
        h.Run("=FEOF(99)");
        Assert.Equal(6, h.Int("FERROR()"));
    }

    // ─────────────────────────── handles are INDEPENDENT of work areas (survive USE / USE-close) ───────────────────────────

    [Fact]
    public void Handles_Survive_Opening_And_Closing_A_Table()
    {
        using var h = new H();
        string db = MicroVfpTestSupport.CopyDatabase(
            Path.Combine(Fixtures.RepoRoot, "Tastrade_VFPData"), h.Dir);
        h.Session.OpenDatabase(Path.Combine(db, "tastrade.dbc"));
        h.Run($"nh = FCREATE({h.Q("survive.bin")})");
        h.Run("=FWRITE(nh, 'keepme')");                            // 6 bytes
        h.Run("USE customer IN 0 ALIAS cust");                     // open a work area…
        h.Run("USE IN cust");                                      // …and close it
        // The low-level handle is untouched by the table USE/close — still positioned/usable.
        Assert.Equal(6, h.Int("FSEEK(nh, 0, 2)"));
        h.Run("=FCLOSE(nh)");
    }

    // ─────────────────────────── CLOSE ALL sweeps low-level handles ───────────────────────────

    [Fact]
    public void CloseAll_Closes_Open_Low_Level_Handles()
    {
        using var h = new H();
        h.Run($"nh = FCREATE({h.Q("ca.bin")})");
        h.Run("=FWRITE(nh, 'abc')");
        h.Run("CLOSE ALL");
        // After CLOSE ALL the handle is invalid: FEOF → .T. (bug), FWRITE → 0.
        Assert.True(h.Bool("FEOF(nh)"));
        Assert.Equal(0, h.Int("FWRITE(nh, 'd')"));
    }

    // ─────────────────────────── release on interpreter/session dispose (no leaked OS handle) ───────────────────────────

    [Fact]
    public void Disposing_The_Interpreter_Releases_Handles_So_The_File_Is_Deletable()
    {
        using var dir = new MicroVfpTestSupport.TempDir("p3fileio_dispose_i");
        string path = dir.File("held.bin");
        var session = new VfpSession();
        using (var interp = new VfpInterpreter(session))
        {
            interp.Execute($"nh = FCREATE('{path}')");
            interp.Execute("=FWRITE(nh, 'held')");                 // never FCLOSE — rely on dispose
        }
        session.Dispose();
        // If the OS handle leaked, this delete would throw IOException (file in use).
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Disposing_The_Session_Releases_Handles_So_The_File_Is_Deletable()
    {
        using var dir = new MicroVfpTestSupport.TempDir("p3fileio_dispose_s");
        string path = dir.File("held.bin");
        var session = new VfpSession();
        var interp = new VfpInterpreter(session);
        interp.Execute($"nh = FCREATE('{path}')");
        interp.Execute("=FWRITE(nh, 'held')");                     // never FCLOSE
        session.Dispose();                                         // the codebase lifecycle owner
        File.Delete(path);
        Assert.False(File.Exists(path));
    }
}
