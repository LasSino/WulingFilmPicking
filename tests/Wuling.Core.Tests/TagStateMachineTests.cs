using Wuling.Core.Models;
using Wuling.Core.Tagging;

namespace Wuling.Core.Tests;

public sealed class TagStateMachineTests
{
    [Theory]
    [InlineData(PhotoTag.Unmarked, PhotoTag.Selected, true)]
    [InlineData(PhotoTag.Unmarked, PhotoTag.Rejected, true)]
    [InlineData(PhotoTag.Selected, PhotoTag.Rejected, true)]
    [InlineData(PhotoTag.Rejected, PhotoTag.Selected, true)]
    [InlineData(PhotoTag.Selected, PhotoTag.Unmarked, true)]
    [InlineData(PhotoTag.Rejected, PhotoTag.Unmarked, true)]
    [InlineData(PhotoTag.Unmarked, PhotoTag.Unmarked, false)]
    [InlineData(PhotoTag.Selected, PhotoTag.Selected, false)]
    [InlineData(PhotoTag.Rejected, PhotoTag.Rejected, false)]
    public void CanTransition_CoversAllPaths(PhotoTag from, PhotoTag to, bool expected)
    {
        Assert.Equal(expected, TagStateMachine.CanTransition(from, to));
    }

    [Fact]
    public void Apply_SetsTag_AndThrowsOnSelfTransition()
    {
        var pair = new PhotoPair { DisplayName = "A", JpegPath = "a.jpg" };
        Assert.Equal(PhotoTag.Unmarked, pair.Tag);

        TagStateMachine.Apply(pair, PhotoTag.Selected);
        Assert.Equal(PhotoTag.Selected, pair.Tag);

        Assert.Throws<InvalidOperationException>(() => TagStateMachine.Apply(pair, PhotoTag.Selected));
    }
}
