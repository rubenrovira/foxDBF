using System;
using System.IO;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// <see cref="DbfColumnDef"/> fills in the canonical descriptor length for fixed-width field types
/// when the caller passes 0 (so <c>new DbfColumnDef("id", 'I')</c> needs no width, like VFP
/// <c>CREATE TABLE t (id I)</c>). Variable-width types still require an explicit length.
/// </summary>
public sealed class ColumnDefDefaultsTests
{
    [Theory]
    [InlineData('I', 4)]
    [InlineData('+', 4)]
    [InlineData('Y', 8)]
    [InlineData('B', 8)]
    [InlineData('D', 8)]
    [InlineData('T', 8)]
    [InlineData('L', 1)]
    [InlineData('M', 4)]
    [InlineData('G', 4)]
    [InlineData('P', 4)]
    [InlineData('W', 4)]
    public void FixedWidthType_NoLength_GetsCanonicalWidth(char type, int expected)
        => Assert.Equal(expected, new DbfColumnDef("F", type).Length);

    [Theory]
    [InlineData('C')]
    [InlineData('N')]
    [InlineData('F')]
    [InlineData('V')]
    public void VariableWidthType_NoLength_StaysZero(char type)
        => Assert.Equal(0, new DbfColumnDef("F", type).Length);

    [Fact]
    public void ExplicitLength_IsNotOverridden()
    {
        Assert.Equal(30, new DbfColumnDef("NAME", 'C', 30).Length);
        Assert.Equal(10, new DbfColumnDef("AMT", 'N', 10, 2).Length);
    }

    [Fact]
    public void Create_WithBareFixedWidthColumns_Succeeds_AndRoundTrips()
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_coldef_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dbf = Path.Combine(dir, "t.dbf");
        try
        {
            using (var w = DbfWriter.Create(dbf, new[]
            {
                new DbfColumnDef("ID", 'I'),       // no length → 4
                new DbfColumnDef("WHEN", 'T'),     // no length → 8
                new DbfColumnDef("OK", 'L'),       // no length → 1
                new DbfColumnDef("NAME", 'C', 20),
            }, new DbfCreateOptions { Overwrite = true }))
            {
                w.AppendRecord(new object?[] { 7, new DateTime(2024, 3, 14, 10, 30, 0), true, "Ada" });
            }
            using var t = DbfTable.Open(dbf);
            Assert.Equal(4, t.Columns[0].Length);
            Assert.Equal(8, t.Columns[1].Length);
            Assert.Equal(1, t.Columns[2].Length);
            var r = t.GetRecord(0);
            Assert.Equal(7, Convert.ToInt32(r?["ID"]));
            Assert.Equal("Ada", (r?["NAME"] as string)?.TrimEnd());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Create_WithBareCharacterColumn_StillRejected()
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_coldef_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // A Character column with no length is still invalid (length 0).
            Assert.ThrowsAny<Exception>(() =>
            {
                using var w = DbfWriter.Create(Path.Combine(dir, "bad.dbf"),
                    new[] { new DbfColumnDef("C", 'C') }, new DbfCreateOptions { Overwrite = true });
            });
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
