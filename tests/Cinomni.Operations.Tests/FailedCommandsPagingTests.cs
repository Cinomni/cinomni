using Cinomni.Operations.Diagnostics;
using Xunit;

namespace Cinomni.Operations.Tests;

/// <summary>
/// Pure unit tests for the ceiling <c>GET /api/operations/commands/failed</c> applies to a caller's
/// <c>limit</c>. No database: this is the guarantee that the query string is capped rather than
/// trusted, independent of how much data happens to exist.
/// </summary>
public sealed class FailedCommandsPagingTests
{
    [Fact]
    public void No_limit_falls_back_to_the_default()
    {
        Assert.Equal(FailedCommandsPaging.DefaultLimit, FailedCommandsPaging.Clamp(null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1_000)]
    public void A_non_positive_limit_falls_back_to_the_default(int requested)
    {
        Assert.Equal(FailedCommandsPaging.DefaultLimit, FailedCommandsPaging.Clamp(requested));
    }

    [Theory]
    [InlineData(201)]
    [InlineData(10_000)]
    [InlineData(int.MaxValue)]
    public void A_limit_above_the_ceiling_is_capped_to_it(int requested)
    {
        Assert.Equal(FailedCommandsPaging.MaxLimit, FailedCommandsPaging.Clamp(requested));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(75)]
    [InlineData(200)]
    public void A_limit_inside_the_bounds_passes_through_unchanged(int requested)
    {
        Assert.Equal(requested, FailedCommandsPaging.Clamp(requested));
    }
}
