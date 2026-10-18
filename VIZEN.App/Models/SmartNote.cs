namespace VIZEN.App.Models;

public enum NoteKind
{
    Text,
    Checklist,
}

/// <summary>One line of a checklist note.</summary>
public sealed class ChecklistItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = string.Empty;
    public bool Done { get; set; }

    public ChecklistItem Clone() => new() { Id = Id, Text = Text, Done = Done };
}

/// <summary>
/// One Smart Note: its content and how its floating window is shown. Persisted in notes.json.
/// Bounds are WPF device-independent units; 0 width/height = not placed yet.
/// </summary>
public sealed class SmartNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public NoteKind Kind { get; set; } = NoteKind.Text;

    /// <summary>Content of a text note (plain text, multiline).</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Lines of a checklist note, in display order.</summary>
    public List<ChecklistItem> Items { get; set; } = [];

    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public double Opacity { get; set; } = 1.0;

    /// <summary>Above all other windows (including borderless games), like the overlays.</summary>
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>Locked notes cannot be moved or resized with the mouse.</summary>
    public bool Locked { get; set; }

    /// <summary>Mouse input goes through the note to the window underneath.</summary>
    public bool ClickThrough { get; set; }

    /// <summary>The floating window is open.</summary>
    public bool IsShown { get; set; }

    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
}
