using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpDeleteRecallUnsupportedTests
{
    private const string DeleteMessage = "DELETE: record scope/FOR/WHILE clauses are not supported.";
    private const string RecallMessage = "RECALL: record scope/FOR/WHILE clauses are not supported.";

    [Theory]
    [InlineData("DELETE ALL", false, DeleteMessage)]
    [InlineData("DELETE REST", false, DeleteMessage)]
    [InlineData("DELETE NEXT 2", false, DeleteMessage)]
    [InlineData("DELETE RECORD 3", false, DeleteMessage)]
    [InlineData("DELETE FOR id > 1", false, DeleteMessage)]
    [InlineData("DELETE WHILE id < 3", false, DeleteMessage)]
    [InlineData("DELETE NEXT 2 FOR id > 1 WHILE id < 4 IN items", false, DeleteMessage)]
    [InlineData("RECALL ALL", true, RecallMessage)]
    [InlineData("RECALL REST", true, RecallMessage)]
    [InlineData("RECALL NEXT 2", true, RecallMessage)]
    [InlineData("RECALL RECORD 3", true, RecallMessage)]
    [InlineData("RECALL FOR id > 1", true, RecallMessage)]
    [InlineData("RECALL WHILE id < 3", true, RecallMessage)]
    [InlineData("RECALL NEXT 2 FOR id > 1 WHILE id < 4", true, RecallMessage)]
    public void UnsupportedClauses_ThrowExactMessage_WithoutChangingRecordBytes(
        string command,
        bool prepareDeletedCurrent,
        string expectedMessage)
    {
        using var f = new Fixture();
        f.Run("GO 2");
        if (prepareDeletedCurrent) f.Run("DELETE");
        byte[] before = f.Bytes();

        Exception? error = Record.Exception(() => f.Run(command));

        Assert.Equal(before, f.Bytes());
        var ex = Assert.IsType<MicroVfpRuntimeException>(error);
        Assert.Equal(expectedMessage, ex.Message);
    }

    [Theory]
    [InlineData("DELETE ALL", false, DeleteMessage)]
    [InlineData("RECALL ALL", true, RecallMessage)]
    public void UnsupportedClauses_ThrowBeforeBufferedMutation(
        string command,
        bool prepareDeletedCurrent,
        string expectedMessage)
    {
        using var f = new Fixture();
        f.Run("GO 2");
        if (prepareDeletedCurrent) f.Run("DELETE");
        f.Run("=CURSORSETPROP('Buffering', 5)");
        byte[] before = f.Bytes();

        var ex = Assert.Throws<MicroVfpRuntimeException>(() => f.Run(command));

        Assert.Equal(expectedMessage, ex.Message);
        Assert.Equal(before, f.Bytes());
    }

    [Fact]
    public void UnsupportedDelete_OnErrorHandlesExceptionWithoutMutation()
    {
        using var f = new Fixture();
        f.Run("GO 2\nhandled = .F.\nON ERROR handled = .T.");
        byte[] before = f.Bytes();

        f.Run("DELETE ALL");

        Assert.True(f.Bool("handled"));
        Assert.Equal(before, f.Bytes());
    }

    [Fact]
    public void BareDeleteAndRecall_StillChangeOnlyCurrentRecord()
    {
        using var f = new Fixture();

        f.Run("GO 2\nDELETE");
        Assert.False(f.Deleted(0));
        Assert.True(f.Deleted(1));
        Assert.False(f.Deleted(2));

        f.Run("RECALL");
        Assert.False(f.Deleted(0));
        Assert.False(f.Deleted(1));
        Assert.False(f.Deleted(2));
    }

    [Fact]
    public void BareDeleteInAlias_StillChangesThatAreasCurrentRecord()
    {
        using var f = new Fixture();
        f.Run("USE items AGAIN IN 0 ALIAS other\nSELECT other\nGO 3\nSELECT items\nGO 1\nDELETE IN other");

        Assert.False(f.Deleted(0));
        Assert.False(f.Deleted(1));
        Assert.True(f.Deleted(2));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MicroVfpTestSupport.TempDir _dir = new("delete_recall_unsupported");
        private readonly string _dbf;
        private readonly VfpSession _session;
        private readonly VfpInterpreter _interp;

        public Fixture()
        {
            _dbf = _dir.File("items.dbf");
            using (var writer = DbfWriter.Create(_dbf,
                [new DbfColumnDef("ID", 'I', 4), new DbfColumnDef("NAME", 'C', 12)]))
            {
                writer.AppendRecord(1, "one");
                writer.AppendRecord(2, "two");
                writer.AppendRecord(3, "three");
            }

            _session = new VfpSession();
            _session.OpenDirectory(_dir.Path);
            _interp = new VfpInterpreter(_session);
            Run("USE items");
        }

        public void Run(string source) => _interp.Execute(source);
        public bool Bool(string expression) => _interp.EvalExpression(expression).AsLogical;
        public byte[] Bytes()
        {
            using var stream = new FileStream(
                _dbf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }

        public bool Deleted(int recordIndex)
        {
            byte[] bytes = Bytes();
            int headerLength = BitConverter.ToUInt16(bytes, 8);
            int recordLength = BitConverter.ToUInt16(bytes, 10);
            return bytes[headerLength + recordIndex * recordLength] == (byte)'*';
        }

        public void Dispose()
        {
            _interp.Dispose();
            _session.Dispose();
            _dir.Dispose();
        }
    }
}
