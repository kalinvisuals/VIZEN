namespace VIZEN.App.Models;

public enum DrawingChangeKind
{
    Added,
    Removed,
    Updated,
    Reset,

    /// <summary>Some annotations were replaced by others in their place (an eraser drag, or its undo / redo).</summary>
    Replaced,
}

/// <summary>What changed in a <see cref="DrawingDocument"/>, so the canvases update only the affected visuals.</summary>
/// <param name="Annotations">Added, removed, updated or (Reset) all annotations; for Replaced the ones that came in.</param>
/// <param name="Removed">Replaced only: the annotations that went out.</param>
public sealed record DrawingChange(DrawingChangeKind Kind, IReadOnlyList<Annotation> Annotations, IReadOnlyList<Annotation>? Removed = null);

/// <summary>
/// The annotations of a Smart Draw session and a bounded undo / redo history of vector commands (never bitmaps).
/// UI thread only.
/// </summary>
public sealed class DrawingDocument
{
    /// <summary>At most this many annotations are kept (each one is a WPF visual on screen).</summary>
    public const int MaxAnnotations = 1000;

    /// <summary>Undo steps kept.</summary>
    public const int MaxHistory = 100;

    private readonly List<Annotation> _items = [];
    private readonly LinkedList<Command> _undo = new();
    private readonly Stack<Command> _redo = new();

    public IReadOnlyList<Annotation> Items => _items;

    public int Count => _items.Count;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public bool IsFull => _items.Count >= MaxAnnotations;

    /// <summary>Raised after every change (UI thread).</summary>
    public event EventHandler<DrawingChange>? Changed;

    /// <summary>Adds a finished annotation. False when it is too small to keep or the drawing is full.</summary>
    public bool Add(Annotation annotation)
    {
        if (!DrawingGeometry.IsMeaningful(annotation) || IsFull)
        {
            return false;
        }

        Do(new AddCommand(annotation));
        return true;
    }

    /// <summary>Removes several annotations as one undo step (one eraser drag).</summary>
    public int Remove(IEnumerable<Annotation> annotations)
    {
        var present = annotations.Distinct().Where(_items.Contains).ToList();
        if (present.Count > 0)
        {
            Do(new RemoveCommand(present));
        }

        return present.Count;
    }

    /// <summary>Changes the text of a text annotation (one undo step); an empty text removes it.</summary>
    public void UpdateText(Annotation annotation, string text, double width, double height)
    {
        if (!_items.Contains(annotation) || annotation.Text == text)
        {
            return;
        }

        if (text.Trim().Length == 0)
        {
            Remove([annotation]);
            return;
        }

        Do(new TextCommand(annotation, annotation.Text, annotation.TextWidth, annotation.TextHeight, text, width, height));
    }

    /// <summary>
    /// Replaces annotations by others in their place (one eraser drag: cut strokes by their remaining pieces, erased
    /// objects by nothing) as one undo step. Pieces keep the stacking position of the stroke they come from.
    /// </summary>
    public void ReplaceParts(IReadOnlyDictionary<Annotation, IReadOnlyList<Annotation>> replacements)
    {
        if (!replacements.Keys.Any(_items.Contains))
        {
            return;
        }

        var after = new List<Annotation>(_items.Count);
        foreach (var a in _items)
        {
            if (replacements.TryGetValue(a, out var pieces))
            {
                after.AddRange(pieces);
            }
            else
            {
                after.Add(a);
            }
        }

        var removed = replacements.Keys.Where(_items.Contains).ToList();
        var added = after.Except(_items).ToList();
        Do(new ReplaceCommand([.. _items], after, removed, added));
    }

    public void Clear()
    {
        if (_items.Count > 0)
        {
            Do(new ClearCommand([.. _items]));
        }
    }

    public void Undo()
    {
        if (_undo.Last is { } last)
        {
            _undo.RemoveLast();
            last.Value.Revert(this);
            _redo.Push(last.Value);
        }
    }

    public void Redo()
    {
        if (_redo.TryPop(out var command))
        {
            command.Apply(this);
            _undo.AddLast(command);
        }
    }

    /// <summary>Replaces everything (a restored drawing); the history starts empty.</summary>
    public void Load(IEnumerable<Annotation> annotations)
    {
        _items.Clear();
        _items.AddRange(annotations.Where(DrawingGeometry.IsMeaningful).Take(MaxAnnotations));
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, new DrawingChange(DrawingChangeKind.Reset, _items));
    }

    private void Do(Command command)
    {
        command.Apply(this);
        _undo.AddLast(command);
        if (_undo.Count > MaxHistory)
        {
            _undo.RemoveFirst();
        }

        _redo.Clear();
    }

    private void Raise(DrawingChangeKind kind, IReadOnlyList<Annotation> annotations) => Changed?.Invoke(this, new DrawingChange(kind, annotations));

    private abstract class Command
    {
        public abstract void Apply(DrawingDocument d);
        public abstract void Revert(DrawingDocument d);
    }

    private sealed class AddCommand(Annotation a) : Command
    {
        public override void Apply(DrawingDocument d) { d._items.Add(a); d.Raise(DrawingChangeKind.Added, [a]); }
        public override void Revert(DrawingDocument d) { d._items.Remove(a); d.Raise(DrawingChangeKind.Removed, [a]); }
    }

    private sealed class RemoveCommand(List<Annotation> removed) : Command
    {
        private readonly List<int> _positions = [];

        public override void Apply(DrawingDocument d)
        {
            _positions.Clear();
            _positions.AddRange(removed.Select(a => d._items.IndexOf(a)));
            foreach (var a in removed)
            {
                d._items.Remove(a);
            }

            d.Raise(DrawingChangeKind.Removed, removed);
        }

        public override void Revert(DrawingDocument d)
        {
            // Back at their places, so the stacking order is the same as before.
            foreach (var (a, index) in removed.Zip(_positions).OrderBy(x => x.Second))
            {
                d._items.Insert(Math.Clamp(index, 0, d._items.Count), a);
            }

            d.Raise(DrawingChangeKind.Reset, d._items);
        }
    }

    private sealed class ClearCommand(List<Annotation> all) : Command
    {
        public override void Apply(DrawingDocument d) { d._items.Clear(); d.Raise(DrawingChangeKind.Reset, d._items); }
        public override void Revert(DrawingDocument d) { d._items.Clear(); d._items.AddRange(all); d.Raise(DrawingChangeKind.Reset, d._items); }
    }

    /// <summary>
    /// Before / after lists of references (no copies of the drawings themselves). Raises Replaced with only the
    /// annotations that went out and came in, so the canvases redraw just those (no full-screen redraw).
    /// </summary>
    private sealed class ReplaceCommand(List<Annotation> before, List<Annotation> after, List<Annotation> removed, List<Annotation> added) : Command
    {
        public override void Apply(DrawingDocument d)
        {
            d._items.Clear();
            d._items.AddRange(after);
            d.Changed?.Invoke(d, new DrawingChange(DrawingChangeKind.Replaced, added, removed));
        }

        public override void Revert(DrawingDocument d)
        {
            d._items.Clear();
            d._items.AddRange(before);
            d.Changed?.Invoke(d, new DrawingChange(DrawingChangeKind.Replaced, removed, added));
        }
    }

    private sealed class TextCommand(Annotation a, string oldText, double oldWidth, double oldHeight, string newText, double newWidth, double newHeight) : Command
    {
        public override void Apply(DrawingDocument d) { (a.Text, a.TextWidth, a.TextHeight) = (newText, newWidth, newHeight); d.Raise(DrawingChangeKind.Updated, [a]); }
        public override void Revert(DrawingDocument d) { (a.Text, a.TextWidth, a.TextHeight) = (oldText, oldWidth, oldHeight); d.Raise(DrawingChangeKind.Updated, [a]); }
    }
}
