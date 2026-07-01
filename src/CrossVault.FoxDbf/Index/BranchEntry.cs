namespace CrossVault.FoxDbf.Index;

/// <summary>
/// One entry of an interior (branch) B-tree node (plan §C3):
/// <c>[key(KeyLength)] [recno(4, BIG-endian)] [childPointer(4, BIG-endian)]</c>.
/// Note the mixed-endianness trap: branch numbers are stored BIG-endian even
/// though almost everything else in the file is little-endian.
/// </summary>
public readonly struct BranchEntry
{
    /// <summary>The raw key bytes (length == the node's key length).</summary>
    public byte[] Key { get; }

    /// <summary>The separator record number (read BIG-endian).</summary>
    public uint RecordNumber { get; }

    /// <summary>The child node pointer as a byte offset (read BIG-endian).</summary>
    public uint ChildPointer { get; }

    public BranchEntry(byte[] key, uint recordNumber, uint childPointer)
    {
        Key = key;
        RecordNumber = recordNumber;
        ChildPointer = childPointer;
    }
}
