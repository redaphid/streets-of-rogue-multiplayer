using EightPlayers;
using Xunit;

public class ReflectCoerceTests
{
    [Fact]
    public void UnwrapsJsonQuotedString()
    {
        // rogue-gm#16: a pre-quoted JSON string must NOT keep its quotes.
        Assert.Equal("MAULER", ReflectCoerce.CoerceStringValue("\"MAULER\""));
    }

    [Fact]
    public void BareStringPassesThrough()
    {
        Assert.Equal("MAULER", ReflectCoerce.CoerceStringValue("MAULER"));
    }

    [Fact]
    public void UnwrapsJsonEscapes()
    {
        Assert.Equal("a\"b", ReflectCoerce.CoerceStringValue("\"a\\\"b\""));
    }

    [Fact]
    public void EmptyJsonStringUnwrapsToEmpty()
    {
        Assert.Equal("", ReflectCoerce.CoerceStringValue("\"\""));
    }

    [Fact]
    public void NumericLookingStringKeepsItsText()
    {
        // Assigning to a STRING member — the digits stay a string, unquoted.
        Assert.Equal("123", ReflectCoerce.CoerceStringValue("123"));
        Assert.Equal("123", ReflectCoerce.CoerceStringValue("\"123\""));
    }

    [Fact]
    public void MalformedQuotedFallsBackToRaw()
    {
        // Looks quoted but has an invalid JSON escape — keep the raw text
        // rather than throw. Raw value is the 4 chars: " \ x "
        var raw = "\"\\x\"";
        Assert.Equal(raw, ReflectCoerce.CoerceStringValue(raw));
    }

    [Fact]
    public void SingleCharAndNullAreSafe()
    {
        Assert.Equal("\"", ReflectCoerce.CoerceStringValue("\""));
        Assert.Null(ReflectCoerce.CoerceStringValue(null));
    }
}
