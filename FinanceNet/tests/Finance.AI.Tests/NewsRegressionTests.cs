using Finance.NewsAnalytics;

namespace Finance.AI.Tests;

/// <summary>Cases found while writing the notebooks: headlines the lexicon used to misread, and near-threshold duplicates.</summary>
public class NewsRegressionTests
{
    private static readonly LexiconSentimentModel Model = new();

    [Theory]
    [InlineData("Uncertainty grows as the Fed signals more hikes")]
    [InlineData("Losses widen at the struggling retailer")]
    [InlineData("Amazon faces lawsuit over alleged violations")]
    public void AdverseHeadlines_ScoreNegative(string headline)
    {
        Assert.True(Model.Score(headline).Score < 0, headline);
    }

    [Fact]
    public void PositiveDirection_StaysPositive()
    {
        Assert.True(Model.Score("Revenue grows 20% as demand surges").Score > 0.1);
    }

    [Fact]
    public void NearThresholdDuplicates_AreClustered()
    {
        var d = new DuplicateDetector();
        string a = "Apple shares rise after strong iPhone sales beat estimates";
        string b = "Apple shares rise after strong iPhone sales beat analyst estimates";
        double exact = d.Jaccard(a, b);
        Assert.True(exact >= 0.6, $"precondition: Jaccard {exact}");
        var now = DateTimeOffset.UtcNow;
        var groups = d.Cluster(new[] { new NewsItem(a, now), new NewsItem(b, now.AddMinutes(1)) });
        Assert.Single(groups);
    }
}
