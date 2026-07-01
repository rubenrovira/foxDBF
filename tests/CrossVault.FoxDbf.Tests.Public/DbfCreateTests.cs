using System.Buffers.Binary;
using System.Linq;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Behavioral spec for the §D4 CREATE-from-scratch surface
/// (<see cref="DbfWriter.Create(string, System.Collections.Generic.IEnumerable{DbfColumnDef}, DbfCreateOptions)"/>):
/// build a valid EMPTY VFP table (≥ VFP6), choose the version byte by feature, auto-add the
/// hidden <c>_NullFlags</c> system column, write the code-page / table-flags / geometry bytes,
/// create the companion <c>.fpt</c> for a memo column, then reopen and round-trip an appended row.
///
/// SAFETY: every test writes ONLY into a throwaway temp dir (deleted in a finally). No fixture
/// under data/ or vfp_test/ is ever created or written.
///
/// Expected values are HARDCODED, derived from the documented VFP layout (plan §A4/§A5b/§D4):
///   Mixed table cols (physical order, all lengths in bytes):
///     CNAME C20, NUM N10.2, INTF I4, CUR Y8, DBL B8, DT D8, TS T8, FLG L1,
///     NN N12.2 (nullable), VC V30 (varchar), MEMO M4, + hidden _NullFlags '0' len1.
///   ⇒ version 0x32 (varchar present); 12 physical descriptors;
///     HeaderLength = 32 + 12*32 + 1 + 263 = 680;
///     RecordLength = 1 + (20+10+4+8+8+8+8+1+12+30+4+1) = 1 + 114 = 115;
///     _NullFlags width = ceil((1 nullable + 1 varlen)/8) = 1 byte;
///     table-flags byte 28 = 0x02 (memo); code-page byte 29 = 0x03 (CP1252); RecordCount 0.
///   Bitmap (LSB-first, physical order): NN → null bit 0, VC → varlen bit 1, so an appended
///   row with NN = null and VC = "Hi" yields a _NullFlags byte of 0x03.
/// </summary>
public sealed class DbfCreateTests
{
    // ---- temp plumbing (never touch committed fixtures) ------------------------

    private static string FreshTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_create_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    /// <summary>The mixed-feature column set pinned by the spec above (11 user columns).</summary>
    private static DbfColumnDef[] MixedColumns() => new[]
    {
        new DbfColumnDef("CNAME", 'C', 20),
        new DbfColumnDef("NUM",   'N', 10, 2),
        new DbfColumnDef("INTF",  'I', 4),
        new DbfColumnDef("CUR",   'Y', 8),
        new DbfColumnDef("DBL",   'B', 8),
        new DbfColumnDef("DT",    'D', 8),
        new DbfColumnDef("TS",    'T', 8),
        new DbfColumnDef("FLG",   'L', 1),
        new DbfColumnDef("NN",    'N', 12, 2, nullable: true),
        new DbfColumnDef("VC",    'V', 30),
        new DbfColumnDef("MEMO",  'M', 4),
    };

    // ---- (1) the empty mixed table is structurally valid -----------------------

    [Fact]
    public void Create_mixed_table_writes_valid_empty_vfp32_structure()
    {
        string dir = FreshTempDir();
        string dbf = Path.Combine(dir, "mixed.dbf");
        try
        {
            using (var w = DbfWriter.Create(dbf, MixedColumns(), new DbfCreateOptions()))
            {
                Assert.Equal(0, w.RecordCount);
            }

            // ---- raw header bytes (version / table-flags / code-page / geometry / count) ----
            byte[] raw = File.ReadAllBytes(dbf);
            Assert.Equal(0x32, raw[0]);                                            // varchar ⇒ 0x32
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4)));   // RecordCount 0
            Assert.Equal(680, BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(8)));  // HeaderLength
            Assert.Equal(115, BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(10))); // RecordLength
            Assert.Equal(0x02, raw[28]);                                           // memo table-flag bit
            Assert.Equal(0x03, raw[29]);                                           // CP1252 code page
            Assert.Equal(0x0D, raw[32 + 12 * 32]);                                 // descriptor terminator
            // 263-byte backlink (free table) is all-zero, and the empty VFP table carries 0x1A EOF.
            Assert.Equal(680 + 1, raw.Length);                                     // header + 0x1A
            Assert.Equal(0x1A, raw[680]);
            for (int i = 32 + 12 * 32 + 1; i < 680; i++)
                Assert.Equal(0x00, raw[i]);                                        // backlink zeroed

            // ---- the reader agrees on columns, flags, geometry and the hidden _NullFlags ----
            using var shown = DbfTable.Open(dbf, new DbfOptions { ExposeSystemColumns = true });
            Assert.Equal(0x32, shown.Version.Code);
            Assert.Equal(115, shown.RecordLength);
            Assert.Equal(0, shown.RecordCount);
            Assert.Equal(12, shown.Columns.Count);                                 // 11 user + _NullFlags

            DbfColumn Col(string n) => shown.Columns.Single(c => c.Name == n);

            Assert.Equal(('C', 20, 0), (Col("CNAME").Type, Col("CNAME").Length, Col("CNAME").Decimal));
            Assert.Equal(('N', 10, 2), (Col("NUM").Type, Col("NUM").Length, Col("NUM").Decimal));
            Assert.Equal(('I', 4), (Col("INTF").Type, Col("INTF").Length));
            Assert.Equal(('Y', 8), (Col("CUR").Type, Col("CUR").Length));
            Assert.Equal(('B', 8), (Col("DBL").Type, Col("DBL").Length));
            Assert.Equal(('D', 8), (Col("DT").Type, Col("DT").Length));
            Assert.Equal(('T', 8), (Col("TS").Type, Col("TS").Length));
            Assert.Equal(('L', 1), (Col("FLG").Type, Col("FLG").Length));
            Assert.Equal(('V', 30), (Col("VC").Type, Col("VC").Length));
            Assert.Equal(('M', 4), (Col("MEMO").Type, Col("MEMO").Length));

            // NN is nullable (flag 0x02), the others not.
            Assert.True(Col("NN").IsNullable);
            Assert.False(Col("CNAME").IsNullable);
            Assert.False(Col("VC").IsNullable);

            // The auto-added hidden system column.
            var nf = Col("_NullFlags");
            Assert.Equal('0', nf.Type);          // type byte 0x30
            Assert.Equal(1, nf.Length);          // ceil((1 nullable + 1 varlen)/8) == 1
            Assert.True(nf.IsSystem);
            Assert.Same(nf, shown.Columns[^1]);  // last physical column

            // Hidden by default (not exposed).
            using var hidden = DbfTable.Open(dbf, new DbfOptions { ExposeSystemColumns = false });
            Assert.Equal(11, hidden.Columns.Count);
            Assert.DoesNotContain(hidden.Columns, c => c.Name == "_NullFlags");

            // ---- companion .fpt exists with a valid FoxPro header --------------
            string fpt = Path.ChangeExtension(dbf, ".fpt");
            Assert.True(File.Exists(fpt));
            byte[] fh = File.ReadAllBytes(fpt);
            // VFP reserves a full 512-byte FPT header (8 blocks at the 64-byte default); memo
            // blocks start at byte 512 and next-free points there. A shorter header is rejected
            // by Visual FoxPro ("memo file is invalid") — verified against the real VFP9 runtime.
            Assert.Equal(512, fh.Length);
            Assert.Equal(64, BinaryPrimitives.ReadUInt16BigEndian(fh.AsSpan(6)));  // block size
            Assert.Equal(8u, BinaryPrimitives.ReadUInt32BigEndian(fh.AsSpan(0)));  // next-free block (= 512 / 64)
        }
        finally { Cleanup(dir); }
    }

    // ---- (2) append round-trips null + short varchar + all typed values --------

    [Fact]
    public void Create_then_append_roundtrips_null_varchar_and_typed_values()
    {
        string dir = FreshTempDir();
        string dbf = Path.Combine(dir, "rt.dbf");
        try
        {
            var dt = new DateOnly(2024, 6, 15);
            var ts = new DateTime(2024, 6, 15, 10, 30, 0);

            using (var w = DbfWriter.Create(dbf, MixedColumns(), new DbfCreateOptions()))
            {
                int idx = w.AppendRecord(
                    "Hello",          // CNAME
                    123.45m,          // NUM
                    42,               // INTF
                    12.34m,           // CUR (Y)
                    3.14159d,         // DBL (B)
                    dt,               // DT
                    ts,               // TS
                    true,             // FLG
                    null,             // NN  → null bit
                    "Hi",             // VC  → short varchar (varlen bit)
                    null);            // MEMO
                Assert.Equal(0, idx);
                Assert.Equal(1, w.RecordCount);
            }

            // Raw _NullFlags byte: NN null bit (0) + VC varlen bit (1) ⇒ 0x03.
            using (var exposed = DbfTable.Open(dbf, new DbfOptions { ExposeSystemColumns = true }))
            {
                var nf = exposed.Columns.Single(c => c.Name == "_NullFlags");
                Assert.Equal((byte)0x03, exposed.GetRecord(0)!.Value.GetRawField(nf)[0]);
            }

            using var t = DbfTable.Open(dbf, new DbfOptions());
            Assert.Equal(1, t.RecordCount);
            var r = t.GetRecord(0)!.Value;

            Assert.Equal("Hello", r.GetString("CNAME"));
            Assert.Equal(123.45m, r.GetDecimal("NUM"));
            Assert.Equal(42, r.GetInt32("INTF"));
            Assert.Equal(12.34m, r.GetDecimal("CUR"));
            Assert.Equal(3.14159d, r.GetDouble("DBL")!.Value, 5);
            Assert.Equal(dt, r.GetDateOnly("DT"));
            Assert.Equal(ts, r.GetDateTime("TS"));
            Assert.Equal(true, r.GetBoolean("FLG"));
            Assert.Null(r["NN"]);                       // null bit applied
            Assert.Equal("Hi", r.GetString("VC"));      // short varchar round-trip
        }
        finally { Cleanup(dir); }
    }

    // ---- (3) version selection: AutoIncrement-only ⇒ 0x31 ----------------------

    [Fact]
    public void Create_autoincrement_only_table_selects_version_0x31_and_has_no_nullflags()
    {
        string dir = FreshTempDir();
        string dbf = Path.Combine(dir, "ai.dbf");
        try
        {
            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4, autoIncrement: new DbfAutoIncrement(1, 1)),
                new DbfColumnDef("LABEL", 'C', 8),
            };
            using (DbfWriter.Create(dbf, cols, new DbfCreateOptions())) { }

            byte[] raw = File.ReadAllBytes(dbf);
            Assert.Equal(0x31, raw[0]);                 // autoinc (no varchar) ⇒ 0x31
            Assert.Equal(0x00, raw[28]);                // no memo flag

            // Byte-verify the AutoIncrement seed lands at the exact descriptor offsets: ID is the
            // first (and only AutoInc) column, so its 32-byte descriptor starts at offset 32, with
            // Next (u32 LE) @ +19 and Step @ +23. A transposed write offset would leave IsAutoIncrement
            // (flag bit 0x08) correct yet the seed wrong — only this raw check catches it.
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(32 + 19, 4))); // Next
            Assert.Equal(1, raw[32 + 23]);                                                     // Step

            using var shown = DbfTable.Open(dbf, new DbfOptions { ExposeSystemColumns = true });
            Assert.Equal(0x31, shown.Version.Code);
            Assert.DoesNotContain(shown.Columns, c => c.Name == "_NullFlags"); // none needed
            var id = shown.Columns.Single(c => c.Name == "ID");
            Assert.True(id.IsAutoIncrement);            // descriptor flag 0x08
            Assert.Equal(1 + 4 + 8, shown.RecordLength);
        }
        finally { Cleanup(dir); }
    }

    // ---- (4) version selection: plain table ⇒ 0x30, no _NullFlags --------------

    [Fact]
    public void Create_plain_table_selects_version_0x30_and_has_no_nullflags()
    {
        string dir = FreshTempDir();
        string dbf = Path.Combine(dir, "plain.dbf");
        try
        {
            var cols = new[]
            {
                new DbfColumnDef("CODE", 'C', 10),
                new DbfColumnDef("QTY",  'N', 5),
            };
            using (DbfWriter.Create(dbf, cols, new DbfCreateOptions())) { }

            byte[] raw = File.ReadAllBytes(dbf);
            Assert.Equal(0x30, raw[0]);                 // no autoinc, no varchar ⇒ 0x30 base
            Assert.Equal(0x00, raw[28]);                // no memo

            using var shown = DbfTable.Open(dbf, new DbfOptions { ExposeSystemColumns = true });
            Assert.Equal(0x30, shown.Version.Code);
            Assert.DoesNotContain(shown.Columns, c => c.Name == "_NullFlags");
            Assert.Equal(2, shown.Columns.Count);
            Assert.Equal(1 + 10 + 5, shown.RecordLength);
        }
        finally { Cleanup(dir); }
    }

    // ---- (5) a single nullable column STILL forces a _NullFlags column ----------

    [Fact]
    public void Create_with_one_nullable_column_adds_nullflags_even_on_version_0x30()
    {
        string dir = FreshTempDir();
        string dbf = Path.Combine(dir, "nn.dbf");
        try
        {
            var cols = new[]
            {
                new DbfColumnDef("A", 'C', 4),
                new DbfColumnDef("B", 'N', 6, 0, nullable: true),
            };
            using (DbfWriter.Create(dbf, cols, new DbfCreateOptions())) { }

            byte[] raw = File.ReadAllBytes(dbf);
            Assert.Equal(0x30, raw[0]);                 // nullable alone does NOT raise the version

            using var shown = DbfTable.Open(dbf, new DbfOptions { ExposeSystemColumns = true });
            var nf = shown.Columns.Single(c => c.Name == "_NullFlags");
            Assert.Equal('0', nf.Type);
            Assert.Equal(1, nf.Length);                 // ceil(1/8) == 1
            Assert.Equal(1 + 4 + 6 + 1, shown.RecordLength);
        }
        finally { Cleanup(dir); }
    }

    // ---- (6) custom code page is honoured --------------------------------------

    [Fact]
    public void Create_honours_custom_codepage_byte()
    {
        string dir = FreshTempDir();
        string dbf = Path.Combine(dir, "cp.dbf");
        try
        {
            using (DbfWriter.Create(dbf, new[] { new DbfColumnDef("X", 'C', 3) },
                       new DbfCreateOptions { CodePage = 0x26 })) { } // Russian OEM (CP866)

            byte[] raw = File.ReadAllBytes(dbf);
            Assert.Equal(0x26, raw[29]);
        }
        finally { Cleanup(dir); }
    }

    // ---- (7) adversarial schema rejection (typed exception) --------------------

    [Fact]
    public void Create_rejects_empty_column_name()
    {
        string dir = FreshTempDir();
        string dbf = Path.Combine(dir, "bad.dbf");
        try
        {
            Assert.Throws<DbfSchemaException>(() =>
                DbfWriter.Create(dbf, new[] { new DbfColumnDef("", 'C', 4) }, new DbfCreateOptions()));
            Assert.False(File.Exists(dbf)); // rejected before any byte is written
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Create_rejects_duplicate_column_names_case_insensitively()
    {
        string dir = FreshTempDir();
        string dbf = Path.Combine(dir, "dup.dbf");
        try
        {
            var cols = new[]
            {
                new DbfColumnDef("DUP", 'C', 4),
                new DbfColumnDef("dup", 'N', 3),
            };
            Assert.Throws<DbfSchemaException>(() => DbfWriter.Create(dbf, cols, new DbfCreateOptions()));
        }
        finally { Cleanup(dir); }
    }

    [Theory]
    [InlineData(0)]    // zero length
    [InlineData(-1)]   // negative length
    [InlineData(300)]  // > 255 (the descriptor length byte cannot hold it)
    public void Create_rejects_bad_field_length(int length)
    {
        string dir = FreshTempDir();
        string dbf = Path.Combine(dir, "len.dbf");
        try
        {
            Assert.Throws<DbfSchemaException>(() =>
                DbfWriter.Create(dbf, new[] { new DbfColumnDef("F", 'C', length) }, new DbfCreateOptions()));
        }
        finally { Cleanup(dir); }
    }

    [Theory]
    [InlineData('I', 3)]   // Integer must be exactly 4
    [InlineData('I', 8)]
    [InlineData('Y', 4)]   // Currency must be exactly 8
    [InlineData('B', 4)]   // Double must be exactly 8
    [InlineData('D', 4)]   // Date must be exactly 8
    [InlineData('T', 5)]   // DateTime must be exactly 8
    [InlineData('L', 2)]   // Logical must be exactly 1
    [InlineData('M', 8)]   // Memo pointer must be exactly 4
    [InlineData('G', 2)]   // General/OLE pointer must be exactly 4
    [InlineData('P', 8)]   // Picture pointer must be exactly 4
    [InlineData('N', 25)]  // Numeric must be 1–20
    [InlineData('F', 30)]  // Float must be 1–20
    public void Create_rejects_type_specific_bad_length(char type, int length)
    {
        string dir = FreshTempDir();
        string dbf = Path.Combine(dir, "badlen.dbf");
        try
        {
            Assert.Throws<DbfSchemaException>(() =>
                DbfWriter.Create(dbf, new[] { new DbfColumnDef("F", type, length) }, new DbfCreateOptions()));
            Assert.False(File.Exists(dbf)); // rejected before any byte is written
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Create_rejects_more_than_255_columns()
    {
        string dir = FreshTempDir();
        string dbf = Path.Combine(dir, "many.dbf");
        try
        {
            var cols = Enumerable.Range(0, 256)
                .Select(i => new DbfColumnDef("F" + i, 'C', 1))
                .ToArray();
            Assert.Throws<DbfSchemaException>(() => DbfWriter.Create(dbf, cols, new DbfCreateOptions()));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Create_rejects_empty_column_set()
    {
        string dir = FreshTempDir();
        string dbf = Path.Combine(dir, "none.dbf");
        try
        {
            Assert.Throws<DbfSchemaException>(() =>
                DbfWriter.Create(dbf, Array.Empty<DbfColumnDef>(), new DbfCreateOptions()));
        }
        finally { Cleanup(dir); }
    }

    // ---- (8) the reserved _NullFlags name is rejected (any casing) --------------

    [Theory]
    [InlineData("_NullFlags")]
    [InlineData("_nullflags")]
    [InlineData("_NULLFLAGS")]
    public void Create_rejects_reserved_nullflags_column_name(string reserved)
    {
        string dir = FreshTempDir();
        string dbf = Path.Combine(dir, "reserved.dbf");
        try
        {
            // A user column literally named _NullFlags would collide with the hidden system column
            // the writer auto-adds whenever a nullable/varlen column is present — reject it before
            // any byte is written.
            var cols = new[]
            {
                new DbfColumnDef(reserved, 'C', 4),
                new DbfColumnDef("N", 'N', 6, 0, nullable: true), // forces a real _NullFlags column
            };
            Assert.Throws<DbfSchemaException>(() => DbfWriter.Create(dbf, cols, new DbfCreateOptions()));
            Assert.False(File.Exists(dbf)); // rejected before disk is touched
        }
        finally { Cleanup(dir); }
    }

    // ---- (9) Create does not clobber an existing file unless Overwrite is set ----

    [Fact]
    public void Create_refuses_to_clobber_existing_file_and_leaves_it_intact()
    {
        string dir = FreshTempDir();
        string dbf = Path.Combine(dir, "exists.dbf");
        try
        {
            byte[] sentinel = new byte[] { 1, 2, 3, 4, 5 };
            File.WriteAllBytes(dbf, sentinel);

            Assert.Throws<DbfWriteException>(() =>
                DbfWriter.Create(dbf, new[] { new DbfColumnDef("X", 'C', 3) }, new DbfCreateOptions()));

            // The pre-existing file is byte-identical — the refused create never touched it.
            Assert.Equal(sentinel, File.ReadAllBytes(dbf));

            // With Overwrite the create succeeds and replaces it with a valid table.
            using (DbfWriter.Create(dbf, new[] { new DbfColumnDef("X", 'C', 3) },
                       new DbfCreateOptions { Overwrite = true })) { }
            byte[] raw = File.ReadAllBytes(dbf);
            Assert.Equal(0x30, raw[0]);
            using var t = DbfTable.Open(dbf, new DbfOptions());
            Assert.Single(t.Columns);
        }
        finally { Cleanup(dir); }
    }

    // ---- (10) a 'G' (General/OLE) column reopens with a usable .fpt sidecar ------

    [Fact]
    public void Create_general_column_reopens_with_usable_fpt_sidecar()
    {
        string dir = FreshTempDir();
        string dbf = Path.Combine(dir, "gen.dbf");
        try
        {
            // 'G' creates a .fpt at CREATE time; the reopen guard must also open it so a later
            // append does not fault on a (wrongly) null sidecar despite the .fpt existing.
            using (var w = DbfWriter.Create(dbf, new[]
            {
                new DbfColumnDef("CODE", 'C', 4),
                new DbfColumnDef("PIC",  'G', 4),
            }, new DbfCreateOptions()))
            {
                // A General field is a 4-byte .fpt pointer; appending a plain row (null General)
                // must not throw, and the writer must hold an open sidecar.
                int idx = w.AppendRecord("ABCD", null);
                Assert.Equal(0, idx);
            }

            Assert.True(File.Exists(Path.ChangeExtension(dbf, ".fpt")));
            using var t = DbfTable.Open(dbf, new DbfOptions());
            Assert.Equal(1, t.RecordCount);
            Assert.Equal("ABCD", t.GetRecord(0)!.Value.GetString("CODE"));
        }
        finally { Cleanup(dir); }
    }
}
