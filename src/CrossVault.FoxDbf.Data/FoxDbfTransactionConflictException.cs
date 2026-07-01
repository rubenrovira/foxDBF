using System;
using System.Data.Common;

namespace CrossVault.FoxDbf.Data;

/// <summary>
/// Thrown by <see cref="FoxDbfTransaction.Commit"/> when OPTIMISTIC CONCURRENCY detects that a table
/// touched (written) by the transaction was changed on disk by a FOREIGN writer after the transaction
/// took its private working copy. The copy-on-write transaction records each touched table's
/// change-token (record count + last-update stamp + file length / mtime) at copy time and re-verifies
/// it at commit; a mismatch means another connection / a direct <c>DbfWriter</c> mutated the live file,
/// so committing the private copy would silently LOSE that foreign change. The commit therefore fails
/// with this exception (no lost update) and the transaction can still be rolled back cleanly.
/// <para>
/// Derives from <see cref="DbException"/> (like <see cref="FoxDbfException"/>) so generic ADO.NET
/// callers can catch it without a reference to <c>CrossVault.FoxDbf.Sql</c>.
/// </para>
/// </summary>
public sealed class FoxDbfTransactionConflictException : DbException
{
    public FoxDbfTransactionConflictException() { }
    public FoxDbfTransactionConflictException(string? message) : base(message) { }
    public FoxDbfTransactionConflictException(string? message, Exception? innerException)
        : base(message, innerException) { }
}
