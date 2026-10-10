using Finance.Gallery.Pages;

namespace Finance.Gallery.Shell;

/// <summary>The Gallery's pages, in sidebar order (pages sharing a group are listed together).</summary>
public static class PageRegistry
{
    public static IReadOnlyList<ModulePage> Create() => new ModulePage[]
    {
        new HomePage(),
        new LiveMarketPage(),
        new OrderBookPage(),
        new IndicatorExplorerPage(),
        new PortfolioOptimizerPage(),
        new VarExplorerPage(),
        new GreeksExplorerPage(),
        new StrategyBuilderPage(),
        new BacktestingStudioPage(),
        new AiAssistantPage(),
        new NewsIntelligencePage(),
        new SocialSentimentPage(),
    };
}
