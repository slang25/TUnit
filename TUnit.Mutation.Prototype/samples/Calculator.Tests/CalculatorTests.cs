using Calculator;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Calculator.Tests;

public class Math2Tests
{
    [Test]
    public async Task Add_TwoPositives_ReturnsSum()
    {
        await Assert.That(Math2.Add(2, 3)).IsEqualTo(5);
    }

    [Test]
    public async Task Sub_LeftMinusRight()
    {
        await Assert.That(Math2.Sub(10, 4)).IsEqualTo(6);
    }

    [Test]
    public async Task Max_FirstIsLarger()
    {
        await Assert.That(Math2.Max(7, 3)).IsEqualTo(7);
    }

    [Test]
    public async Task Max_SecondIsLarger()
    {
        await Assert.That(Math2.Max(2, 9)).IsEqualTo(9);
    }

    [Test]
    public async Task IsPositive_True()
    {
        await Assert.That(Math2.IsPositive(5)).IsTrue();
    }

    [Test]
    public async Task IsPositive_False()
    {
        await Assert.That(Math2.IsPositive(-1)).IsFalse();
    }

    [Test]
    public async Task IsPositive_Zero_IsFalse()
    {
        await Assert.That(Math2.IsPositive(0)).IsFalse();
    }

    [Test]
    public async Task Abs_Negative_ReturnsPositive()
    {
        await Assert.That(Math2.Abs(-9)).IsEqualTo(9);
    }

    [Test]
    public async Task Abs_Positive_Unchanged()
    {
        await Assert.That(Math2.Abs(4)).IsEqualTo(4);
    }

    [Test]
    public async Task Answer_Is42()
    {
        await Assert.That(Math2.Answer).IsEqualTo(42);
    }
}
