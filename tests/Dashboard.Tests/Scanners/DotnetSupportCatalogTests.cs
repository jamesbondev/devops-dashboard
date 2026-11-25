using Dashboard.Core.Scanners;

namespace Dashboard.Tests.Scanners;

public class DotnetSupportCatalogTests
{
    private static readonly DotnetSupportCatalog Today = new(new DateOnly(2026, 11, 25));

    [Theory]
    [InlineData("net10.0", TfmSupportStatus.Supported)]
    [InlineData("net10.0-windows", TfmSupportStatus.Supported)]
    [InlineData("NET10.0", TfmSupportStatus.Supported)]
    [InlineData("net9.0", TfmSupportStatus.Unsupported)]
    [InlineData("net8.0", TfmSupportStatus.Unsupported)]
    [InlineData("net8.0-android", TfmSupportStatus.Unsupported)]
    [InlineData("net7.0", TfmSupportStatus.Unsupported)]
    [InlineData("net6.0", TfmSupportStatus.Unsupported)]
    [InlineData("net5.0", TfmSupportStatus.Unsupported)]
    [InlineData("netcoreapp3.1", TfmSupportStatus.Unsupported)]
    [InlineData("netcoreapp2.1", TfmSupportStatus.Unsupported)]
    [InlineData("net48", TfmSupportStatus.Legacy)]
    [InlineData("net481", TfmSupportStatus.Legacy)]
    [InlineData("net462", TfmSupportStatus.Legacy)]
    [InlineData("net461", TfmSupportStatus.Unsupported)]
    [InlineData("net45", TfmSupportStatus.Unsupported)]
    [InlineData("net35", TfmSupportStatus.Legacy)]
    [InlineData("v4.7.2", TfmSupportStatus.Legacy)]
    [InlineData("netstandard2.0", TfmSupportStatus.Supported)]
    [InlineData("netstandard2.1", TfmSupportStatus.Supported)]
    [InlineData("netstandard1.6", TfmSupportStatus.Legacy)]
    [InlineData("net11.0", TfmSupportStatus.Unknown)]
    [InlineData("uap10.0", TfmSupportStatus.Unsupported)]
    [InlineData("banana", TfmSupportStatus.Unknown)]
    public void Status_as_of_2026_11_25(string tfm, TfmSupportStatus expected)
        => Assert.Equal(expected, Today.Evaluate(tfm).Status);

    [Fact]
    public void Net8_and_net9_were_supported_before_2026_11_10()
    {
        var before = new DotnetSupportCatalog(new DateOnly(2026, 11, 10));
        Assert.Equal(TfmSupportStatus.Supported, before.Evaluate("net8.0").Status);
        Assert.Equal(TfmSupportStatus.Supported, before.Evaluate("net9.0").Status);
        Assert.False(before.IsFlagged("net8.0"));
        var after = new DotnetSupportCatalog(new DateOnly(2026, 11, 11));
        Assert.True(after.IsFlagged("net8.0"));
        Assert.True(after.IsFlagged("net9.0"));
    }

    [Fact]
    public void Net10_ends_november_2028()
    {
        var info = Today.Evaluate("net10.0");
        Assert.Equal(new DateOnly(2028, 11, 10), info.EndOfSupport);
        Assert.Contains("LTS", info.Product, StringComparison.Ordinal);
        Assert.True(new DotnetSupportCatalog(new DateOnly(2028, 11, 11)).IsFlagged("net10.0"));
    }

    [Fact]
    public void Flagged_covers_unsupported_and_legacy_only()
    {
        Assert.True(Today.IsFlagged("net48"));
        Assert.True(Today.IsFlagged("net6.0"));
        Assert.False(Today.IsFlagged("net10.0"));
        Assert.False(Today.IsFlagged("net11.0"));
        Assert.False(Today.IsFlagged("netstandard2.0"));
    }

    [Theory]
    [InlineData("v4.7.2", "net472")]
    [InlineData("v4.8", "net48")]
    [InlineData(" net8.0-windows10.0.19041 ", "net8.0")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, DotnetSupportCatalog.Normalize(input));
}
