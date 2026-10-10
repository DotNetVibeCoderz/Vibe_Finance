// Executes every .NET Interactive notebook (.dib) in notebooks/ the way the notebook kernel does:
// each C# cell is a Roslyn script submission chained onto the previous one. #i / #r directives are
// skipped because the Finance.Net assemblies are referenced directly from this tool's output folder.
//
// Usage: dotnet run --project tools/NotebookCheck -- [notebooks-dir] [--live] [--only 07] [--html out-dir]
//   --live   also runs cells marked "// optional-live" (network calls); skipped by default.
using System.Diagnostics;
using System.Reflection;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

var argList = args.ToList();
bool live = argList.Remove("--live");
string? only = TakeOption(argList, "--only");
string? htmlDir = TakeOption(argList, "--html");
string dir = argList.Count > 0 ? argList[0] : FindNotebooks();
if (htmlDir is not null) Directory.CreateDirectory(htmlDir);

// Load every Finance.Net assembly that sits next to this tool so scripts can reference them.
var references = new List<Assembly>();
foreach (var path in Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
{
    string name = Path.GetFileNameWithoutExtension(path);
    if (!(name.StartsWith("Finance.", StringComparison.Ordinal) || name.StartsWith("Gravicode.", StringComparison.Ordinal) || name.StartsWith("RLNet", StringComparison.Ordinal) || name.StartsWith("ILGPU", StringComparison.Ordinal))) continue;
    try { references.Add(Assembly.LoadFrom(path)); }
    catch (BadImageFormatException) { } // native libraries
}

var options = ScriptOptions.Default
    .AddReferences(references)
    .AddReferences(typeof(Enumerable).Assembly, typeof(System.Text.Json.JsonSerializer).Assembly, typeof(HttpClient).Assembly)
    .AddImports("System", "System.IO", "System.Linq", "System.Collections.Generic", "System.Threading.Tasks", "System.Text")
    .WithLanguageVersion(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Preview)
    .WithOptimizationLevel(Microsoft.CodeAnalysis.OptimizationLevel.Release);

var files = Directory.GetFiles(dir, "*.dib").OrderBy(f => f, StringComparer.Ordinal)
    .Where(f => only is null || Path.GetFileName(f).StartsWith(only, StringComparison.Ordinal)).ToList();
if (files.Count == 0)
{
    Console.Error.WriteLine($"No notebooks found in {dir}");
    return 2;
}

int failures = 0;
foreach (var file in files)
{
    var cells = Dib.CSharpCells(File.ReadAllText(file));
    var globals = new NotebookGlobals(htmlDir is null ? null : Path.Combine(htmlDir, Path.GetFileNameWithoutExtension(file)));
    var sw = Stopwatch.StartNew();
    ScriptState<object>? state = null;
    int ran = 0, skipped = 0;
    string? error = null;
    for (int i = 0; i < cells.Count; i++)
    {
        var code = cells[i];
        if (!live && code.Contains("// optional-live", StringComparison.Ordinal)) { skipped++; continue; }
        if (string.IsNullOrWhiteSpace(code)) continue;
        try
        {
            state = state is null
                ? await CSharpScript.RunAsync(code, options, globals, typeof(NotebookGlobals))
                : await state.ContinueWithAsync(code, options);
            ran++;
        }
        catch (CompilationErrorException ex)
        {
            error = $"cell {i + 1} does not compile:\n      " + string.Join("\n      ", ex.Diagnostics.Take(8));
            break;
        }
        catch (Exception ex)
        {
            error = $"cell {i + 1} threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}";
            break;
        }
    }
    sw.Stop();
    string tag = error is null ? "PASS" : "FAIL";
    Console.WriteLine($"{tag}  {Path.GetFileName(file),-28} {ran} cells run, {skipped} live skipped, {globals.Displays} displays, {sw.Elapsed.TotalSeconds,6:0.0}s");
    if (error is not null)
    {
        failures++;
        Console.WriteLine("      " + error);
    }
}

Console.WriteLine(failures == 0 ? $"All {files.Count} notebooks executed." : $"{failures} of {files.Count} notebooks failed.");
return failures == 0 ? 0 : 1;

static string? TakeOption(List<string> list, string name)
{
    int i = list.IndexOf(name);
    if (i < 0 || i + 1 >= list.Count) return null;
    string v = list[i + 1];
    list.RemoveRange(i, 2);
    return v;
}

static string FindNotebooks()
{
    for (var d = new DirectoryInfo(Environment.CurrentDirectory); d is not null; d = d.Parent)
    {
        var candidate = Path.Combine(d.FullName, "notebooks");
        if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "*.dib").Length > 0) return candidate;
    }
    return "notebooks";
}

/// <summary>Minimal .dib reader: cells start with a "#!kernel" line.</summary>
static class Dib
{
    public static List<string> CSharpCells(string text)
    {
        var cells = new List<string>();
        string? kernel = null;
        var current = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("#!", StringComparison.Ordinal) && raw.Length > 2 && char.IsLetter(raw[2]) && !raw.Contains(' '))
            {
                Flush();
                kernel = raw[2..].Trim();
                continue;
            }
            current.Add(raw);
        }
        Flush();
        return cells;

        void Flush()
        {
            if (kernel is "csharp" or "c#")
            {
                var code = current.Where(l => !(l.TrimStart().StartsWith("#i ", StringComparison.Ordinal) || l.TrimStart().StartsWith("#r ", StringComparison.Ordinal)));
                cells.Add(string.Join('\n', code));
            }
            current.Clear();
        }
    }
}

/// <summary>Stand-in for the notebook kernel's HTML content.</summary>
public sealed record HtmlContent(string Html)
{
    public override string ToString() => Html;
}

/// <summary>Globals visible to notebook code: the kernel's <c>display</c> and <c>HTML</c> helpers.</summary>
public sealed class NotebookGlobals(string? htmlPrefix)
{
    public int Displays { get; private set; }

    public HtmlContent HTML(string html) => new(html);

    public object? display(object? value)
    {
        Displays++;
        switch (value)
        {
            case HtmlContent h:
                if (htmlPrefix is not null) File.WriteAllText($"{htmlPrefix}-{Displays:00}.html", h.Html);
                Console.WriteLine($"      [html {h.Html.Length:N0} chars]");
                break;
            case null:
                Console.WriteLine("      null");
                break;
            default:
                var s = value.ToString() ?? "";
                foreach (var line in s.Split('\n').Take(6)) Console.WriteLine("      " + line.TrimEnd());
                break;
        }
        return value;
    }
}
