using Berichtsheft.Core.Services;
using Xunit;

namespace Berichtsheft.Tests.Services;

// Ported from tests/test_features_v12.py::test_wrap_text
public class LineWrapTests
{
    [Fact]
    public void SplitsAtWordBoundary()
    {
        Assert.Equal(new[] { "aaa bbb", "ccc" }, LineWrap.WrapText("aaa bbb ccc", 7));
    }

    [Fact]
    public void HardBreaksVeryLongWord()
    {
        Assert.Equal(new[] { "xxxxx", "xxxxx", "xx" }, LineWrap.WrapText(new string('x', 12), 5));
    }

    [Fact]
    public void EmptyTextGivesSingleEmptyLine()
    {
        Assert.Equal(new[] { "" }, LineWrap.WrapText("", 10));
    }

    [Fact]
    public void ShortTextFitsOnOneLine()
    {
        Assert.Equal(new[] { "kurz" }, LineWrap.WrapText("kurz", 10));
    }
}
