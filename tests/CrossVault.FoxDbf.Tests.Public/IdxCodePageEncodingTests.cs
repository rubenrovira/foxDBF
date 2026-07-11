using System;
using System.IO;
using System.Linq;
using System.Text;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>Standalone character IDX keys use the table/code-field byte encoding end to end.</summary>
public sealed class IdxCodePageEncodingTests : IDisposable
{
    private readonly SqlTestSupport.TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private (string Dbf, string Idx) Build(
        string stem,
        byte codePage,
        Encoding encoding,
        string[] values,
        bool binary = false,
        bool unique = false,
        int fieldLength = 1,
        string keyExpression = "NAME")
    {
        string dbf = _dir.File(stem + ".dbf");
        string idx = _dir.File(stem + ".idx");
        using var writer = DbfWriter.Create(dbf,
            new[] { new DbfColumnDef("NAME", 'C', fieldLength, binary: binary) },
            new DbfCreateOptions
            {
                CodePage = codePage,
                Encoding = encoding,
                Overwrite = true,
            });
        foreach (string value in values)
            writer.AppendRecord(value);
        writer.CreateStandaloneIdx(idx, keyExpression, unique: unique);
        return (dbf, idx);
    }

    private static (byte[][] Keys, uint[] Recnos) ReadEntries(string idxPath)
    {
        using var idx = IdxFile.Open(idxPath);
        var entries = idx.EnumerateEntries().ToArray();
        return (entries.Select(e => e.Key).ToArray(), entries.Select(e => e.RecordNumber).ToArray());
    }

    [Fact]
    public void Cp1252_UsesExactBytesAndUniqueDoesNotCollapseDistinctCharacters()
    {
        Encoding cp1252 = Encoding.GetEncoding(1252);
        var (_, idx) = Build("cp1252", 0x03, cp1252,
            new[] { "?", "€", "Š", "–", "é", "ÿ" }, unique: true);

        var (keys, recnos) = ReadEntries(idx);
        Assert.Equal(new byte[] { 0x3F, 0x80, 0x8A, 0x96, 0xE9, 0xFF },
            keys.Select(key => Assert.Single(key)).ToArray());
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6 }, recnos);
    }

    [Fact]
    public void Cp850_UsesExactBytesAndByteOrder()
    {
        Encoding cp850 = Encoding.GetEncoding(850);
        var (_, idx) = Build("cp850", 0x02, cp850,
            new[] { "?", "é", "ä", "ÿ", "Ö" });

        var (keys, recnos) = ReadEntries(idx);
        Assert.Equal(new byte[] { 0x3F, 0x82, 0x84, 0x98, 0x99 },
            keys.Select(key => Assert.Single(key)).ToArray());
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5 }, recnos);
    }

    [Fact]
    public void DirectNoCpTransField_UsesLatin1ByteIdentity()
    {
        var (_, idx) = Build("binary", 0x03, Encoding.GetEncoding(1252),
            new[] { "\u0080" }, binary: true);

        var (keys, recnos) = ReadEntries(idx);
        Assert.Equal(0x80, Assert.Single(Assert.Single(keys)));
        Assert.Equal((uint)1, Assert.Single(recnos));
    }

    [Fact]
    public void UnrepresentableCharacter_UsesEncodingReplacementByte()
    {
        var (_, idx) = Build("replacement", 0x02, Encoding.GetEncoding(850),
            new[] { "€" });

        var (keys, _) = ReadEntries(idx);
        Assert.Equal(0x3F, Assert.Single(Assert.Single(keys)));
    }

    [Fact]
    public void CharacterExpression_TruncatesEncodedDbcsBytesToKeyLength()
    {
        Encoding cp932 = Encoding.GetEncoding(932);
        var (_, idx) = Build("dbcs", 0x13, cp932,
            new[] { "あ" }, fieldLength: 2, keyExpression: "LEFT(NAME, 1)");

        var (keys, _) = ReadEntries(idx);
        Assert.Equal(0x82, Assert.Single(Assert.Single(keys)));
    }

    [Fact]
    public void CharacterExpression_DefaultsEvaluationAndStorageToTableEncoding()
    {
        var (_, idx) = Build("expr1252", 0x03, Encoding.GetEncoding(1252),
            new[] { "A" }, keyExpression: "CHR(128)");

        var (keys, _) = ReadEntries(idx);
        Assert.Equal(0x80, Assert.Single(Assert.Single(keys)));
    }

    [Fact]
    public void Cp850Expression_UsesTableEncodingDespiteAmbientInterpreterContext()
    {
        Build("expr850", 0x02, Encoding.GetEncoding(850), new[] { "A" });
        using var session = new VfpSession();
        session.OpenDirectory(_dir.Path);
        var interpreter = new VfpInterpreter(session);

        interpreter.Execute("USE expr850\nINDEX ON CHR(130) TO expr850_chr");

        var (keys, _) = ReadEntries(_dir.File("expr850_chr.idx"));
        Assert.Equal(0x82, Assert.Single(Assert.Single(keys)));
    }

    [Fact]
    public void SeekAndSetKey_UseTheSameEncodingAsCp1252AndCp850IdxKeys()
    {
        AssertSeekAndSetKey("nav1252", 0x03, Encoding.GetEncoding(1252), "€");
        AssertSeekAndSetKey("nav850", 0x02, Encoding.GetEncoding(850), "é");
    }

    private void AssertSeekAndSetKey(string stem, byte codePage, Encoding encoding, string needle)
    {
        Build(stem, codePage, encoding, new[] { "?", needle, "ÿ" });
        using var session = new VfpSession();
        session.OpenDirectory(_dir.Path);
        var interpreter = new VfpInterpreter(session);
        interpreter.Execute($"USE {stem} INDEX {stem}.idx\nSET ORDER TO {stem}");

        interpreter.Execute($"=SEEK('{needle}', '{stem}')");
        Assert.True(interpreter.EvalExpression("FOUND()").AsLogical);
        Assert.Equal(2m, interpreter.EvalExpression("RECNO()").AsNumber);

        interpreter.Execute($"SET KEY TO '{needle}'\nGO TOP");
        Assert.False(interpreter.EvalExpression("EOF()").AsLogical);
        Assert.Equal(2m, interpreter.EvalExpression("RECNO()").AsNumber);
        interpreter.Execute("SKIP");
        Assert.True(interpreter.EvalExpression("EOF()").AsLogical);
    }
}
