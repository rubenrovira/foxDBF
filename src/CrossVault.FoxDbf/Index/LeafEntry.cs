namespace CrossVault.FoxDbf.Index;

/// <summary>
/// One decoded entry of a compact leaf node (plan §C4): the indexed
/// <see cref="RecordNumber"/> and the fully reconstructed <see cref="Key"/>
/// bytes (length == the tag's key length, including any trailing pad).
/// </summary>
public readonly struct LeafEntry
{
    /// <summary>The 1-based DBF record number this key points at.</summary>
    public uint RecordNumber { get; }

    /// <summary>
    /// The reconstructed key bytes (front-coding resolved, trailing pad applied).
    /// Always exactly the tag key length long.
    /// </summary>
    public byte[] Key { get; }

    public LeafEntry(uint recordNumber, byte[] key)
    {
        RecordNumber = recordNumber;
        Key = key;
    }
}
