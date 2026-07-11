using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests.Data;

/// <summary>Connection acquisition publishes ownership only after the data source opens successfully.</summary>
public sealed class FoxDbfConnectionOpenFailureTests : IDisposable
{
    private static readonly FieldInfo SessionField =
        typeof(FoxDbfConnection).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo AcceleratorField =
        typeof(FoxDbfConnection).GetField("_accelerator", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo SessionDisposedField =
        typeof(VfpSession).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly SqlTestSupport.TempDir _dir = new();

    public FoxDbfConnectionOpenFailureTests()
        => SqlTestSupport.CreatePersonTable(_dir.File("person.dbf"));

    public void Dispose() => _dir.Dispose();

    private string MissingPath => _dir.File("missing-source");
    private string ValidConnectionString => $"Data Source={_dir.Path}";

    [Fact]
    public void FailedHighlikeOpen_LeavesClosedConnectionWithoutPublishedOwners()
    {
        using var connection = new FoxDbfConnection(
            $"Data Source={MissingPath};Accelerator=Highlike");

        Assert.Throws<FoxDbfException>(() => connection.Open());

        var capturedSession = SessionField.GetValue(connection) as VfpSession;
        if (capturedSession is not null)
            Assert.True((bool)SessionDisposedField.GetValue(capturedSession)!);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Null(SessionField.GetValue(connection));
        Assert.Null(AcceleratorField.GetValue(connection));
    }

    [Fact]
    public void FailedOpen_CanRetryWithCorrectedConnectionString()
    {
        using var connection = new FoxDbfConnection(
            $"Data Source={MissingPath};Accelerator=Highlike");
        Assert.Throws<FoxDbfException>(() => connection.Open());

        connection.ConnectionString = ValidConnectionString;
        connection.Open();

        Assert.Equal(ConnectionState.Open, connection.State);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM person WHERE id = 1";
        Assert.Equal(1, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public void FailedOpen_RaisesNoStateChange_RetryRaisesClosedToOpenExactlyOnce()
    {
        using var connection = new FoxDbfConnection(
            $"Data Source={MissingPath};Accelerator=Highlike");
        var transitions = new List<(ConnectionState Original, ConnectionState Current)>();
        connection.StateChange += (_, e) => transitions.Add((e.OriginalState, e.CurrentState));

        Assert.Throws<FoxDbfException>(() => connection.Open());
        Assert.Empty(transitions);

        connection.ConnectionString = ValidConnectionString;
        connection.Open();

        Assert.Equal(new[] { (ConnectionState.Closed, ConnectionState.Open) }, transitions);
    }

    [Fact]
    public void AcquisitionExceptions_PreserveFoxDbfAndWrapNonFoxFailures()
    {
        using (var missing = new FoxDbfConnection($"Data Source={MissingPath}"))
        {
            var error = Assert.Throws<FoxDbfException>(() => missing.Open());
            Assert.Null(error.InnerException);
            Assert.DoesNotContain("Failed to open connection", error.Message, StringComparison.Ordinal);
        }

        using (var malformed = new FoxDbfConnection("Data Source=\"unterminated"))
        {
            var error = Assert.Throws<FoxDbfException>(() => malformed.Open());
            Assert.StartsWith("Failed to open connection:", error.Message, StringComparison.Ordinal);
            Assert.NotNull(error.InnerException);
            Assert.IsNotType<FoxDbfException>(error.InnerException);
        }
    }

    [Fact]
    public void ThrowingOpenStateChangeHandler_PreservesWrapperAndLeavesPublishedOwnersUsable()
    {
        using var connection = new FoxDbfConnection($"{ValidConnectionString};Accelerator=Highlike");
        var handlerError = new InvalidOperationException("boom");
        connection.StateChange += (_, e) =>
        {
            if (e.CurrentState == ConnectionState.Open)
                throw handlerError;
        };

        var error = Assert.Throws<FoxDbfException>(() => connection.Open());

        Assert.Equal("Failed to open connection: boom", error.Message);
        Assert.Same(handlerError, error.InnerException);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.NotNull(SessionField.GetValue(connection));
        Assert.NotNull(AcceleratorField.GetValue(connection));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM person WHERE id = 1";
        Assert.Equal(1, Convert.ToInt32(command.ExecuteScalar()));

        connection.Close();
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Null(SessionField.GetValue(connection));
        Assert.Null(AcceleratorField.GetValue(connection));
    }
}
