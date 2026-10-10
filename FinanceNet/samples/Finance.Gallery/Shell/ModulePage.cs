using Avalonia.Controls;

namespace Finance.Gallery.Shell;

/// <summary>A Gallery module. Content is built lazily the first time the page is shown.</summary>
public abstract class ModulePage : UserControl
{
    private bool _built;

    /// <summary>Sidebar and header title.</summary>
    public abstract string Title { get; }

    /// <summary>One-line description shown under the title.</summary>
    public abstract string Subtitle { get; }

    /// <summary>Sidebar glyph (a single emoji-free symbol keeps the look consistent).</summary>
    public abstract string Glyph { get; }

    /// <summary>Sidebar group heading.</summary>
    public virtual string Group => "Modules";

    /// <summary>File name used by the screenshot runner.</summary>
    public virtual string Slug => new string(Title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    public void EnsureBuilt()
    {
        if (_built) return;
        _built = true;
        Content = Build();
    }

    protected abstract Control Build();

    /// <summary>Called when the page becomes visible.</summary>
    public virtual void OnShown() { }

    /// <summary>Called when the page is hidden (stop timers here).</summary>
    public virtual void OnHidden() { }

    /// <summary>Lets the screenshot runner wait for async content (network, LLM) before capturing.</summary>
    public virtual Task WhenReadyAsync() => Task.CompletedTask;
}
