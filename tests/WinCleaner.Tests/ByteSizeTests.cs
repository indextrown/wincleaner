using WinCleaner.Core;

namespace WinCleaner.Tests;

public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(17_824_112_640, "16.6 GB")]
    [InlineData(1_099_511_627_776, "1 TB")]
    public void Format_uses_1024_based_units(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes));
    }

    [Fact]
    public void Format_rejects_negative_size()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ByteSize.Format(-1));
    }
}
