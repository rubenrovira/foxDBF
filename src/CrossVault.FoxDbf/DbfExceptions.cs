namespace CrossVault.FoxDbf;

/// <summary>
/// Thrown by <see cref="DbfTable.Open(string)"/> when the requested table file
/// does not exist on disk (plan §A10). Distinct from the BCL
/// <see cref="System.IO.FileNotFoundException"/> so callers can catch DBF-open
/// failures specifically.
/// </summary>
public sealed class DbfFileNotFoundException : Exception
{
    public DbfFileNotFoundException() { }
    public DbfFileNotFoundException(string message) : base(message) { }
    public DbfFileNotFoundException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when a record is accessed on a table whose field-descriptor array is
/// empty (zero columns), e.g. <c>polygon.dbf</c> — a shapefile attribute table
/// with no DBF columns (plan §A10). Opening such a table is allowed; only
/// record access is undefined.
/// </summary>
public sealed class DbfNoColumnsDefinedException : Exception
{
    public DbfNoColumnsDefinedException() { }
    public DbfNoColumnsDefinedException(string message) : base(message) { }
    public DbfNoColumnsDefinedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when a field descriptor yields a negative column length (plan §A10).
/// A defensive guard: the on-disk length byte is unsigned, but the contract is
/// pinned so callers/constructors that hand in a negative length fail loudly
/// rather than producing a corrupt record geometry.
/// </summary>
public sealed class DbfColumnLengthException : Exception
{
    public DbfColumnLengthException() { }
    public DbfColumnLengthException(string message) : base(message) { }
    public DbfColumnLengthException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when a field descriptor yields an empty column name after cleaning
/// (NUL/space trimming) — a structurally invalid descriptor (plan §A10).
/// </summary>
public sealed class DbfColumnNameException : Exception
{
    public DbfColumnNameException() { }
    public DbfColumnNameException(string message) : base(message) { }
    public DbfColumnNameException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown by <see cref="DbfTable.Open(string, DbfOptions)"/> under
/// <see cref="Recovery.Strict"/> when the header is structurally impossible/inconsistent
/// — e.g. a zeroed <c>RecordLength</c>/<c>HeaderLength</c> (the <c>corrupt.prg</c>
/// first-10-bytes-nulled case) — instead of silently reconstructing (plan §A12).
/// Use <see cref="Recovery.Reconstruct"/> to rebuild geometry from the descriptors.
/// </summary>
public sealed class DbfCorruptHeaderException : Exception
{
    public DbfCorruptHeaderException() { }
    public DbfCorruptHeaderException(string message) : base(message) { }
    public DbfCorruptHeaderException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown by <see cref="DbfTable.Open(string)"/> when the file's version byte is an
/// unknown/unsupported layout (e.g. the encrypted <c>V_usr.dbf</c> <c>0xee</c>), plan §A12.
/// Use <see cref="DbfTable.TryOpen(string, out string?)"/> for a non-throwing,
/// directory-scan-friendly probe, or <see cref="DbfOptions.ForceVersion"/> to force a layout.
/// </summary>
public sealed class DbfUnsupportedVersionException : Exception
{
    public DbfUnsupportedVersionException() { }
    public DbfUnsupportedVersionException(string message) : base(message) { }
    public DbfUnsupportedVersionException(string message, Exception inner) : base(message, inner) { }
}
