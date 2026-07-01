using System.Linq;
using CrossVault.FoxDbf.Query;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// §D8 — the <see cref="RecordBitmap"/> primitive (CodeBase m4map F4FLAG bitmaps).
/// EDGE / ADVERSARIAL: word-boundary ranges, clamping, reversed (empty) ranges,
/// the cheap NOT flip, And/Or set algebra and Enumerate ordering. These are pure
/// in-memory algebra tests — no files.
/// </summary>
public sealed class RecordBitmapTests
{
    [Fact]
    public void New_Bitmap_IsAllClear()
    {
        var b = new RecordBitmap(100);
        Assert.Equal(100, b.RecordCount);
        Assert.Equal(0, b.Count);
        Assert.Empty(b.Enumerate());
        Assert.False(b.Get(0));
        Assert.False(b.Get(99));
    }

    [Fact]
    public void SetRange_MarksInclusiveRun_AndEnumeratesAscending()
    {
        var b = new RecordBitmap(10);
        b.SetRange(2, 5);
        Assert.Equal(4, b.Count);
        Assert.Equal(new[] { 2, 3, 4, 5 }, b.Enumerate().ToArray());
        Assert.True(b.Get(2));
        Assert.True(b.Get(5));
        Assert.False(b.Get(1));
        Assert.False(b.Get(6));
    }

    [Fact]
    public void SetRange_CrossingWordBoundaries_CountsCorrectly()
    {
        // 200 bits spans >3 64-bit words; a range straddling several words must be exact.
        var b = new RecordBitmap(200);
        b.SetRange(60, 130);
        Assert.Equal(71, b.Count);
        Assert.Equal(Enumerable.Range(60, 71), b.Enumerate());
    }

    [Fact]
    public void SetRange_Reversed_SetsNothing()
    {
        var b = new RecordBitmap(10);
        b.SetRange(7, 3); // low > high → empty window (a contradiction)
        Assert.Equal(0, b.Count);
        Assert.Empty(b.Enumerate());
    }

    [Fact]
    public void SetRange_Clamps_OutOfBoundsEnds()
    {
        var b = new RecordBitmap(10);
        b.SetRange(-5, 100); // clamps to 0..9
        Assert.Equal(10, b.Count);
        Assert.Equal(Enumerable.Range(0, 10), b.Enumerate());
    }

    [Fact]
    public void SetAll_ThenNot_IsEmpty_AndViceVersa()
    {
        var all = new RecordBitmap(50);
        all.SetAll();
        Assert.Equal(50, all.Count);

        var none = all.Not();
        Assert.Equal(0, none.Count);

        var backAll = none.Not();
        Assert.Equal(50, backAll.Count);
        Assert.Equal(Enumerable.Range(0, 50), backAll.Enumerate());
    }

    [Fact]
    public void Not_FlipsExactly_OnPartialBitmap()
    {
        var b = new RecordBitmap(10);
        b.SetRange(2, 5);
        var n = b.Not();
        Assert.Equal(new[] { 0, 1, 6, 7, 8, 9 }, n.Enumerate().ToArray());
        Assert.Equal(6, n.Count);
        // The original is unchanged (And/Or/Not return new bitmaps).
        Assert.Equal(4, b.Count);
    }

    [Fact]
    public void And_Intersects()
    {
        var a = new RecordBitmap(20);
        a.SetRange(0, 9);
        var b = new RecordBitmap(20);
        b.SetRange(5, 14);
        var r = a.And(b);
        Assert.Equal(Enumerable.Range(5, 5), r.Enumerate()); // 5..9
    }

    [Fact]
    public void Or_Unions()
    {
        var a = new RecordBitmap(20);
        a.SetRange(0, 4);
        var b = new RecordBitmap(20);
        b.SetRange(10, 12);
        var r = a.Or(b);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 10, 11, 12 }, r.Enumerate().ToArray());
    }

    [Fact]
    public void And_OfDisjoint_IsEmpty()
    {
        var a = new RecordBitmap(64);
        a.SetRange(0, 9);
        var b = new RecordBitmap(64);
        b.SetRange(40, 49);
        Assert.Equal(0, a.And(b).Count);
    }

    [Fact]
    public void Set_SingleBits_TracksCountAndEnumeration()
    {
        var b = new RecordBitmap(70);
        b.Set(0);
        b.Set(63); // last bit of word 0
        b.Set(64); // first bit of word 1
        b.Set(0);  // idempotent
        Assert.Equal(3, b.Count);
        Assert.Equal(new[] { 0, 63, 64 }, b.Enumerate().ToArray());
    }
}
