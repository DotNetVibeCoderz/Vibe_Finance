using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Finance.Core;

namespace Finance.Gallery.Shell;

/// <summary>Sidebar navigation + header + page host.</summary>
public sealed class MainWindow : Window
{
    private readonly ContentControl _host = new();
    private readonly TextBlock _title = new() { FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Palette.Ink };
    private readonly TextBlock _subtitle = new() { FontSize = 12.5, Foreground = Palette.InkSoft, TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<ModulePage, Button> _nav = new();
    private ModulePage? _current;

    public MainWindow()
    {
        Title = "Finance.Net Gallery";
        Width = 1440;
        Height = 900;
        MinWidth = 1100;
        MinHeight = 700;
        Background = Palette.Ground;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Finance.Gallery/Assets/icon-512.png")));
        }
        catch (Exception)
        {
            // Icon is cosmetic.
        }

        Pages = PageRegistry.Create();
        Content = BuildLayout();
        Navigate(Pages[0]);
    }

    public IReadOnlyList<ModulePage> Pages { get; }

    public ModulePage? CurrentPage => _current;

    private Control BuildLayout()
    {
        var sidebar = new StackPanel { Spacing = 2, Margin = new Thickness(12, 0, 12, 12) };
        string? group = null;
        foreach (var page in Pages)
        {
            if (page.Group != group)
            {
                group = page.Group;
                sidebar.Children.Add(new TextBlock
                {
                    Text = group.ToUpperInvariant(),
                    FontSize = 10,
                    LetterSpacing = 1.2,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Palette.InkFaint,
                    Margin = new Thickness(12, 16, 0, 6),
                });
            }
            var glyph = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(7),
                Background = Palette.Raised,
                Child = new TextBlock { Text = page.Glyph, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Teal },
            };
            var btn = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { glyph, new TextBlock { Text = page.Title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center } },
                },
            };
            btn.Classes.Add("nav");
            var target = page;
            btn.Click += (_, _) => Navigate(target);
            _nav[page] = btn;
            sidebar.Children.Add(btn);
        }

        Bitmap? logo = null;
        try { logo = new Bitmap(AssetLoader.Open(new Uri("avares://Finance.Gallery/Assets/icon-512.png"))); }
        catch (Exception) { }

        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(22, 20, 12, 6),
            Children =
            {
                new Image { Source = logo, Width = 34, Height = 34 },
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = "Finance.Net", FontSize = 16, FontWeight = FontWeight.Bold, Foreground = Palette.Ink },
                        new TextBlock { Text = $"Gallery · v{FinanceInfo.Version}", FontSize = 11, Foreground = Palette.InkFaint },
                    },
                },
            },
        };

        var credit = new TextBlock
        {
            Text = FinanceInfo.CreditIndonesian,
            FontSize = 10.5,
            Foreground = Palette.InkFaint,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(22, 10, 18, 16),
        };

        var side = new DockPanel { Background = Palette.Panel, Width = 248 };
        DockPanel.SetDock(brand, Dock.Top);
        DockPanel.SetDock(credit, Dock.Bottom);
        side.Children.Add(brand);
        side.Children.Add(credit);
        side.Children.Add(new ScrollViewer { Content = sidebar });

        var header = new Border
        {
            Padding = new Thickness(28, 20, 28, 14),
            Child = new StackPanel { Spacing = 3, Children = { _title, _subtitle } },
        };
        var main = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        main.Children.Add(header);
        main.Children.Add(new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(28, 4, 28, 28), Child = _host },
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        });

        var root = new DockPanel();
        var divider = new Border { Width = 1, Background = Palette.Line };
        DockPanel.SetDock(side, Dock.Left);
        DockPanel.SetDock(divider, Dock.Left);
        root.Children.Add(side);
        root.Children.Add(divider);
        root.Children.Add(main);
        return root;
    }

    public void Navigate(ModulePage page)
    {
        if (_current == page) return;
        _current?.OnHidden();
        if (_current is not null) _nav[_current].Classes.Remove("active");
        _current = page;
        _nav[page].Classes.Add("active");
        _title.Text = page.Title;
        _subtitle.Text = page.Subtitle;
        page.EnsureBuilt();
        _host.Content = page;
        page.OnShown();
    }
}
