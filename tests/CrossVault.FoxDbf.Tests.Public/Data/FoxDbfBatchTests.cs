using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using CrossVault.FoxDbf.Data;
using Xunit;

namespace CrossVault.FoxDbf.Tests.Data;

/// <summary>
/// Acceptance tests (TDD RED) for <see cref="FoxDbfBatch"/> + <see cref="FoxDbfBatchCommand"/> — the
/// <see cref="DbBatch"/> abstraction (.NET 6+) executing multiple commands over one
/// <see cref="FoxDbfConnection"/>. Local file engine, so no round-trip saving; the semantics are:
/// ExecuteReader exposes the FIRST command's rows and NextResult() walks the rest; ExecuteNonQuery sums
/// the affected counts; ExecuteScalar is the first cell of the first command; the batch honors the
/// connection's transaction. Each test drives a temp COPY of the canonical PERSON table.
/// </summary>
public sealed class FoxDbfBatchTests
{
    // ---- (1) Factory + connection expose the batch ---------------------------------------------

    [Fact]
    public void Factory_CanCreateBatch_And_CreateBatch_ReturnsType()
    {
        Assert.True(FoxDbfProviderFactory.Instance.CanCreateBatch);
        Assert.IsType<FoxDbfBatch>(FoxDbfProviderFactory.Instance.CreateBatch());
        Assert.IsType<FoxDbfBatchCommand>(FoxDbfProviderFactory.Instance.CreateBatchCommand());
    }

    [Fact]
    public void Connection_CreateBatch_ReturnsType()
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        Assert.True(conn.CanCreateBatch);
        using var batch = conn.CreateBatch();
        Assert.IsType<FoxDbfBatch>(batch);

        // The BatchCommands collection accepts FoxDbfBatchCommands.
        var bc = (FoxDbfBatchCommand)batch.CreateBatchCommand();
        bc.CommandText = "SELECT 1";
        batch.BatchCommands.Add(bc);
        Assert.Single(batch.BatchCommands);
        Assert.Same(bc, batch.BatchCommands[0]);
    }

    // ---- (2) ExecuteReader: first command's rows, NextResult() walks to the next ---------------

    [Fact]
    public void ExecuteReader_FirstCommandRows_ThenNextResult_Advances()
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        using var batch = conn.CreateBatch();

        batch.BatchCommands.Add(new FoxDbfBatchCommand("SELECT id FROM person WHERE id <= 2 ORDER BY id"));
        batch.BatchCommands.Add(new FoxDbfBatchCommand("SELECT name FROM person WHERE id = 3"));

        using var r = batch.ExecuteReader();

        // First command's result set.
        var ids = new List<int>();
        while (r.Read()) ids.Add(Convert.ToInt32(r.GetValue(0)));
        Assert.Equal(new[] { 1, 2 }, ids);

        // NextResult walks to the second command's result set.
        Assert.True(r.NextResult());
        Assert.True(r.Read());
        Assert.Equal("Smithson", ((string)r.GetValue(0)).TrimEnd());
        Assert.False(r.Read());

        // No third command.
        Assert.False(r.NextResult());
    }

    // ---- (3) ExecuteNonQuery sums every command's affected count -------------------------------

    [Fact]
    public void ExecuteNonQuery_SumsAffectedAcrossCommands()
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        using var batch = conn.CreateBatch();

        var c1 = new FoxDbfBatchCommand("UPDATE person SET active = .T. WHERE city = 'Berlin'"); // 1,2,4,9 = 4
        var c2 = new FoxDbfBatchCommand("UPDATE person SET active = .T. WHERE city = 'Munich'"); // 3,5,8   = 3
        batch.BatchCommands.Add(c1);
        batch.BatchCommands.Add(c2);

        Assert.Equal(7, batch.ExecuteNonQuery());

        // Each command exposes its own affected count after execution.
        Assert.Equal(4, c1.RecordsAffected);
        Assert.Equal(3, c2.RecordsAffected);
    }

    // ---- (4) ExecuteScalar = first column of first row of the FIRST command --------------------

    [Fact]
    public void ExecuteScalar_FirstCellOfFirstCommand()
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        using var batch = conn.CreateBatch();

        batch.BatchCommands.Add(new FoxDbfBatchCommand("SELECT name FROM person WHERE id = 3"));
        batch.BatchCommands.Add(new FoxDbfBatchCommand("SELECT id FROM person WHERE id = 1"));

        Assert.Equal("Smithson", ((string)batch.ExecuteScalar()!).TrimEnd());
    }

    // ---- (5) The batch honors the connection's transaction (rollback discards the writes) ------

    [Fact]
    public void Batch_HonorsConnectionTransaction_RollbackDiscards()
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        using (var tx = conn.BeginTransaction())
        {
            using var batch = conn.CreateBatch();
            batch.Transaction = tx;
            batch.BatchCommands.Add(new FoxDbfBatchCommand("UPDATE person SET city = 'Cologne' WHERE id = 1"));
            Assert.Equal(1, batch.ExecuteNonQuery());

            // Read-your-writes inside the transaction.
            using var check = conn.CreateCommand();
            check.Transaction = tx;
            check.CommandText = "SELECT city FROM person WHERE id = 1";
            Assert.Equal("Cologne", ((string)check.ExecuteScalar()!).TrimEnd());

            tx.Rollback();
        }

        // After rollback the write is gone on a fresh connection.
        using var conn2 = db.Open();
        using var verify = conn2.CreateCommand();
        verify.CommandText = "SELECT city FROM person WHERE id = 1";
        Assert.Equal("Berlin", ((string)verify.ExecuteScalar()!).TrimEnd());
    }
}
