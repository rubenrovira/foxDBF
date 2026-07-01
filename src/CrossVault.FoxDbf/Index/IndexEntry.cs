namespace CrossVault.FoxDbf.Index;

/// <summary>
/// One logical entry produced when enumerating a CDX tag or an IDX index in
/// index order (plan §C3/§C4): the indexed 1-based DBF <see cref="RecordNumber"/>
/// and the reconstructed <see cref="Key"/> bytes (the tag's key length, including
/// any trailing pad).
/// </summary>
public readonly struct IndexEntry
{
    /// <summary>The 1-based DBF record number this key points at.</summary>
    public uint RecordNumber { get; }

    /// <summary>The reconstructed key bytes (front-coding resolved, pad applied).</summary>
    public byte[] Key { get; }

    public IndexEntry(uint recordNumber, byte[] key)
    {
        RecordNumber = recordNumber;
        Key = key;
    }
}
