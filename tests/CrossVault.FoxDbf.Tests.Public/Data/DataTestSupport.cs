using System;
using CrossVault.FoxDbf.Data;

namespace CrossVault.FoxDbf.Tests.Data;

/// <summary>
/// Scaffolding for the ADO.NET provider tests: a throwaway temp directory holding a COPY of the
/// canonical 10-row PERSON table (NEVER a committed fixture — DML mutates), plus connection-string
/// helpers. The table schema is ID(I) NAME(C20) CITY(C10) AMOUNT(N10,2) HIRED(D) ACTIVE(L), row 7
/// (Adams) deleted, with a structural CDX (see <see cref="SqlTestSupport.CreatePersonTable"/>).
/// </summary>
internal sealed class PersonDb : IDisposable
{
    private readonly SqlTestSupport.TempDir _dir;

    public PersonDb()
    {
        _dir = new SqlTestSupport.TempDir();
        SqlTestSupport.CreatePersonTable(_dir.File("person.dbf"));
    }

    /// <summary>The temp directory used as the connection's <c>Data Source</c> (free-table mode).</summary>
    public string Path => _dir.Path;

    /// <summary>A connection string against the temp dir; <paramref name="extra"/> appends more keys.</summary>
    public string ConnectionString(string? extra = null)
        => $"Data Source={_dir.Path}" + (extra is null ? "" : ";" + extra);

    /// <summary>An OPENED connection ready for commands.</summary>
    public FoxDbfConnection Open(string? extra = null)
    {
        var c = new FoxDbfConnection(ConnectionString(extra));
        c.Open();
        return c;
    }

    public void Dispose() => _dir.Dispose();
}
