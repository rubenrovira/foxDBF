using System.Collections.Generic;
using System.Linq;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;

namespace CrossVault.FoxDbf.Write;

/// <summary>
/// Project-review finding 5.1 — INCREMENTAL structural-<c>.cdx</c> maintenance on the WRITE path. Every
/// append / key-changing update funnels through <see cref="AppendCore"/> / <see cref="UpdateCore"/>, which
/// (after the row is durably written) edit every open structural tag's on-disk B-tree in place via
/// <see cref="CdxTreeEditor"/> so SEEK / ordered navigation see the change WITHOUT a REINDEX — matching a
/// real VFP9 which maintains every open tag on every write. The SQL DML executor, the ADO.NET provider and
/// microVFP REPLACE/APPEND all route through the same two cores, so they inherit maintenance for free.
/// </summary>
/// <remarks>
/// SCOPE / DEFERRED (flagged): this maintains the STRUCTURAL <c>.cdx</c> (header byte 28 bit 0). microVFP's
/// non-structural open indexes (standalone <c>.idx</c>, <c>OF &lt;cdx&gt;</c>) are NOT edited here — they are
/// rebuilt by microVFP's own INDEX/REINDEX path and re-opened after each write, never left silently stale.
/// CANDIDATE-uniqueness ENFORCEMENT (raise on a duplicate) lives in microVFP (which alone knows a tag is a
/// candidate — a free-table candidate tag is byte-identical to a plain tag on disk); the writer only honours
/// the on-disk UNIQUE bit (keep first key). Delete()/Recall() do NO index work (VFP keeps a deleted record's
/// entries until PACK).
/// <para>
/// DURABILITY — a tag we cannot faithfully maintain never leaves a valid-looking-but-stale <c>.cdx</c> on
/// disk (which would silently mis-answer SEEK and re-introduce finding 5.1). A tag is "un-derivable" when its
/// KEY (or FOR) expression names a field ABSENT from the raw <c>.dbf</c> schema — the canonical DBC long-name
/// case (a tag keyed on <c>customer_id</c> over the physical <c>CUSTOMER_I</c>) — or uses an expression we
/// cannot compile: our raw-schema derivation would then produce WRONG (typically all-blank) key bytes, and a
/// REINDEX (same derivation) cannot rebuild it either. So when ANY structural tag is un-derivable, or tag
/// maintenance throws, we PHYSICALLY INVALIDATE the whole structural <c>.cdx</c> — the same durable mechanism
/// PACK/ZAP use (<see cref="InvalidateStructuralCdx"/>): delete the now-stale sidecar, clear the header's
/// structural bit and raise <see cref="ReindexNeeded"/>, so nothing on disk lies and a real VFP <c>USE</c> /
/// an explicit REINDEX rebuilds it from scratch. This supersedes the per-write in-memory flag (which a
/// short-lived per-statement writer would otherwise lose on Dispose). Crash safety: the row is written FIRST;
/// only then are the tags edited, so an invalidation never loses committed row data.
/// </para>
/// </remarks>
public sealed partial class DbfWriter
{
    // Compiled per-tag key/FOR derivation, cached across this writer's lifetime keyed by tag name (a NULL
    // value caches "this tag cannot be incrementally maintained" — see GetComputer). A tag's DEFINITION
    // (key expr / collation / FOR / unique) is stable for the writer's life; only its on-disk ROOT moves
    // (read fresh from the header on every edit), so caching the computer is safe.
    private Dictionary<string, CdxIndexBuilder.TagKeyComputer?>? _tagComputers;

    /// <summary>True when a structural <c>.cdx</c> exists and should be incrementally maintained on writes.
    /// False for a table with no structural index (must stay 100% untouched) or once maintenance has already
    /// physically invalidated the sidecar (it no longer exists on disk — a rebuild is pending anyway). This
    /// deliberately does NOT gate on <see cref="ReindexNeeded"/>: an un-derivable tag must not disable
    /// maintenance of the perfectly derivable SIBLING tags across writes — that per-tag decision is made in
    /// the maintenance loop, and a genuine invalidation removes the file (so the File.Exists check covers it).</summary>
    private bool ShouldMaintainIndexes()
        => _hasStructuralCdx && File.Exists(ExistingCdxPath());

    /// <summary>Maintain every structural tag after an APPEND: insert the new record's key (honouring each
    /// tag's FOR filter and UNIQUE bit). An un-derivable tag or a maintenance throw physically invalidates
    /// the sidecar (durable) rather than leave it silently stale; a throw is rethrown after invalidation.</summary>
    private void MaintainIndexesAfterAppend(int recNo, DbfRecord newRecord)
    {
        if (!ShouldMaintainIndexes())
            return;
        bool deferred = false;
        try
        {
            ThrowIfInjectedFault();
            using (var rw = OpenCdxReadWrite())
            using (var editor = new CdxTreeEditor(rw))
            {
                if (!TryResolveComputers(editor, out var plans))
                    deferred = true;   // an un-derivable tag → invalidate below (handle closed first)
                else
                {
                    foreach (var (tag, computer) in plans)
                    {
                        if (!computer.TryComputeKey(newRecord, recNo, _recordCount, out var key))
                            continue;   // FOR filter excludes this record from the tag
                        if (computer.Unique && editor.ContainsKey(tag.HeaderOffset, key, computer.KeyLen, computer.Pad))
                            continue;   // UNIQUE tag already holds the key (keep the first record — VFP semantics)
                        editor.Insert(tag.HeaderOffset, key, (uint)recNo, computer.KeyLen, computer.Pad, _recordCount);
                    }
                    editor.Flush();
                }
            }
        }
        catch
        {
            InvalidateStructuralCdxForMaintenance();
            throw;
        }
        if (deferred)
            InvalidateStructuralCdxForMaintenance();
    }

    /// <summary>
    /// Maintain every structural tag after a BATCH append (<see cref="AppendCoreBatch"/>): insert every new
    /// record's key into every derivable tag under ONE editor session (far cheaper than re-opening per row),
    /// honouring each tag's FOR filter and UNIQUE bit (a UNIQUE tag's ContainsKey sees earlier same-batch
    /// inserts because they land in the live tree first). Same durability contract as the per-row path — an
    /// un-derivable tag or a throw physically invalidates the sidecar rather than leaving it silently stale.
    /// This closes the batch-path backstop gap (finding 5.1 review item 4): the public
    /// <see cref="AppendRecords(System.Collections.Generic.IEnumerable{object?[]})"/> API is now safe against
    /// an already-indexed table, not only the brand-new tables its in-repo callers happen to target.
    /// </summary>
    private void MaintainIndexesAfterAppendBatch(IReadOnlyList<(int RecNo, byte[] Record)> appended)
    {
        if (appended.Count == 0 || !ShouldMaintainIndexes())
            return;
        bool deferred = false;
        try
        {
            ThrowIfInjectedFault();
            using (var rw = OpenCdxReadWrite())
            using (var editor = new CdxTreeEditor(rw))
            {
                if (!TryResolveComputers(editor, out var plans))
                    deferred = true;
                else
                {
                    foreach (var (tag, computer) in plans)
                        foreach (var (recNo, bytes) in appended)
                        {
                            var record = new DbfRecord(_schema, bytes);
                            if (!computer.TryComputeKey(record, recNo, _recordCount, out var key))
                                continue;
                            if (computer.Unique && editor.ContainsKey(tag.HeaderOffset, key, computer.KeyLen, computer.Pad))
                                continue;
                            editor.Insert(tag.HeaderOffset, key, (uint)recNo, computer.KeyLen, computer.Pad, _recordCount);
                        }
                    editor.Flush();
                }
            }
        }
        catch
        {
            InvalidateStructuralCdxForMaintenance();
            throw;
        }
        if (deferred)
            InvalidateStructuralCdxForMaintenance();
    }

    /// <summary>Maintain every structural tag after an in-place UPDATE: for each tag compute the OLD key
    /// (pre-image) and NEW key; when they differ, or FOR-filter membership changed, delete the old entry and
    /// insert the new one. An un-derivable tag or a maintenance throw physically invalidates the sidecar.</summary>
    private void MaintainIndexesAfterUpdate(int recNo, DbfRecord oldRecord, DbfRecord newRecord)
    {
        if (!ShouldMaintainIndexes())
            return;
        bool deferred = false;
        try
        {
            ThrowIfInjectedFault();
            using (var rw = OpenCdxReadWrite())
            using (var editor = new CdxTreeEditor(rw))
            {
                if (!TryResolveComputers(editor, out var plans))
                    deferred = true;
                else
                {
                    foreach (var (tag, computer) in plans)
                    {
                        bool oldIn = computer.TryComputeKey(oldRecord, recNo, _recordCount, out var oldKey);
                        bool newIn = computer.TryComputeKey(newRecord, recNo, _recordCount, out var newKey);

                        // Unchanged membership AND identical key → nothing to do for this tag.
                        if (oldIn && newIn && oldKey.AsSpan().SequenceEqual(newKey))
                            continue;

                        if (oldIn)
                            editor.Delete(tag.HeaderOffset, oldKey, (uint)recNo, computer.KeyLen, computer.Pad, _recordCount);
                        if (newIn && !(computer.Unique && editor.ContainsKey(tag.HeaderOffset, newKey, computer.KeyLen, computer.Pad)))
                            editor.Insert(tag.HeaderOffset, newKey, (uint)recNo, computer.KeyLen, computer.Pad, _recordCount);
                    }
                    editor.Flush();
                }
            }
        }
        catch
        {
            InvalidateStructuralCdxForMaintenance();
            throw;
        }
        if (deferred)
            InvalidateStructuralCdxForMaintenance();
    }

    /// <summary>
    /// Resolve the per-tag key computer for EVERY structural tag. Returns <see langword="true"/> with a plan
    /// per tag when all tags are derivable (safe to edit incrementally); returns <see langword="false"/> the
    /// moment ANY tag is un-derivable (<see cref="GetComputer"/> is null) — the caller then invalidates the
    /// whole sidecar instead of editing, because a compound index with one stale tag cannot be trusted and we
    /// cannot rebuild the un-derivable tag either.
    /// </summary>
    private bool TryResolveComputers(CdxTreeEditor editor,
        out List<(CdxTreeEditor.TagHandle Tag, CdxIndexBuilder.TagKeyComputer Computer)> plans)
    {
        plans = new List<(CdxTreeEditor.TagHandle, CdxIndexBuilder.TagKeyComputer)>();
        foreach (var tag in editor.ReadTags())
        {
            var computer = GetComputer(tag);
            if (computer is null)
            {
                plans = null!;
                return false;
            }
            plans.Add((tag, computer));
        }
        return true;
    }

    /// <summary>
    /// Physically invalidate the structural <c>.cdx</c> when maintenance cannot keep it faithful (an
    /// un-derivable tag or a maintenance throw): the SAME durable, consumed mechanism PACK/ZAP use — delete
    /// the stale sidecar, clear the header's structural bit (byte 28 bit 0) and raise <see cref="ReindexNeeded"/>.
    /// This never leaves a valid-looking-but-stale index a subsequent write or a VFP <c>USE</c> could trust,
    /// and — unlike the in-memory flag — survives disposal of a short-lived per-statement writer. Best-effort:
    /// if the sidecar cannot be deleted (locked), the cleared header bit + <see cref="ReindexNeeded"/> still
    /// signal the rebuild.
    /// </summary>
    private void InvalidateStructuralCdxForMaintenance() => InvalidateStructuralCdx();

    /// <summary>Test seam: force the NEXT maintenance pass to throw so the crash-safety backstop can be proven.</summary>
    private void ThrowIfInjectedFault()
    {
        if (FailIndexMaintenanceForTests)
            throw new InvalidOperationException("Injected index-maintenance fault (test seam).");
    }

    /// <summary>Open the structural <c>.cdx</c> read/write, sharing with any concurrent reader (the microVFP
    /// work area keeps its own read handle open across a write).</summary>
    private FileStream OpenCdxReadWrite()
        => new(ExistingCdxPath(), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

    /// <summary>
    /// Resolve (and cache) the compiled key/FOR computer for <paramref name="tag"/> from its on-disk header,
    /// or <see langword="null"/> when the tag CANNOT be incrementally maintained faithfully — then the caller
    /// physically invalidates the sidecar (never silently stale).
    /// </summary>
    /// <remarks>
    /// A tag is "un-derivable" when its KEY or FOR expression references a field that does NOT exist in the
    /// writer's PHYSICAL <c>.dbf</c> schema (the canonical DBC long-name case: a tag KEY <c>category_id</c> or
    /// <c>UPPER(product_name)</c> over the 10-char-truncated physical names <c>CATEGORY_I</c> /
    /// <c>PRODUCT_NA</c>), when its inferred key type is Unknown, when the KEY expression cannot be compiled,
    /// or when the derived key LENGTH disagrees with the stored tag header. Deriving a key for such a tag would
    /// yield WRONG (typically all-blank) bytes and silently corrupt the tree — so we reject it. The check is
    /// RESOLVE-aware (every referenced field must be present), NOT merely length-based: a same-length field
    /// coincidence (an unresolvable C(10) name over a real C(10) column) no longer slips through.
    /// </remarks>
    private CdxIndexBuilder.TagKeyComputer? GetComputer(CdxTreeEditor.TagHandle tag)
    {
        _tagComputers ??= new Dictionary<string, CdxIndexBuilder.TagKeyComputer?>(StringComparer.OrdinalIgnoreCase);
        if (_tagComputers.TryGetValue(tag.Name, out var cached))
            return cached;

        CdxHeader hdr = tag.Header;
        CdxIndexBuilder.TagKeyComputer? computer = null;
        try
        {
            string? forExpr = hdr.HasFor && hdr.ForExpression.Length > 0 ? hdr.ForExpression : null;

            // RESOLVE-aware guard (finding 2): every field the KEY and FOR expressions read must exist in the
            // physical schema, else our derivation silently produces wrong key bytes regardless of any length
            // coincidence. Checked BEFORE building the computer so an un-derivable DBC long-name tag can never
            // be mistaken for derivable just because its width happens to match.
            if (FieldsResolve(hdr.KeyExpression) && FieldsResolve(forExpr))
            {
                var def = new CdxTagDefinition(tag.Name, hdr.KeyExpression, forExpr, hdr.Descending, hdr.SortOrder, hdr.IsUnique);

                // No ambient EvaluationContext on the raw write path → VFP-neutral defaults (EXACT/ANSI OFF), the
                // same defaults the bulk builder uses when CreateTag/Reindex run without a context.
                var c = CdxIndexBuilder.CreateKeyComputer(_schema, def, evalContext: null);
                if (c.KeyLen == hdr.KeyLength)
                    computer = c;   // derived key length matches the stored tag → safe to edit incrementally
            }
        }
        catch
        {
            computer = null;    // unparseable/unsupported KEY expression → defer (invalidate)
        }

        _tagComputers[tag.Name] = computer;
        return computer;
    }

    /// <summary>True when EVERY field referenced by <paramref name="expr"/> exists in this writer's physical
    /// schema (an empty / null expression trivially resolves). An unparseable expression does not resolve.
    /// This is the resolve-aware core of the un-derivable check (finding 2).</summary>
    private bool FieldsResolve(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return true;
        IReadOnlyCollection<string> fields;
        try { fields = VfpExpression.Parse(expr).ReferencedFields(); }
        catch { return false; }   // cannot even parse → not derivable
        foreach (var f in fields)
            if (!_schema.Columns.Any(c => string.Equals(c.Name, f, StringComparison.OrdinalIgnoreCase)))
                return false;
        return true;
    }
}
