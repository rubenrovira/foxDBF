using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.Tests;

public sealed class VfpValueNumericSafetyTests
{
    [Fact]
    public void AsNumber_DoubleBackedNonFiniteAndHugeValues_AreSafe()
    {
        Assert.Equal(0m, VfpValue.Number(double.NaN).AsNumber);
        Assert.Equal(decimal.MaxValue, VfpValue.Number(double.PositiveInfinity).AsNumber);
        Assert.Equal(decimal.MinValue, VfpValue.Number(double.NegativeInfinity).AsNumber);
        Assert.Equal(decimal.MaxValue, VfpValue.Number(1e300).AsNumber);
        Assert.Equal(decimal.MinValue, VfpValue.Number(-1e300).AsNumber);
        Assert.Equal(12.5m, VfpValue.Number(12.5d).AsNumber);
        Assert.Equal(decimal.MaxValue, VfpValue.Number(decimal.MaxValue).AsNumber);
        Assert.Equal(decimal.MinValue, VfpValue.Number(decimal.MinValue).AsNumber);
    }

    [Fact]
    public void AsInteger_NonFiniteOutOfRangeAndFractionalValues_AreSafe()
    {
        Assert.Equal(0, VfpValue.Number(double.NaN).AsInteger);
        Assert.Equal(int.MaxValue, VfpValue.Number(double.PositiveInfinity).AsInteger);
        Assert.Equal(int.MinValue, VfpValue.Number(double.NegativeInfinity).AsInteger);
        Assert.Equal(int.MaxValue, VfpValue.Number(1e300).AsInteger);
        Assert.Equal(int.MinValue, VfpValue.Number(-1e300).AsInteger);
        Assert.Equal(int.MaxValue, VfpValue.Number(decimal.MaxValue).AsInteger);
        Assert.Equal(int.MinValue, VfpValue.Number(decimal.MinValue).AsInteger);
        Assert.Equal(12, VfpValue.Number(12.9m).AsInteger);
        Assert.Equal(-12, VfpValue.Number(-12.9d).AsInteger);
    }

    [Fact]
    public void NumericToString_UsesInvariantNativeBacking()
    {
        Assert.Equal("12.5", VfpValue.Number(12.5m).ToString());
        Assert.Equal(decimal.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            VfpValue.Number(decimal.MaxValue).ToString());
        Assert.Equal("12.5", VfpValue.Number(12.5d).ToString());
        Assert.Equal("NaN", VfpValue.Number(double.NaN).ToString());
        Assert.Equal("Infinity", VfpValue.Number(double.PositiveInfinity).ToString());
        Assert.Equal("-Infinity", VfpValue.Number(double.NegativeInfinity).ToString());
        Assert.Equal("1E+300", VfpValue.Number(1e300).ToString());
        Assert.Equal("-0", VfpValue.Number(double.NegativeZero).ToString());
        Assert.Equal("42", VfpValue.Integer(42).ToString());
        Assert.Equal("12.50", VfpValue.Currency(12.50m).ToString());
    }

    [Fact]
    public void HugePower_RemainsInspectable_InEvaluateAndCompile()
    {
        var parsed = VfpExpression.Parse("10^400");

        var interpreted = parsed.Evaluate(TestRow.Empty);
        var compiled = parsed.Compile()(TestRow.Empty);

        Assert.Equal(VfpType.Numeric, interpreted.Type);
        Assert.Equal("Infinity", interpreted.ToString());
        Assert.Equal(decimal.MaxValue, interpreted.AsNumber);
        Assert.Equal(VfpType.Numeric, compiled.Type);
        Assert.Equal("Infinity", compiled.ToString());
        Assert.Equal(decimal.MaxValue, compiled.AsNumber);
    }

    [Fact]
    public void SpaceHugeLiteral_InferTypeSaturatesWithoutAllocating()
    {
        var type = VfpExpression.Parse("SPACE(99999999999)").InferType(new TestSchema());

        Assert.Equal(VfpType.Character, type.Type);
        Assert.Equal(int.MaxValue, type.Length);
    }

    [Fact]
    public void ToClr_PreservesNumericBacking()
    {
        object? exact = VfpValue.Number(decimal.MaxValue).ToClr();
        object? floating = VfpValue.Number(1e300).ToClr();

        Assert.IsType<decimal>(exact);
        Assert.Equal(decimal.MaxValue, exact);
        Assert.IsType<double>(floating);
        Assert.Equal(1e300, floating);
    }

    [Fact]
    public void NumericEquality_RemainsValueBasedAcrossBackingKinds()
    {
        Assert.Equal(VfpValue.Number(12.5m), VfpValue.Number(12.5d));
        Assert.NotEqual(VfpValue.Number(12.5m), VfpValue.Number(12.6d));
    }
}
