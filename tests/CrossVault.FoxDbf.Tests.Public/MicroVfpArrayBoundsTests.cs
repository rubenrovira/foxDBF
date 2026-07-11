using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpArrayBoundsTests
{
    [Theory]
    [InlineData(1, 3)]
    [InlineData(0, 1)]
    [InlineData(0, 3)]
    [InlineData(3, 1)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    public void TwoDimensional_InvalidCoordinates_ReadFalseAndWriteNoOp(int row, int col)
    {
        var array = Seed2x2();

        var value = array.Get(row, col);
        array.Set(row, col, VfpValue.Integer(99));

        Assert.Equal(VfpType.Logical, value.Type);
        Assert.False(value.AsLogical);
        Assert2x2Unchanged(array);
    }

    [Fact]
    public void OneDimensional_SecondSubscript_ReadsFalseAndWritesNoOp()
    {
        var array = new VfpArray(3, 0);
        array.SetLinear(1, VfpValue.Integer(1));
        array.SetLinear(2, VfpValue.Integer(2));
        array.SetLinear(3, VfpValue.Integer(3));

        var value = array.Get(2, 1);
        array.Set(2, 1, VfpValue.Integer(99));

        Assert.Equal(VfpType.Logical, value.Type);
        Assert.False(value.AsLogical);
        Assert.Equal(1, array.GetLinear(1).AsInteger);
        Assert.Equal(2, array.GetLinear(2).AsInteger);
        Assert.Equal(3, array.GetLinear(3).AsInteger);
    }

    [Fact]
    public void TwoDimensional_SingleSubscript_RemainsLinearAcrossFullLength()
    {
        var array = Seed2x2();

        Assert.Equal(21, array.Get(3, null).AsInteger);
        array.Set(4, null, VfpValue.Integer(99));

        Assert.Equal(11, array.Get(1, 1).AsInteger);
        Assert.Equal(12, array.Get(1, 2).AsInteger);
        Assert.Equal(21, array.Get(2, 1).AsInteger);
        Assert.Equal(99, array.Get(2, 2).AsInteger);
    }

    private static VfpArray Seed2x2()
    {
        var array = new VfpArray(2, 2);
        array.Set(1, 1, VfpValue.Integer(11));
        array.Set(1, 2, VfpValue.Integer(12));
        array.Set(2, 1, VfpValue.Integer(21));
        array.Set(2, 2, VfpValue.Integer(22));
        return array;
    }

    private static void Assert2x2Unchanged(VfpArray array)
    {
        Assert.Equal(11, array.Get(1, 1).AsInteger);
        Assert.Equal(12, array.Get(1, 2).AsInteger);
        Assert.Equal(21, array.Get(2, 1).AsInteger);
        Assert.Equal(22, array.Get(2, 2).AsInteger);
    }
}
