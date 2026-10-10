// Renders the Finance.Net package icon (assets/icon.png, assets/icon-512.png).
// Usage: dotnet run --project tools/IconGen -- <output-directory>
using SkiaSharp;

string outDir = args.Length > 0 ? args[0] : "assets";
Directory.CreateDirectory(outDir);
Render(512, Path.Combine(outDir, "icon-512.png"));
Render(128, Path.Combine(outDir, "icon.png"));
Console.WriteLine($"Icons written to {Path.GetFullPath(outDir)}");

static void Render(int size, string file)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var c = surface.Canvas;
    c.Clear(SKColors.Transparent);
    float s = size / 512f;
    c.Scale(s);

    // Rounded tile with a deep navy -> indigo gradient.
    var tile = new SKRoundRect(new SKRect(16, 16, 496, 496), 108, 108);
    using (var bg = new SKPaint { IsAntialias = true })
    {
        bg.Shader = SKShader.CreateLinearGradient(new SKPoint(16, 16), new SKPoint(496, 496),
            new[] { SKColor.Parse("#0B1430"), SKColor.Parse("#1B2A6B"), SKColor.Parse("#3B1E7A") },
            new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp);
        c.DrawRoundRect(tile, bg);
    }

    c.Save();
    c.ClipRoundRect(tile, antialias: true);

    // Soft glow behind the chart.
    using (var glow = new SKPaint { IsAntialias = true })
    {
        glow.Shader = SKShader.CreateRadialGradient(new SKPoint(330, 190), 260,
            new[] { SKColor.Parse("#5EEAD4").WithAlpha(70), SKColors.Transparent }, SKShaderTileMode.Clamp);
        c.DrawRect(0, 0, 512, 512, glow);
    }

    // Faint grid.
    using (var grid = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(18), StrokeWidth = 2, Style = SKPaintStyle.Stroke })
    {
        for (int y = 120; y <= 420; y += 75) c.DrawLine(60, y, 452, y, grid);
    }

    // Candlesticks climbing left to right: (x, open, close, high, low).
    var candles = new (float X, float O, float C, float H, float L)[]
    {
        (110, 360, 330, 312, 392),
        (178, 335, 350, 300, 372),
        (246, 340, 282, 262, 356),
        (314, 290, 238, 214, 306),
        (382, 246, 170, 146, 262),
    };
    var up = SKColor.Parse("#34D399");
    var down = SKColor.Parse("#F87171");
    foreach (var k in candles)
    {
        bool bull = k.C < k.O; // y grows downward
        var col = bull ? up : down;
        using var wick = new SKPaint { IsAntialias = true, Color = col, StrokeWidth = 7, StrokeCap = SKStrokeCap.Round, Style = SKPaintStyle.Stroke };
        c.DrawLine(k.X, k.H, k.X, k.L, wick);
        using var body = new SKPaint { IsAntialias = true };
        body.Shader = SKShader.CreateLinearGradient(new SKPoint(k.X, Math.Min(k.O, k.C)), new SKPoint(k.X, Math.Max(k.O, k.C)),
            new[] { col, col.WithAlpha(200) }, SKShaderTileMode.Clamp);
        float top = Math.Min(k.O, k.C), bottom = Math.Max(k.O, k.C);
        c.DrawRoundRect(new SKRoundRect(new SKRect(k.X - 22, top, k.X + 22, bottom), 7, 7), body);
    }

    // Gold trend line with an arrow head.
    using (var path = new SKPath())
    {
        path.MoveTo(70, 410);
        path.CubicTo(170, 400, 220, 330, 290, 300);
        path.CubicTo(350, 275, 380, 190, 440, 112);
        using var shadow = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#FBBF24").WithAlpha(90), StrokeWidth = 26, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
        shadow.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10);
        c.DrawPath(path, shadow);
        using var line = new SKPaint { IsAntialias = true, StrokeWidth = 14, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
        line.Shader = SKShader.CreateLinearGradient(new SKPoint(70, 410), new SKPoint(440, 112),
            new[] { SKColor.Parse("#F59E0B"), SKColor.Parse("#FDE68A") }, SKShaderTileMode.Clamp);
        c.DrawPath(path, line);
    }
    using (var head = new SKPath())
    {
        head.MoveTo(462, 82);
        head.LineTo(408, 104);
        head.LineTo(448, 142);
        head.Close();
        using var fill = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#FDE68A") };
        c.DrawPath(head, fill);
    }

    c.Restore();

    // Thin highlight border.
    using (var border = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4 })
    {
        border.Shader = SKShader.CreateLinearGradient(new SKPoint(16, 16), new SKPoint(496, 496),
            new[] { SKColors.White.WithAlpha(90), SKColors.White.WithAlpha(10) }, SKShaderTileMode.Clamp);
        c.DrawRoundRect(tile, border);
    }

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.Create(file);
    data.SaveTo(fs);
}
