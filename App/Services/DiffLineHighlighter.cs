namespace Accel.App.Services;

using System.Collections.Generic;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

/// <summary>
/// AvalonEdit line transformer for the GIT diff view's editable "After" pane
/// (<c>MainWindow.DiffNewEditor</c>): paints <see cref="Brush"/> over whichever 0-based
/// line indices <see cref="SetHighlightedLines"/> was last given, the AvalonEdit
/// equivalent of the <c>Func&lt;int, Brush?&gt; lineBackground</c> callback
/// <c>MainWindow.BuildHighlightedDocument</c> already uses to colour the read-only diff
/// panes' <see cref="System.Windows.Documents.Paragraph.Background"/>.
///
/// <para>Kept separate from <see cref="SyntaxColorizer"/> (rather than folding this into
/// it) because the two vary independently: syntax colouring is a function of the
/// document's language and content alone, while this is a function of a diff computed
/// against a second, unrelated document (the "Before" side) - re-running
/// <c>ComputeDiffMarks</c> must not touch, and is not gated by, the syntax cache.</para>
/// </summary>
public sealed class DiffLineHighlighter : DocumentColorizingTransformer
{
    private Brush? _brush;
    private HashSet<int> _lines = new();

    /// <summary>Replaces the highlighted-line set and the brush painted over it.
    /// <paramref name="lines"/> holds 0-based line indices, matching
    /// <c>MainWindow.ComputeDiffMarks</c>'s own convention. The caller is responsible for
    /// calling <c>TextView.Redraw()</c> afterwards - this class does not own the
    /// <see cref="ICSharpCode.AvalonEdit.Rendering.TextView"/> it is attached to.</summary>
    public void SetHighlightedLines(IReadOnlyCollection<int> lines, Brush brush)
    {
        _lines = new HashSet<int>(lines);
        _brush = brush;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_brush is null || !_lines.Contains(line.LineNumber - 1))
        {
            return;
        }

        ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetBackgroundBrush(_brush));
    }
}
