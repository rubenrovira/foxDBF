using System;
using System.Data.Common;

namespace CrossVault.FoxDbf.Data;

/// <summary>
/// The ADO.NET provider exception type (mirrors <c>SqliteException</c>'s posture): every provider
/// failure surfaces as a <see cref="DbException"/> so generic ADO.NET callers can catch it without a
/// reference to <c>CrossVault.FoxDbf.Sql</c>'s <c>FoxDbfSqlException</c>.
/// </summary>
public sealed class FoxDbfException : DbException
{
    public FoxDbfException() { }
    public FoxDbfException(string? message) : base(message) { }
    public FoxDbfException(string? message, Exception? innerException) : base(message, innerException) { }
}
