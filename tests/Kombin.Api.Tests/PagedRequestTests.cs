using Kombin.Api.Common.Pagination;

namespace Kombin.Api.Tests;

public class PagedRequestTests
{
    [Theory]
    [InlineData(1, 20, 1, 20, 0)]
    [InlineData(3, 10, 3, 10, 20)]
    [InlineData(0, 10, 1, 10, 0)]
    [InlineData(-4, 0, 1, 1, 0)]
    [InlineData(2, 1000, 2, 100, 100)]
    public void ValuesAreClamped(int page, int pageSize, int expectedPage, int expectedPageSize, int expectedSkip)
    {
        var request = new PagedRequest(page, pageSize);

        Assert.Equal(expectedPage, request.EffectivePage);
        Assert.Equal(expectedPageSize, request.EffectivePageSize);
        Assert.Equal(expectedSkip, request.Skip);
    }

    [Fact]
    public void ResponseReportsTotalPages()
    {
        var response = PagedResponse<int>.Create([1, 2, 3], new PagedRequest(1, 3), totalCount: 7);

        Assert.Equal(3, response.TotalPages);
        Assert.Equal(3, response.PageSize);
        Assert.Equal(7, response.TotalCount);
    }
}
