using EtherCAT.NET.Engine.Esi;

namespace EtherCAT.NET.Engine.Tests.Esi;

public class EsiNumberTests
{
    [Theory]
    [InlineData("#x0000066F", 0x066Ful)]
    [InlineData("#x60380000", 0x60380000ul)]
    [InlineData("#x1a00", 0x1a00ul)]
    [InlineData("#X1A00", 0x1a00ul)]
    [InlineData("#x00", 0ul)]
    [InlineData("0x10", 0x10ul)]
    [InlineData("16", 16ul)]
    [InlineData("0", 0ul)]
    [InlineData("256", 256ul)]
    public void Parse_handles_both_hash_hex_and_decimal_formats(string text, ulong expected)
    {
        Assert.Equal(expected, EsiNumber.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    [InlineData("not-a-number")]
    [InlineData("#x")]
    public void TryParse_returns_false_for_invalid_input(string? text)
    {
        Assert.False(EsiNumber.TryParse(text, out _));
    }

    [Fact]
    public void Parse_throws_FormatException_for_invalid_input()
    {
        Assert.Throws<FormatException>(() => EsiNumber.Parse("not-a-number"));
    }
}
