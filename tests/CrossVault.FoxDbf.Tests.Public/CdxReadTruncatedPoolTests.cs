using System;
using System.IO;
using System.Linq;
using System.Text;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// ROADMAP §6.6 — <see cref="IndexFile.ReadCdxHeader(long)"/> must FAIL CLOSED on a truncated KEY/FOR
/// expression pool. A legit CDX always stores its full pool inline right after the 512-byte header page;
/// when the declared pool extends past EOF (a partially-written / damaged sidecar) the reader previously
/// clamped and zero-filled the remainder, so a truncated tag parsed as a VALID but silently SHORTENED
/// expression. The fix returns <c>default</c> (tag unusable) instead — consistent with the reader's own
/// doctrine of never silently parsing zero-filled fields.
/// <para>
/// The test builds a real single-tag <c>.cdx</c> on a THROWAWAY temp copy, confirms the tag header parses
/// with its full pool present, then binary-truncates the file mid-pool and asserts the same header now reads
/// back as absent/invalid rather than mis-parsing.
/// </para>
/// </summary>
public sealed class CdxReadTruncatedPoolTests
{
    private static string FreshDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_cdxtrunc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ReadCdxHeader_TruncatedMidPool_TreatsTagAsAbsent_NotMisParsed()
    {
        string dir = FreshDir();
        try
        {
            string dbf = Path.Combine(dir, "t.dbf");
            using (var w = DbfWriter.Create(dbf, new[] { new DbfColumnDef("NAME", 'C', 10) },
                       new DbfCreateOptions { Overwrite = true }))
            {
                w.AppendRecord("alpha");
                w.AppendRecord("bravo");
                w.CreateTag(new CdxTagDefinition("T", "NAME"));
                w.Flush();
            }

            string cdx = Path.ChangeExtension(dbf, ".cdx");
            Assert.True(File.Exists(cdx));

            // Locate the per-tag header (KEY = "NAME") — a page-aligned header whose pool holds the expression.
            long tagOffset = -1;
            int poolTotal = 0;
            using (var idx = IndexFile.Open(cdx))
            {
                for (long off = IndexFile.PageSize; off + IndexFile.PageSize <= idx.Length; off += IndexFile.PageSize)
                {
                    var h = idx.ReadCdxHeader(off);
                    if (string.Equals(h.KeyExpression?.Trim(), "NAME", StringComparison.OrdinalIgnoreCase))
                    {
                        tagOffset = off;
                        poolTotal = h.KeyExprLength + h.ForExprLength;

                        // BASELINE: with the full pool present the header parses to a real, usable tag.
                        Assert.NotEqual(0u, h.Root);
                        break;
                    }
                }
            }

            Assert.True(tagOffset >= 0, "could not locate the NAME tag header");
            Assert.True(poolTotal >= 1, "the tag must declare a non-empty expression pool to truncate");

            // Binary-truncate the file so the tag's pool loses its last byte (mid-pool): the 512-byte header
            // page is fully present, but the declared pool now runs one byte past EOF.
            long truncatedLength = tagOffset + IndexFile.PageSize + (poolTotal - 1);
            using (var fs = new FileStream(cdx, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                fs.SetLength(truncatedLength);

            using (var idx = IndexFile.Open(cdx))
            {
                // The truncated tag must read back as absent/invalid (fail closed), NOT a shortened expression.
                var h = idx.ReadCdxHeader(tagOffset);
                Assert.Equal(0u, h.Root);
                Assert.True(string.IsNullOrEmpty(h.KeyExpression),
                    $"expected an empty (fail-closed) key expression, got '{h.KeyExpression}'");

                // Surgical: the intact FILE header at offset 0 (whose own pool is fully present) still parses
                // — fail-closed is scoped to the truncated tag, it does not blanket-reject the whole file.
                var fileHeader = idx.ReadCdxHeader(0);
                Assert.NotEqual(0u, fileHeader.Root);
            }
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// ROADMAP §6.6 (integration): the exact scenario the spec named — build a REAL multi-tag structural
    /// <c>.cdx</c> on a throwaway temp copy, binary-truncate the file so ONE tag's KEY/FOR pool is cut
    /// mid-way, then open it through <see cref="CdxFile.Open(string, DbfTable?)"/> with a REAL
    /// <see cref="DbfTable"/> (the path the <see cref="IndexFile.ReadCdxHeader(long)"/> unit test never drove
    /// through — so the null-key NRE in <see cref="CdxFile"/>.BuildDirectory slipped past it). Asserts that
    /// (a) the open does NOT throw (the class contract: "Never throws on a malformed index"),
    /// (b) every HEALTHY sibling tag stays enumerable AND seekable, and
    /// (c) the truncated tag is genuinely ABSENT (Tag(name) is null / not in TagNames), not mis-parsed into a
    /// silently shortened expression.
    /// </summary>
    [Fact]
    public void CdxOpen_OneTagTruncatedMidPool_HealthyTagSurvives_TruncatedTagAbsent()
    {
        string dir = FreshDir();
        try
        {
            string dbf = Path.Combine(dir, "m.dbf");
            var names = new[] { "alpha", "bravo", "carol" };
            var codes = new[] { "AA", "BB", "CC" };
            using (var w = DbfWriter.Create(dbf,
                       new[] { new DbfColumnDef("NAME", 'C', 10), new DbfColumnDef("CODE", 'C', 5) },
                       new DbfCreateOptions { Overwrite = true }))
            {
                for (int i = 0; i < names.Length; i++)
                    w.AppendRecord(names[i], codes[i]);
                w.CreateTag(new CdxTagDefinition("TNAME", "NAME"));
                w.CreateTag(new CdxTagDefinition("TCODE", "CODE"));
                w.Flush();
            }

            string cdx = Path.ChangeExtension(dbf, ".cdx");
            Assert.True(File.Exists(cdx));

            // Locate each tag's header by matching its KEY expression; capture its pool length so we can cut
            // exactly one byte short of a full pool.
            long nameOff = -1, codeOff = -1;
            int namePool = 0, codePool = 0;
            using (var idx = IndexFile.Open(cdx))
            {
                for (long off = IndexFile.PageSize; off + IndexFile.PageSize <= idx.Length; off += IndexFile.PageSize)
                {
                    var h = idx.ReadCdxHeader(off);
                    string key = h.KeyExpression?.Trim() ?? string.Empty;
                    if (nameOff < 0 && key.Equals("NAME", StringComparison.OrdinalIgnoreCase))
                    { nameOff = off; namePool = h.KeyExprLength + h.ForExprLength; }
                    else if (codeOff < 0 && key.Equals("CODE", StringComparison.OrdinalIgnoreCase))
                    { codeOff = off; codePool = h.KeyExprLength + h.ForExprLength; }
                }
            }
            Assert.True(nameOff > 0 && codeOff > 0, "both tag headers must be locatable");

            // Truncate the PHYSICALLY LAST tag (max header offset): the CDX lays tags down sequentially
            // (page0 header, page1 pool, page2 directory, then per-tag header+pool+B-tree), so cutting the
            // file tail leaves every EARLIER tag's full footprint (header + pool + tree pages) byte-intact.
            bool nameIsLast = nameOff > codeOff;
            long truncOff = nameIsLast ? nameOff : codeOff;
            int truncPool = nameIsLast ? namePool : codePool;
            string truncatedTag = nameIsLast ? "TNAME" : "TCODE";
            string healthyTag = nameIsLast ? "TCODE" : "TNAME";
            string[] healthyValues = nameIsLast ? codes : names;
            Assert.True(truncPool >= 1, "the truncated tag must declare a non-empty pool to cut mid-way");

            // Keep the full 512-byte header page; drop the last pool byte so the declared pool runs one byte
            // past EOF — the fail-closed case IndexFile.ReadCdxHeader now returns default() for.
            long truncatedLength = truncOff + IndexFile.PageSize + (truncPool - 1);
            using (var fs = new FileStream(cdx, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                fs.SetLength(truncatedLength);

            using var table = DbfTable.Open(dbf);
            using var index = CdxFile.Open(cdx, table);   // (a) MUST NOT throw — finding 1's NRE fired here.

            // (c) the truncated tag is genuinely ABSENT (fail-closed), not mis-parsed.
            Assert.Null(index.Tag(truncatedTag));
            Assert.DoesNotContain(index.TagNames, n => string.Equals(n, truncatedTag, StringComparison.OrdinalIgnoreCase));

            // (b) the healthy sibling tag survives: present, fully enumerable, and seekable through its B-tree.
            var tag = index.Tag(healthyTag);
            Assert.NotNull(tag);
            Assert.Contains(index.TagNames, n => string.Equals(n, healthyTag, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(names.Length, tag!.EnumerateEntries().Count());

            uint? hit = tag.Seek(Encoding.ASCII.GetBytes(healthyValues[0]));
            Assert.True(hit.HasValue, "the healthy tag must remain seekable after the sibling tag was truncated");
            Assert.Equal(1u, hit!.Value); // healthyValues[0] is the smallest key → record 1
        }
        finally { Cleanup(dir); }
    }
}
