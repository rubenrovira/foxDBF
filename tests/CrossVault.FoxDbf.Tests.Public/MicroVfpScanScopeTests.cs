using System.IO;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpScanScopeTests
{
    private static VfpInterpreter OpenNumbers(MicroVfpTestSupport.TempDir dir, out VfpSession session)
    {
        string dbf = Path.Combine(dir.Path, "numbers.dbf");
        using (var writer = DbfWriter.Create(dbf, new[] { new DbfColumnDef("id", 'N', 3) }))
        {
            for (int id = 1; id <= 5; id++) writer.AppendRecord(id);
            writer.Flush();
        }

        session = new VfpSession();
        session.OpenDirectory(dir.Path);
        var interpreter = new VfpInterpreter(session);
        interpreter.Execute("USE numbers");
        return interpreter;
    }

    private static decimal Run(VfpInterpreter interpreter, string scan)
    {
        interpreter.Execute($"seen = 0\n{scan}");
        return interpreter.EvalExpression("seen").AsNumber;
    }

    [Theory]
    [InlineData("GO 3\nSCAN REST\n  seen = seen * 10 + id\nENDSCAN", 345)]
    [InlineData("GO 3\nSCAN NEXT 2\n  seen = seen * 10 + id\nENDSCAN", 34)]
    public void Scan_ExplicitScope_StartsAtCurrentRecord(string scan, int expected)
    {
        using var dir = new MicroVfpTestSupport.TempDir("scan_scope");
        var interpreter = OpenNumbers(dir, out var session);
        using (session)
            Assert.Equal(expected, Run(interpreter, scan));
    }

    [Fact]
    public void Scan_WithoutScope_DefaultsToAll()
    {
        using var dir = new MicroVfpTestSupport.TempDir("scan_all");
        var interpreter = OpenNumbers(dir, out var session);
        using (session)
            Assert.Equal(12345m, Run(interpreter,
                "GO 3\nSCAN\n  seen = seen * 10 + id\nENDSCAN"));
    }

    [Theory]
    [InlineData("SCAN WHILE id >= 3")]
    [InlineData("SCAN REST WHILE id >= 3")]
    public void Scan_WhileFromCurrent_UsesRestScope(string command)
    {
        using var dir = new MicroVfpTestSupport.TempDir("scan_while");
        var interpreter = OpenNumbers(dir, out var session);
        using (session)
            Assert.Equal(345m, Run(interpreter,
                $"GO 3\n{command}\n  seen = seen * 10 + id\nENDSCAN"));
    }

    [Fact]
    public void Scan_NextScope_CountsVisitedRecords_NotForMatches()
    {
        using var dir = new MicroVfpTestSupport.TempDir("scan_next_for");
        var interpreter = OpenNumbers(dir, out var session);
        using (session)
            Assert.Equal(3m, Run(interpreter,
                "GO 2\nSCAN NEXT 2 FOR id >= 3\n  seen = seen * 10 + id\nENDSCAN"));
    }

    [Fact]
    public void Scan_RecordScope_VisitsOnlyRequestedRecord()
    {
        using var dir = new MicroVfpTestSupport.TempDir("scan_record");
        var interpreter = OpenNumbers(dir, out var session);
        using (session)
            Assert.Equal(4m, Run(interpreter,
                "GO 1\nSCAN RECORD 4\n  seen = seen * 10 + id\nENDSCAN"));
    }

    [Fact]
    public void Scan_NextScope_ConsumesLoopIterations()
    {
        using var dir = new MicroVfpTestSupport.TempDir("scan_loop");
        var interpreter = OpenNumbers(dir, out var session);
        using (session)
            Assert.Equal(23m, Run(interpreter,
                "GO 2\nSCAN NEXT 2\n  seen = seen * 10 + id\n  LOOP\nENDSCAN"));
    }

    [Fact]
    public void Scan_Exit_StopsBeforeRemainingScope()
    {
        using var dir = new MicroVfpTestSupport.TempDir("scan_exit");
        var interpreter = OpenNumbers(dir, out var session);
        using (session)
            Assert.Equal(2m, Run(interpreter,
                "GO 2\nSCAN NEXT 4\n  seen = seen * 10 + id\n  EXIT\nENDSCAN"));
    }

    [Fact]
    public void Scan_Exit_RestoresOriginalWorkAreaAfterBodySelectsAnotherArea()
    {
        using var dir = new MicroVfpTestSupport.TempDir("scan_exit_area");
        var interpreter = OpenNumbers(dir, out var session);
        using (session)
        {
            interpreter.Execute(@"
USE numbers AGAIN IN 0 ALIAS other
SELECT numbers
GO 2
seen = 0
SCAN NEXT 3
  seen = seen + 1
  SELECT other
  EXIT
ENDSCAN
afterAlias = ALIAS()
afterId = id");

            Assert.Equal(1m, interpreter.EvalExpression("seen").AsNumber);
            Assert.Equal("NUMBERS", interpreter.EvalExpression("afterAlias").AsString.ToUpperInvariant());
            Assert.Equal(2, interpreter.EvalExpression("afterId").AsInteger);
        }
    }

    [Fact]
    public void Scan_NextScope_CountsOnlyVisibleRecords()
    {
        using var dir = new MicroVfpTestSupport.TempDir("scan_filter");
        var interpreter = OpenNumbers(dir, out var session);
        using (session)
            Assert.Equal(13m, Run(interpreter,
                "SET FILTER TO MOD(id, 2) = 1\nGO TOP\nSCAN NEXT 2\n  seen = seen * 10 + id\nENDSCAN"));
    }

    [Fact]
    public void Scan_RestScope_FollowsControllingDescendingOrder()
    {
        using var dir = new MicroVfpTestSupport.TempDir("scan_order");
        var interpreter = OpenNumbers(dir, out var session);
        using (session)
            Assert.Equal(321m, Run(interpreter,
                "INDEX ON id TAG tid DESCENDING\nSET ORDER TO tid\nGO 3\nSCAN REST\n  seen = seen * 10 + id\nENDSCAN"));
    }
}
