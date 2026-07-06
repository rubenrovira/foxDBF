using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests.Data;

/// <summary>
/// Phase-3 ADO.NET DataSet-bridge acceptance tests (TDD RED) for <see cref="FoxDbfDataAdapter"/> +
/// <see cref="FoxDbfCommandBuilder"/>. Each test drives the public <see cref="DbDataAdapter"/> /
/// <see cref="DbCommandBuilder"/> surface against a temp COPY of the canonical PERSON table (NEVER a
/// committed fixture — Update mutates). They fail until the bridge is implemented.
/// <para>
/// Row identity for UPDATE / DELETE is VFP's natural key <c>RECNO()</c>: an updatable adapter projects
/// <c>RECNO() AS recno</c> so the builder can target <c>WHERE RECNO()=?</c> from each row's original
/// recno value.
/// </para>
/// </summary>
public sealed class FoxDbfDataAdapterTests
{
    private const string UpdatableSelect =
        "SELECT RECNO() AS recno, id, name, city, amount, hired, active FROM person";

    // ---- (1) Fill(DataTable) matches a direct reader --------------------------------------

    [Fact]
    public void Fill_DataTable_HasSameColumnsAndRows_AsReader()
    {
        using var db = new PersonDb();
        using var conn = db.Open();

        const string sql = "SELECT id, name, city, amount, hired, active FROM person ORDER BY id";

        // Ground truth: a direct reader.
        var expected = new System.Collections.Generic.List<object?[]>();
        string[] readerNames;
        using (var rcmd = conn.CreateCommand())
        {
            rcmd.CommandText = sql;
            using var r = rcmd.ExecuteReader();
            readerNames = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
            while (r.Read())
            {
                var row = new object[r.FieldCount];
                r.GetValues(row);
                expected.Add(row);
            }
        }

        // The adapter fill.
        var select = (FoxDbfCommand)conn.CreateCommand();
        select.CommandText = sql;
        var a = new FoxDbfDataAdapter(select);

        var dt = new DataTable();
        int filled = a.Fill(dt);

        Assert.Equal(expected.Count, filled);
        Assert.Equal(expected.Count, dt.Rows.Count);

        // Same columns (names), in order.
        Assert.Equal(
            readerNames.Select(n => n.ToUpperInvariant()),
            dt.Columns.Cast<DataColumn>().Select(c => c.ColumnName.ToUpperInvariant()));

        // Same cell values, row by row.
        for (int i = 0; i < expected.Count; i++)
            for (int c = 0; c < dt.Columns.Count; c++)
                Assert.Equal(expected[i][c], dt.Rows[i][c]);
    }

    [Fact]
    public void Fill_DataSet_NamesTheTable()
    {
        using var db = new PersonDb();
        using var conn = db.Open();

        var select = (FoxDbfCommand)conn.CreateCommand();
        select.CommandText = "SELECT id, name FROM person ORDER BY id";
        var a = new FoxDbfDataAdapter(select);

        var ds = new DataSet();
        int filled = a.Fill(ds, "PERSON");

        Assert.True(filled > 0);
        var t = ds.Tables["PERSON"];
        Assert.NotNull(t);
        Assert.Equal(filled, t!.Rows.Count);
        Assert.Equal(2, t.Columns.Count);
    }

    // ---- (2) Update round-trip: insert + modify + delete ----------------------------------

    [Fact]
    public void Update_RoundTrip_PersistsInsert_Modify_Delete()
    {
        using var db = new PersonDb();
        using var conn = db.Open();

        var select = (FoxDbfCommand)conn.CreateCommand();
        select.CommandText = UpdatableSelect;
        var a = new FoxDbfDataAdapter(select);
        using var builder = new FoxDbfCommandBuilder(a);

        var dt = new DataTable();
        a.Fill(dt);

        // ---- mutate the DataTable -------------------------------------------------------
        // INSERT a brand-new person (recno is unknown for a new row; the builder omits it).
        var nr = dt.NewRow();
        nr["id"] = 11;
        nr["name"] = "Newman";
        nr["city"] = "Bonn";
        nr["amount"] = 500.00m;
        nr["hired"] = new DateTime(2010, 1, 1);
        nr["active"] = true;
        dt.Rows.Add(nr);

        // MODIFY id=1 (Smith) -> city Cologne.
        var modify = dt.Rows.Cast<DataRow>().First(rw => Convert.ToInt32(rw["id"]) == 1);
        modify["city"] = "Cologne";

        // DELETE id=10 (Evans).
        var del = dt.Rows.Cast<DataRow>().First(rw => Convert.ToInt32(rw["id"]) == 10);
        del.Delete();

        int affected = a.Update(dt);
        Assert.Equal(3, affected);

        // ---- re-read on a FRESH connection and assert the three changes -----------------
        using var conn2 = db.Open();
        using var check = conn2.CreateCommand();

        check.CommandText = "SELECT amount FROM person WHERE id = 11";
        Assert.Equal(500.00m, Convert.ToDecimal(check.ExecuteScalar()));

        check.CommandText = "SELECT city FROM person WHERE id = 1";
        Assert.Equal("Cologne", ((string)check.ExecuteScalar()!).TrimEnd());

        check.CommandText = "SELECT id FROM person WHERE id = 10";
        Assert.Null(check.ExecuteScalar()); // soft-deleted -> excluded under SET DELETED ON.
    }

    // ---- (3) CommandBuilder produces runnable SQL -----------------------------------------

    [Fact]
    public void CommandBuilder_Generates_Insert_Update_Delete_Sql()
    {
        using var db = new PersonDb();
        using var conn = db.Open();

        var select = (FoxDbfCommand)conn.CreateCommand();
        select.CommandText = UpdatableSelect;
        var a = new FoxDbfDataAdapter(select);
        using var builder = new FoxDbfCommandBuilder(a);

        var insert = builder.GetInsertCommand();
        var update = builder.GetUpdateCommand();
        var delete = builder.GetDeleteCommand();

        Assert.False(string.IsNullOrWhiteSpace(insert.CommandText));
        Assert.False(string.IsNullOrWhiteSpace(update.CommandText));
        Assert.False(string.IsNullOrWhiteSpace(delete.CommandText));

        Assert.Contains("INSERT", insert.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UPDATE", update.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DELETE", delete.CommandText, StringComparison.OrdinalIgnoreCase);

        // RECNO()-based row identity for the UPDATE / DELETE WHERE (VFP's natural key).
        Assert.Contains("RECNO()", update.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RECNO()", delete.CommandText, StringComparison.OrdinalIgnoreCase);
    }

    // ---- (4) Factory exposes the adapter + builder ----------------------------------------

    [Fact]
    public void Factory_Creates_DataAdapter_And_CommandBuilder()
    {
        var f = FoxDbfProviderFactory.Instance;

        Assert.True(f.CanCreateDataAdapter);
        Assert.True(f.CanCreateCommandBuilder);

        Assert.IsType<FoxDbfDataAdapter>(f.CreateDataAdapter());
        Assert.IsType<FoxDbfCommandBuilder>(f.CreateCommandBuilder());
    }

    // ---- (5) MUST-FIX #1: alias-qualified columns are NOT dropped from the generated DML ----

    [Fact]
    public void AliasQualified_Columns_RoundTrip_NotDropped()
    {
        using var db = new PersonDb();
        using var conn = db.Open();

        // Every data column is alias-qualified ('p.id', 'p.name' …). The builder must resolve each to
        // its physical field and keep it; the old code dropped them all (or all-but-the-bare ones),
        // emitting wrong-data SQL with NO error.
        var select = (FoxDbfCommand)conn.CreateCommand();
        select.CommandText =
            "SELECT RECNO() AS recno, p.id, p.name, p.city, p.amount, p.hired, p.active FROM person p";
        var a = new FoxDbfDataAdapter(select);
        using var builder = new FoxDbfCommandBuilder(a);

        // The generated INSERT/UPDATE must mention the PHYSICAL fields, not be silently truncated.
        Assert.Contains("id", builder.GetInsertCommand().CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name", builder.GetUpdateCommand().CommandText, StringComparison.OrdinalIgnoreCase);

        var dt = new DataTable();
        a.Fill(dt);

        var modify = dt.Rows.Cast<DataRow>().First(rw => Convert.ToInt32(rw["id"]) == 1);
        modify["name"] = "Renamed";
        modify["city"] = "Cologne";

        Assert.Equal(1, a.Update(dt));

        using var conn2 = db.Open();
        using var check = conn2.CreateCommand();
        check.CommandText = "SELECT name, city FROM person WHERE id = 1";
        using var r = check.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("Renamed", ((string)r.GetValue(0)).TrimEnd());   // would be unchanged if 'name' dropped
        Assert.Equal("Cologne", ((string)r.GetValue(1)).TrimEnd());
    }

    // ---- (6) MUST-FIX #3: a missing RECNO() identity column fails LOUDLY (no 0-row write) ---

    [Fact]
    public void UnaliasedRecno_Update_ThrowsDescriptive_NotSilentZeroRow()
    {
        using var db = new PersonDb();
        using var conn = db.Open();

        // RECNO() WITHOUT an alias: the DataTable names that column 'EXP_n', so the identity binding
        // would resolve to .NULL. and silently match no rows. The builder must throw instead.
        var select = (FoxDbfCommand)conn.CreateCommand();
        select.CommandText = "SELECT RECNO(), id, name FROM person";
        var a = new FoxDbfDataAdapter(select);
        using var builder = new FoxDbfCommandBuilder(a);

        var dt = new DataTable();
        a.Fill(dt);
        var modify = dt.Rows.Cast<DataRow>().First(rw => Convert.ToInt32(rw["id"]) == 1);
        modify["name"] = "X";

        var ex = Assert.ThrowsAny<Exception>(() => a.Update(dt));
        Assert.Contains("RECNO()", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- (7) MUST-FIX #4: a changed SelectCommand text re-derives the schema (no stale table) --

    [Fact]
    public void ChangedSelectText_RederivesSchema_NotStaleTable()
    {
        using var db = new PersonDb();
        using var conn = db.Open();

        var select = (FoxDbfCommand)conn.CreateCommand();
        select.CommandText = "SELECT RECNO() AS recno, id, name FROM person";
        var a = new FoxDbfDataAdapter(select);
        using var builder = new FoxDbfCommandBuilder(a);

        // Prime the cache against the 2-column SELECT.
        Assert.DoesNotContain("city", builder.GetInsertCommand().CommandText, StringComparison.OrdinalIgnoreCase);

        // Change the SelectCommand text: the builder must re-derive and now include 'city'.
        select.CommandText = "SELECT RECNO() AS recno, id, name, city FROM person";
        builder.RefreshSchema();
        Assert.Contains("city", builder.GetInsertCommand().CommandText, StringComparison.OrdinalIgnoreCase);
    }

    // ---- (8) MUST-FIX #2: memo columns are excluded from the generated DML + round-trip ------

    [Fact]
    public void MemoColumn_Excluded_And_UpdatePreservesMemo()
    {
        using var dir = new SqlTestSupportProxy.TempDir();
        string dbf = Path.Combine(dir.Path, "notes.dbf");
        using (var w = DbfWriter.Create(dbf, new[]
        {
            new DbfColumnDef("ID", 'I', 4),
            new DbfColumnDef("TITLE", 'C', 20),
            new DbfColumnDef("BODY", 'M', 4),   // FPT-backed memo
        }, new DbfCreateOptions { Overwrite = true }))
        {
            w.AppendRecord(1, "first", "memo-one");
            w.AppendRecord(2, "second", "memo-two");
            w.Flush();
        }

        using var conn = new FoxDbfConnection($"Data Source={dir.Path}");
        conn.Open();

        var select = (FoxDbfCommand)conn.CreateCommand();
        select.CommandText = "SELECT RECNO() AS recno, id, title, body FROM notes";
        var a = new FoxDbfDataAdapter(select);
        using var builder = new FoxDbfCommandBuilder(a);

        // The memo column must be omitted from INSERT and UPDATE (byte/FPT hazard).
        Assert.DoesNotContain("body", builder.GetInsertCommand().CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("body", builder.GetUpdateCommand().CommandText, StringComparison.OrdinalIgnoreCase);

        var dt = new DataTable();
        a.Fill(dt);
        var modify = dt.Rows.Cast<DataRow>().First(rw => Convert.ToInt32(rw["id"]) == 1);
        modify["title"] = "changed";  // change a NON-memo column

        Assert.Equal(1, a.Update(dt));  // must not throw on the memo field

        // The memo content is preserved (UPDATE omits BODY → DmlExecutor keeps the existing .fpt block).
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT title, body FROM notes WHERE id = 1";
        using var r = check.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("changed", ((string)r.GetValue(0)).TrimEnd());
        Assert.Equal("memo-one", (string)r.GetValue(1));
    }
}

/// <summary>Reuses the SQL test harness's throwaway temp directory for the Data tests.</summary>
internal sealed class SqlTestSupportProxy
{
    internal sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foxdbf_data_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch { /* best-effort */ }
        }
    }
}
