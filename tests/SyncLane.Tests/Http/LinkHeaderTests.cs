using SyncLane.Http;

namespace SyncLane.Tests.Http;

public class LinkHeaderTests
{
    [Theory]
    [InlineData("""<https://a.example/x?page=2>; rel="next", <https://a.example/x?page=9>; rel="last" """, "https://a.example/x?page=2")]
    [InlineData("""<https://a.example/x?page=1>; rel="prev", <https://a.example/x?page=3>; rel=next""", "https://a.example/x?page=3")]
    [InlineData("""<https://a.example/x?page=3>; title="Next"; rel="last next" """, "https://a.example/x?page=3")]
    [InlineData("""<https://a.example/x?a=1,2>; rel="next" """, "https://a.example/x?a=1,2")]
    [InlineData("""<https://a.example/x?page=9>; rel="last" """, null)]
    [InlineData("", null)]
    public void The_next_link_is_found_among_the_others(string header, string? expected)
    {
        Assert.Equal(expected, LinkHeader.Next([header]));
    }

    [Fact]
    public void Links_may_come_in_separate_header_values()
    {
        Assert.Equal("https://a.example/2", LinkHeader.Next(["""<https://a.example/1>; rel="prev" """, """<https://a.example/2>; rel="next" """]));
    }
}
