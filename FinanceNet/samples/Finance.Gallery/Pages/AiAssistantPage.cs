using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Finance.Core.Market;
using Finance.Gallery.Controls;
using Finance.Gallery.Shell;
using Finance.LLM;
using Finance.Pricing.Options;
using Finance.Risk;
using Finance.TimeSeries.Indicators;

namespace Finance.Gallery.Pages;

/// <summary>
/// Chat with a tool-calling LLM whose tools are real Finance.Net functions (option pricing, VaR,
/// technical snapshots). Configure via FINANCENET_LLM_KEYFILE / OPENAI_API_KEY / AZURE_OPENAI_* or the form.
/// </summary>
public sealed class AiAssistantPage : ModulePage
{
    public override string Title => "AI Financial Assistant";
    public override string Subtitle => "A tool-calling assistant (Finance.LLM) whose tools are live Finance.Net functions — option pricing, VaR and technical snapshots. Works with OpenAI, Azure OpenAI, Anthropic, Ollama and Hugging Face.";
    public override string Glyph => "✦";
    public override string Group => "Intelligence";

    private const string DemoQuestion =
        "I hold 1,000 shares of NVDA. Give me a quick technical snapshot, the 1-day 99% VaR of the position, and price a 3-month at-the-money put to hedge it (assume 45% vol, 4% rate). Keep it concise.";

    private readonly StackPanel _messages = new() { Spacing = 12 };
    private readonly ScrollViewer _scroll = new() { Height = 560 };
    private readonly TextBox _input = new() { PlaceholderText = "Ask about prices, risk, options…", AcceptsReturn = false };
    private readonly TextBlock _status = new() { FontSize = 11.5, Foreground = Palette.InkFaint, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _toolLog = new() { Spacing = 8 };
    private ComboBox _provider = null!;
    private TextBox _endpoint = null!, _model = null!, _key = null!;
    private FinancialAssistant? _assistant;
    private Task _ready = Task.CompletedTask;
    private bool _busy;

    protected override Control Build()
    {
        var env = LlmSettings.FromEnvironment();
        (var provRow, _provider) = Ui.Combo("Provider", Enum.GetNames<LlmProvider>(), (int)(env?.Provider ?? LlmProvider.OpenAI));
        (var endRow, _endpoint) = Ui.Field("Endpoint", "", env is null ? "https://… (Azure / compatible / Ollama)" : "configured via environment");
        (var modelRow, _model) = Ui.Field("Model / deployment", env?.Model ?? "gpt-4o-mini");
        (var keyRow, _key) = Ui.Field("API key", "", env is null ? "kept in memory only" : "configured via environment");
        _key.PasswordChar = '•';
        var connect = Ui.Button("Connect", () => Connect(ReadForm()));

        var settings = Ui.Card(Ui.Stack(12, provRow, endRow, modelRow, keyRow, connect, _status,
            Ui.Label("Tip: set FINANCENET_LLM_KEYFILE to a key file and the assistant connects at start-up.", 11, Palette.InkFaint)),
            "Model", "Bring your own provider");

        var tools = Ui.Card(Ui.Stack(8,
            ToolChip("get_technical_snapshot", "Price, SMA50/200 trend, RSI, ATR"),
            ToolChip("compute_var", "Historical / Cornish-Fisher VaR & ES"),
            ToolChip("price_option", "Black-Scholes price + Greeks"),
            new Border { Height = 1, Background = Palette.Line, Margin = new Thickness(0, 6) },
            Ui.Label("Tool calls this session", 11.5, Palette.InkFaint, FontWeight.SemiBold),
            _toolLog), "Tools", "Registered Finance.Net functions");

        _scroll.Content = _messages;
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) _ = SendAsync(_input.Text); };
        var send = Ui.Button("Send", () => _ = SendAsync(_input.Text));
        var inputRow = new DockPanel();
        DockPanel.SetDock(send, Dock.Right);
        send.Margin = new Thickness(10, 0, 0, 0);
        inputRow.Children.Add(send);
        inputRow.Children.Add(_input);

        var suggestions = new WrapPanel();
        foreach (var q in new[]
                 {
                     "Price a 6-month 110 call on a 100 stock, 30% vol, 4% rate",
                     "What is the 95% VaR of 500 shares of AAPL?",
                     "Is TSLA overbought right now?",
                 })
        {
            var b = new Button { Content = q, FontSize = 11.5, Margin = new Thickness(0, 0, 8, 8) };
            b.Classes.Add("ghost");
            b.Click += (_, _) => _ = SendAsync(q);
            suggestions.Children.Add(b);
        }

        var chat = Ui.Card(Ui.Stack(12, _scroll, suggestions, inputRow), "Conversation", "Answers are grounded in tool results computed locally");

        AddBubble("assistant", "Hi — I'm the Finance.Net assistant. I call real library functions for numbers instead of guessing them. Connect a model on the left, then ask away.", null);
        if (env is not null) Connect(env);
        else _status.Text = "No model configured.";
        return Ui.Grid(16, new[] { "290", "*", "280" }, new Control[] { settings, chat, tools });
    }

    public override Task WhenReadyAsync()
    {
        // The screenshot runner asks one demo question when a model is configured.
        if (_assistant is not null && _messages.Children.Count <= 1) _ready = SendAsync(DemoQuestion);
        return _ready;
    }

    private LlmSettings ReadForm() => new()
    {
        Provider = Enum.Parse<LlmProvider>((string)_provider.SelectedItem!),
        Endpoint = string.IsNullOrWhiteSpace(_endpoint.Text) ? null : _endpoint.Text.Trim(),
        Model = string.IsNullOrWhiteSpace(_model.Text) ? "gpt-4o-mini" : _model.Text.Trim(),
        ApiKey = string.IsNullOrWhiteSpace(_key.Text) ? null : _key.Text.Trim(),
    };

    private void Connect(LlmSettings settings)
    {
        try
        {
            _assistant = new FinancialAssistant(settings.CreateChatModel());
            RegisterTools(_assistant);
            _status.Text = $"Connected: {settings.Provider} · {settings.Model}";
            _status.Foreground = Palette.Up;
        }
        catch (Exception ex)
        {
            _assistant = null;
            _status.Text = ex.Message;
            _status.Foreground = Palette.Down;
        }
    }

    private static Bar[] BarsFor(string symbol)
    {
        // Deterministic per symbol so answers are reproducible offline.
        ulong seed = 1469598103934665603UL;
        foreach (char c in symbol.ToUpperInvariant()) seed = (seed ^ c) * 1099511628211UL;
        double start = 50 + seed % 400;
        return SyntheticMarket.GeometricBrownianBars(400, start, 0.12, 0.25 + seed % 30 / 100.0, seed);
    }

    private static void RegisterTools(FinancialAssistant a)
    {
        a.RegisterTool("get_technical_snapshot",
            "Latest close, 50/200-day SMA trend, RSI(14) and ATR(14) for a ticker (offline synthetic data in the Gallery).",
            """{"type":"object","properties":{"symbol":{"type":"string"}},"required":["symbol"]}""",
            args =>
            {
                string sym = args.GetProperty("symbol").GetString() ?? "SPY";
                var bars = BarsFor(sym);
                var close = bars.Select(b => b.Close).ToArray();
                var sma50 = Ta.Sma(close, 50);
                var sma200 = Ta.Sma(close, 200);
                var rsi = Ta.Rsi(close, 14);
                return JsonSerializer.Serialize(new
                {
                    symbol = sym,
                    close = Math.Round(close[^1], 2),
                    sma50 = Math.Round(sma50[^1], 2),
                    sma200 = Math.Round(sma200[^1], 2),
                    trend = sma50[^1] > sma200[^1] ? "uptrend (golden cross regime)" : "downtrend (death cross regime)",
                    rsi14 = Math.Round(rsi[^1], 1),
                    change20d = Math.Round(close[^1] / close[^21] - 1, 4),
                    data = "synthetic",
                });
            });

        a.RegisterTool("compute_var",
            "1-day Value-at-Risk and Expected Shortfall of a position, from the ticker's daily return history.",
            """{"type":"object","properties":{"symbol":{"type":"string"},"shares":{"type":"number"},"confidence":{"type":"number","description":"e.g. 0.99"}},"required":["symbol","shares"]}""",
            args =>
            {
                string sym = args.GetProperty("symbol").GetString() ?? "SPY";
                double shares = args.GetProperty("shares").GetDouble();
                double conf = args.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0.99;
                var close = BarsFor(sym).Select(b => b.Close).ToArray();
                var rets = new double[close.Length - 1];
                for (int i = 1; i < close.Length; i++) rets[i - 1] = close[i] / close[i - 1] - 1;
                double value = shares * close[^1];
                var hist = ValueAtRisk.Historical(rets, conf);
                var cf = ValueAtRisk.CornishFisher(rets, conf);
                return JsonSerializer.Serialize(new
                {
                    symbol = sym,
                    positionValue = Math.Round(value, 2),
                    confidence = conf,
                    historicalVaR = Math.Round(hist.ValueAtRisk * value, 2),
                    historicalES = Math.Round(hist.ExpectedShortfall * value, 2),
                    cornishFisherVaR = Math.Round(cf.ValueAtRisk * value, 2),
                });
            });

        a.RegisterTool("price_option",
            "Black-Scholes-Merton price and Greeks of a European option.",
            """{"type":"object","properties":{"type":{"type":"string","enum":["call","put"]},"spot":{"type":"number"},"strike":{"type":"number"},"years":{"type":"number"},"volatility":{"type":"number"},"rate":{"type":"number"},"dividendYield":{"type":"number"}},"required":["type","spot","strike","years","volatility","rate"]}""",
            args =>
            {
                var type = args.GetProperty("type").GetString() == "put" ? OptionType.Put : OptionType.Call;
                double q = args.TryGetProperty("dividendYield", out var d) ? d.GetDouble() : 0;
                var g = BlackScholes.Greeks(type, args.GetProperty("spot").GetDouble(), args.GetProperty("strike").GetDouble(),
                    args.GetProperty("years").GetDouble(), args.GetProperty("rate").GetDouble(), q, args.GetProperty("volatility").GetDouble());
                return JsonSerializer.Serialize(new
                {
                    price = Math.Round(g.Price, 4), delta = Math.Round(g.Delta, 4), gamma = Math.Round(g.Gamma, 5),
                    vegaPerVolPoint = Math.Round(g.VegaPerPoint, 4), thetaPerDay = Math.Round(g.ThetaPerDay, 4),
                });
            });
    }

    private async Task SendAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || _busy) return;
        if (_assistant is null)
        {
            AddBubble("assistant", "Connect a model first (left panel).", null);
            return;
        }
        _busy = true;
        _input.Text = "";
        AddBubble("user", text, null);
        var thinking = AddBubble("assistant", "Thinking…", null);
        try
        {
            var reply = await _assistant.AskAsync(text);
            _messages.Children.Remove(thinking);
            AddBubble("assistant", reply.Text, reply.ToolCalls);
            foreach (var call in reply.ToolCalls)
                _toolLog.Children.Add(ToolChip(call.Name, call.ArgumentsJson.Length > 70 ? call.ArgumentsJson[..70] + "…" : call.ArgumentsJson));
            _status.Text = $"Last reply: {reply.Usage.PromptTokens} in / {reply.Usage.CompletionTokens} out tokens";
        }
        catch (Exception ex)
        {
            _messages.Children.Remove(thinking);
            AddBubble("assistant", "Request failed: " + ex.Message, null);
        }
        finally
        {
            _busy = false;
            Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    private Control AddBubble(string role, string text, IReadOnlyList<ToolInvocation>? calls)
    {
        bool user = role == "user";
        var body = Ui.Stack(8, Markdown.Render(text));
        if (calls is { Count: > 0 })
        {
            var chips = new WrapPanel();
            foreach (var c in calls)
            {
                var pill = Ui.Pill("ƒ " + c.Name, c.Failed ? Palette.DownColor : Palette.TealColor);
                pill.Margin = new Thickness(0, 0, 6, 4);
                chips.Children.Add(pill);
            }
            body.Children.Insert(0, chips);
        }
        var bubble = new Border
        {
            Background = user ? Palette.Brush(Palette.AccentColor, 60) : Palette.Raised,
            BorderBrush = user ? Palette.Brush(Palette.AccentColor, 140) : Palette.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10),
            MaxWidth = 720,
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = body,
        };
        _messages.Children.Add(bubble);
        return bubble;
    }

    private static Control ToolChip(string name, string detail) => new Border
    {
        Background = Palette.Raised,
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10, 7),
        Child = Ui.Stack(2,
            new TextBlock { Text = "ƒ " + name, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Palette.Teal },
            new TextBlock { Text = detail, FontSize = 11, Foreground = Palette.InkFaint, TextWrapping = TextWrapping.Wrap }),
    };
}
