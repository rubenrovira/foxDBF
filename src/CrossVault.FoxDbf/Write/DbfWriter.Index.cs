using System.Collections.Generic;
using System.Linq;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;

namespace CrossVault.FoxDbf.Write;

/// <summary>
/// §D7 — CDX index WRITING surface on the writer: <see cref="CreateTag"/> builds /
/// adds a tag to the structural compound <c>.cdx</c> beside the table, and
/// <see cref="Reindex"/> rebuilds every existing tag (REINDEX). Both delegate the
/// bulk-load to <see cref="CdxIndexBuilder"/>.
/// </summary>
public sealed partial class DbfWriter
{
    /// <summary>
    /// Build (or extend) the structural compound <c>.cdx</c> beside this table with a tag for
    /// <paramref name="definition"/> (plan §D7): compute the KEY for every non-deleted record via
    /// the expression engine, apply the FOR filter, sort, and bulk-load a balanced compact B-tree.
    /// </summary>
    public void CreateTag(CdxTagDefinition definition, EvaluationContext? evalContext = null, bool includeDeleted = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(definition);

        var tags = ReadExistingTagDefinitions();
        tags.RemoveAll(t => string.Equals(t.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
        tags.Add(definition);
        RebuildStructuralCdx(tags, evalContext, includeDeleted);
    }

    /// <summary>
    /// REINDEX — rebuild every tag of the structural <c>.cdx</c> from the live table data
    /// (plan §D7). A no-op when the table has no structural index.
    /// </summary>
    public void Reindex(EvaluationContext? evalContext = null, bool includeDeleted = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tags = ReadExistingTagDefinitions();
        if (tags.Count == 0)
            return;
        RebuildStructuralCdx(tags, evalContext, includeDeleted);
    }

    /// <summary>
    /// Build a standalone legacy <c>.idx</c> at <paramref name="idxPath"/> for KEY
    /// <paramref name="keyExpr"/> (optional <paramref name="forExpr"/> filter) over the live table —
    /// the write side of <c>INDEX ON eExpr TO cIdx</c>. Overwrites any existing file at the path.
    /// A standalone index is NOT advertised in the DBF header (it is not auto-opened by USE).
    /// </summary>
    public void CreateStandaloneIdx(string idxPath, string keyExpr, string? forExpr = null,
        bool unique = false, EvaluationContext? evalContext = null, bool includeDeleted = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(idxPath);
        ArgumentNullException.ThrowIfNull(keyExpr);
        Flush();
        var rows = MaterializeRows();
        IdxIndexBuilder.Build(idxPath, _schema, rows, keyExpr, forExpr, unique, evalContext, includeDeleted);
    }

    /// <summary>
    /// Build (or replace) a tag <paramref name="definition"/> in the compound <c>.cdx</c> at
    /// <paramref name="cdxPath"/> — the STRUCTURAL sidecar (<paramref name="structural"/> true, the DBF
    /// header structural bit is then advertised) or a NAMED non-structural <c>.cdx</c>
    /// (<paramref name="structural"/> false). Preserves any sibling tags already in the file.
    /// </summary>
    public void CreateTagIn(string cdxPath, bool structural, CdxTagDefinition definition,
        EvaluationContext? evalContext = null, bool includeDeleted = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(cdxPath);
        ArgumentNullException.ThrowIfNull(definition);

        var tags = ReadTagDefinitionsFrom(cdxPath);
        tags.RemoveAll(t => string.Equals(t.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
        tags.Add(definition);

        Flush();
        var rows = MaterializeRows();
        CdxIndexBuilder.Build(cdxPath, _schema, rows, tags, evalContext, includeDeleted);
        if (structural && !_hasStructuralCdx)
        {
            SetStructuralCdxFlag();
            _hasStructuralCdx = true;
            _usesStructuralScheme = VfpLock.UsesStructuralScheme(_version.Code, true);
        }
    }

    /// <summary>
    /// DELETE TAG — remove <paramref name="names"/> (or ALL when <paramref name="names"/> is
    /// <see langword="null"/>) from the compound <c>.cdx</c> at <paramref name="cdxPath"/>, rebuilding
    /// the remaining tags. When the last tag is removed the file is DELETED (VFP behaviour) and, for a
    /// <paramref name="structural"/> sidecar, the DBF header structural bit is cleared. Returns the number
    /// of tags remaining afterwards (0 ⇒ the file was deleted).
    /// </summary>
    public int DeleteTagsIn(string cdxPath, bool structural, IReadOnlyCollection<string>? names,
        EvaluationContext? evalContext = null, bool includeDeleted = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(cdxPath);

        var tags = ReadTagDefinitionsFrom(cdxPath);
        if (tags.Count == 0)
            return 0;

        if (names is null)
            tags.Clear();                                   // DELETE TAG ALL
        else
            tags.RemoveAll(t => names.Any(n => string.Equals(n, t.Name, StringComparison.OrdinalIgnoreCase)));

        if (tags.Count == 0)
        {
            try { if (File.Exists(cdxPath)) File.Delete(cdxPath); } catch { /* best-effort */ }
            if (structural && _hasStructuralCdx)
            {
                ClearStructuralCdxFlag();
                _hasStructuralCdx = false;
            }
            return 0;
        }

        Flush();
        var rows = MaterializeRows();
        CdxIndexBuilder.Build(cdxPath, _schema, rows, tags, evalContext, includeDeleted);
        return tags.Count;
    }

    /// <summary>Read the tag definitions currently in the compound <c>.cdx</c> at
    /// <paramref name="cdxPath"/> (empty when the file is missing). Throws only when the file exists but
    /// cannot be parsed.</summary>
    private static List<CdxTagDefinition> ReadTagDefinitionsFrom(string cdxPath)
    {
        var result = new List<CdxTagDefinition>();
        if (!File.Exists(cdxPath))
            return result;

        using var index = IndexFile.Open(cdxPath);
        var fileHeader = index.ReadCdxHeader(0);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byOffset = new List<(uint Offset, CdxTagDefinition Def)>();
        foreach (var (headerOffset, nameBytes) in
                 IndexTraversal.EnumerateCompact(index, fileHeader.Root, fileHeader.KeyLength, isCharacter: true))
        {
            string name = TrimName(nameBytes);
            if (name.Length == 0 || !seen.Add(name))
                continue;
            var hdr = index.ReadCdxHeader(headerOffset);
            string? forExpr = hdr.HasFor && hdr.ForExpression.Length > 0 ? hdr.ForExpression : null;
            byOffset.Add((headerOffset, new CdxTagDefinition(name, hdr.KeyExpression, forExpr,
                hdr.Descending, hdr.SortOrder, hdr.IsUnique)));
        }
        byOffset.Sort((a, b) => a.Offset.CompareTo(b.Offset));   // creation (header-layout) order.
        foreach (var (_, def) in byOffset) result.Add(def);
        return result;
    }

    /// <summary>The structural <c>.cdx</c> path beside the table (same stem, <c>.cdx</c> extension).</summary>
    private string StructuralCdxPath() => Path.ChangeExtension(_stream.Name, ".cdx");

    /// <summary>
    /// Resolve the on-disk structural <c>.cdx</c> path, honouring an existing case variant
    /// (<c>.CDX</c>) beside the table; falls back to the lower-case <c>.cdx</c> stem.
    /// </summary>
    private string ExistingCdxPath()
    {
        string lower = StructuralCdxPath();
        if (File.Exists(lower))
            return lower;

        string dir = Path.GetDirectoryName(lower) is { Length: > 0 } d ? d : ".";
        if (Directory.Exists(dir))
        {
            string name = Path.GetFileName(lower);
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
        }
        return lower;
    }

    /// <summary>
    /// Read the definitions of every tag currently in the structural <c>.cdx</c> (empty when none),
    /// so <see cref="CreateTag"/> can preserve siblings and <see cref="Reindex"/> can rebuild them.
    /// </summary>
    private List<CdxTagDefinition> ReadExistingTagDefinitions()
    {
        var result = new List<CdxTagDefinition>();
        string cdx = ExistingCdxPath();
        if (!File.Exists(cdx))
            return result;

        try
        {
            using var index = IndexFile.Open(cdx);
            var fileHeader = index.ReadCdxHeader(0);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The directory enumerates tags in NAME (B-tree) order, but VFP numbers tags by their
            // HEADER-PAGE LAYOUT order (= creation order — the directory's "recno" is the header byte
            // offset). Collect with that offset and sort by it so a rebuild preserves creation order —
            // which is what TAG()/SYS(14)/DESCENDING() enumerate over (verified vs vfp9.exe).
            var byOffset = new List<(uint Offset, CdxTagDefinition Def)>();
            foreach (var (headerOffset, nameBytes) in
                     IndexTraversal.EnumerateCompact(index, fileHeader.Root, fileHeader.KeyLength, isCharacter: true))
            {
                string name = TrimName(nameBytes);
                if (name.Length == 0 || !seen.Add(name))
                    continue;

                var hdr = index.ReadCdxHeader(headerOffset);
                string? forExpr = hdr.HasFor && hdr.ForExpression.Length > 0 ? hdr.ForExpression : null;
                byOffset.Add((headerOffset, new CdxTagDefinition(
                    name, hdr.KeyExpression, forExpr, hdr.Descending, hdr.SortOrder, hdr.IsUnique)));
            }
            byOffset.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            foreach (var (_, def) in byOffset) result.Add(def);
        }
        catch (Exception ex)
        {
            // The sidecar EXISTS (checked above) but could not be parsed. Returning an empty list here
            // would make CreateTag rebuild the structural CDX from nothing, DESTROYING the sibling tags
            // it cannot see. Surface the failure so a corrupt .cdx can never silently wipe tags — the
            // "no sidecar" case (legitimately empty) already returned above.
            throw new InvalidDataException(
                $"The structural index '{cdx}' exists but could not be read; refusing to rebuild it from " +
                "an empty tag set (which would discard its existing tags). Repair or delete the file.", ex);
        }

        // The IndexFile reader is designed to NEVER throw on malformed pages — it just yields nothing.
        // So a sidecar that EXISTS (checked above) yet parses to ZERO tags is corrupt, not legitimately
        // empty: a structural compound index always carries at least one tag. Treat it the same as an
        // unreadable sidecar so CreateTag can't silently rebuild from nothing and wipe its real tags.
        if (result.Count == 0)
            throw new InvalidDataException(
                $"The structural index '{cdx}' exists but no tags could be read from it; refusing to rebuild " +
                "it from an empty tag set (which would discard its existing tags). Repair or delete the file.");

        return result;
    }

    private static string TrimName(byte[] raw)
    {
        int len = raw.Length;
        while (len > 0 && (raw[len - 1] == 0x20 || raw[len - 1] == 0x00))
            len--;
        return len == 0 ? string.Empty : System.Text.Encoding.ASCII.GetString(raw, 0, len);
    }

    /// <summary>Bulk-build the structural <c>.cdx</c> for <paramref name="tags"/> over the live table.</summary>
    private void RebuildStructuralCdx(IReadOnlyList<CdxTagDefinition> tags, EvaluationContext? evalContext = null, bool includeDeleted = false)
    {
        Flush();
        var rows = MaterializeRows();
        CdxIndexBuilder.Build(StructuralCdxPath(), _schema, rows, tags, evalContext, includeDeleted);

        // The sidecar VFP just wrote is a STRUCTURAL compound .cdx (options 0xE0). Advertise it in the
        // DBF header (byte 28 bit 0) so a real VFP runtime auto-opens it on USE, and flip the writer's
        // own _hasStructuralCdx/_usesStructuralScheme so a subsequent Pack/Zap correctly invalidates
        // (deletes + ReindexNeeded) the now-owned sidecar instead of early-returning and leaving it stale.
        if (!_hasStructuralCdx)
        {
            SetStructuralCdxFlag();
            _hasStructuralCdx = true;
            _usesStructuralScheme = VfpLock.UsesStructuralScheme(_version.Code, true);
        }
    }

    /// <summary>
    /// Set the structural-CDX flag (header byte 28, bit <c>0x01</c>) in place, preserving every other
    /// flag in that byte (e.g. the memo bit <c>0x02</c>). The inverse of
    /// <see cref="ClearStructuralCdxFlag"/>. No-op if the bit is already set.
    /// </summary>
    private void SetStructuralCdxFlag()
    {
        _stream.Seek(28, SeekOrigin.Begin);
        int current = _stream.ReadByte();
        if (current < 0)
            return; // header truncated — nothing safe to rewrite.

        byte set = (byte)(current | 0x01);
        if (set == (byte)current)
            return; // already set.

        _stream.Seek(28, SeekOrigin.Begin);
        _stream.WriteByte(set);
        _stream.Flush();
    }

    /// <summary>
    /// Snapshot every physical record (1-based recno + decoded row) straight off the raw handle via
    /// <see cref="RandomAccess"/>, using the writer's LIVE record count — the schema's cached count
    /// is stale once records have been appended. Deleted-record filtering happens in the builder.
    /// </summary>
    private List<CdxIndexBuilder.BuildRow> MaterializeRows()
    {
        int count = _recordCount;
        var rows = new List<CdxIndexBuilder.BuildRow>(count);
        var buffer = new byte[_recordLength];
        for (int i = 0; i < count; i++)
        {
            long offset = _headerLength + (long)i * _recordLength;
            int read = ReadRawInto(_handle, offset, buffer);
            if (read < _recordLength)
                // A truncated tail means the file is shorter than its header's record count claims —
                // silently dropping the missing rows would build an index over an INCOMPLETE table.
                throw new InvalidDataException(
                    $"Table is truncated: expected {count} records but record {i + 1} is incomplete " +
                    $"({read} of {_recordLength} bytes). Refusing to index a partial table.");
            var copy = (byte[])buffer.Clone();
            rows.Add(new CdxIndexBuilder.BuildRow(i + 1, new DbfRecord(_schema, copy)));
        }
        return rows;
    }
}
