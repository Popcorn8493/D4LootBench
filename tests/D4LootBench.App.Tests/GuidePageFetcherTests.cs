using D4LootBench.App.Services;
using Shouldly;

namespace D4LootBench.App.Tests;

public sealed class GuidePageFetcherTests
{
    [Fact]
    public void ParseCurlOutput_SplitsBodyAndStatus()
    {
        var (status, body) = GuidePageFetcher.ParseCurlOutput(
            "<html>page</html>" + GuidePageFetcher.StatusMarker + "200>>");

        status.ShouldBe(200);
        body.ShouldBe("<html>page</html>");
    }

    [Fact]
    public void ParseCurlOutput_ReportsErrorStatusWithTheBody()
    {
        var (status, body) = GuidePageFetcher.ParseCurlOutput(
            "Just a moment..." + GuidePageFetcher.StatusMarker + "403>>");

        status.ShouldBe(403);
        body.ShouldBe("Just a moment...");
    }

    [Fact]
    public void ParseCurlOutput_NoResponse_ReadsAsStatusZero()
    {
        // curl reports "000" when it never got an HTTP response (DNS/TLS failure, timeout).
        GuidePageFetcher.ParseCurlOutput(GuidePageFetcher.StatusMarker + "000>>").Status.ShouldBe(0);
    }

    [Fact]
    public void ParseCurlOutput_MissingMarker_ReturnsWholeOutputAsBodyWithStatusZero()
    {
        var (status, body) = GuidePageFetcher.ParseCurlOutput("partial output");

        status.ShouldBe(0);
        body.ShouldBe("partial output");
    }

    [Fact]
    public void ParseCurlOutput_UsesTheLastMarker()
    {
        // A page quoting the marker text can't fool the parser — the write-out suffix is last.
        string page = "before" + GuidePageFetcher.StatusMarker + "999>> after";
        var (status, body) = GuidePageFetcher.ParseCurlOutput(page + GuidePageFetcher.StatusMarker + "200>>");

        status.ShouldBe(200);
        body.ShouldBe(page);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><head><title>Just a moment...</title></head></html>", true)]
    [InlineData("<script src=\"/cdn-cgi/challenge-platform/h/g/orchestrate/chl_page/v1\"></script>", true)]
    [InlineData("<html><head><title>Whirlwind Barbarian Build</title></head><body>equipmentPriorityList</body></html>", false)]
    [InlineData("<html><body>403 Forbidden</body></html>", false)]
    public void Cloudflare_challenge_pages_are_recognized(string body, bool isChallenge)
    {
        GuidePageFetcher.IsCloudflareChallenge(body).ShouldBe(isChallenge);
    }
}
