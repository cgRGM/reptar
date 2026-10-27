#if !NETWASM
using Reptar.AI;
using System.Text;

namespace Reptar.Term;

/// <summary>One row in a <see cref="TermSelector"/>.</summary>
/// <param name="Label">Primary text — the model id, provider name, or setting name.</param>
/// <param name="Detail">Second column. Empty means the label uses the full width.</param>
/// <param name="Status">Trailing annotation such as <c>✓ stored</c> or <c>· not configured</c>.</param>
/// <param name="IsCurrent">Marks the active choice with <c>✓</c>.</param>
/// <param name="Description">Shown under the list for the selected row.</param>
/// <param name="Value">Opaque payload the caller uses to act on a choice.</param>
public sealed record TermSelectorItem(
    string Label,
    string Detail = "",
    string Status = "",
    bool IsCurrent = false,
    string Description = "",
    object? Value = null);

/// <summary>
/// A searchable list, rendered inline above the footer the way pi draws its pickers.
/// </summary>
/// <remarks>
/// pi's selectors are all the same component: a rule, an optional notice, a title, a search row,
/// the filtered items, a position counter, a detail line for the selection, a status line, and a
/// key hint. Nothing is boxed or centred — the whole thing occupies the composer's slot, so it
/// reads as part of the terminal rather than a dialog floating over it.
///
/// The search is pi's fuzzy matcher rather than a substring test, so typing "opus" finds
/// <c>claude-opus-4-6</c> and typing "mer" finds <c>Mermaid diagrams</c>.
/// </remarks>
public sealed class TermSelector
{
    private readonly List<TermSelectorItem> _all;
    private readonly List<TermSelectorItem> _visible = [];

    public TermSelector(string title, IEnumerable<TermSelectorItem> items, int maxVisible = 10)
    {
        Title = title;
        _all = [.. items];
        MaxVisible = maxVisible;
        Refilter();
    }

    /// <summary>Heading shown above the search row. Empty omits it.</summary>
    public string Title { get; set; }

    /// <summary>
    /// Line above the title, for the thing the list is scoped to. pi uses it for
    /// "Only showing models from configured providers".
    /// </summary>
    public string Notice { get; set; } = "";

    /// <summary>Status line below the detail. Empty omits it.</summary>
    public string Status { get; set; } = "";

    /// <summary>Key hint on the last line.</summary>
    public string Hint { get; set; } = "Type to search · Enter to select · Esc to cancel";

    /// <summary>How many rows are visible before the list scrolls.</summary>
    public int MaxVisible { get; }

    /// <summary>Current search text.</summary>
    public string Query { get; private set; } = "";

    /// <summary>Index into the filtered list.</summary>
    public int SelectedIndex { get; private set; }

    /// <summary>Filtered items, best match first.</summary>
    public IReadOnlyList<TermSelectorItem> Visible => _visible;

    /// <summary>The highlighted item, or null when nothing matches.</summary>
    public TermSelectorItem? Selected =>
        _visible.Count == 0 ? null : _visible[Math.Clamp(SelectedIndex, 0, _visible.Count - 1)];

    /// <summary>True when a query is filtering the list.</summary>
    public bool IsSearching => Query.Length > 0;

    /// <summary>Appends a character to the search and re-filters.</summary>
    public void Type(char value)
    {
        if (char.IsControl(value))
            return;

        Query += value;
        Refilter();
    }

    /// <summary>Appends pasted text to the search and re-filters.</summary>
    public void Paste(string text)
    {
        Query += text.ReplaceLineEndings(" ").Trim();
        Refilter();
    }

    /// <summary>Removes the last search character.</summary>
    public void Backspace()
    {
        if (Query.Length == 0)
            return;

        Query = Query[..^1];
        Refilter();
    }

    /// <summary>Moves the selection, wrapping at both ends.</summary>
    public void Move(int delta)
    {
        if (_visible.Count == 0)
            return;

        SelectedIndex = (SelectedIndex + delta + _visible.Count) % _visible.Count;
    }

    /// <summary>
    /// Replaces the item list while preserving what the user is doing: the search text, and the
    /// selection by label.
    /// </summary>
    /// <remarks>
    /// Rebuilding a selector from scratch resets the query and the index, which makes a refresh
    /// look like the list jumped. Anything that reloads data behind an open picker should come
    /// through here rather than constructing a new selector.
    /// </remarks>
    public void SetItems(IEnumerable<TermSelectorItem> items)
    {
        var selectedLabel = Selected?.Label;
        var previousIndex = SelectedIndex;

        _all.Clear();
        _all.AddRange(items);

        Refilter();

        // Prefer the same item by name; fall back to the same position so the highlight does not
        // teleport to the top when the item genuinely disappeared.
        if (!SelectByLabel(selectedLabel))
            SelectedIndex = Math.Clamp(previousIndex, 0, Math.Max(0, _visible.Count - 1));
    }

    /// <summary>
    /// Applies a mouse event: wheel scrolls the selection, a click selects the row under the
    /// pointer.
    /// </summary>
    /// <param name="mouse">The decoded event.</param>
    /// <param name="listTop">
    /// Screen row of the first visible item, so a click can be mapped to an index. Pass -1 when
    /// the caller does not track rows; clicks are then ignored and only the wheel works.
    /// </param>
    /// <returns>True when the event was consumed.</returns>
    public bool ApplyMouse(TermMouseEvent mouse, int listTop = -1)
    {
        switch (mouse.Kind)
        {
            case TermMouseKind.WheelUp:
                Move(-3);
                return true;

            case TermMouseKind.WheelDown:
                Move(3);
                return true;

            case TermMouseKind.Press when listTop >= 0:
                var index = _visibleIndexForRow(mouse.Y, listTop);
                if (index < 0)
                    return false;

                SelectedIndex = index;
                return true;

            default:
                return false;
        }
    }

    /// <summary>Maps a screen row to a filtered-item index, or -1 when it is outside the list.</summary>
    private int _visibleIndexForRow(int row, int listTop)
    {
        var offset = row - listTop;
        if (offset < 0 || offset >= Math.Min(_visible.Count, MaxVisible))
            return -1;

        var first = Math.Max(0, Math.Min(SelectedIndex - MaxVisible + 1, _visible.Count - MaxVisible));
        var index = first + offset;
        return index >= 0 && index < _visible.Count ? index : -1;
    }

    /// <summary>Clears the search text, for reopening a picker fresh.</summary>
    public void ClearQuery()
    {
        Query = "";
        Refilter();
    }

    /// <summary>
    /// Moves the selection to the first item whose label matches. Used after a rebuild so
    /// changing a value does not throw you back to the top of the list.
    /// </summary>
    /// <returns>True when the label was found.</returns>
    public bool SelectByLabel(string? label)
    {
        if (string.IsNullOrEmpty(label))
            return false;

        for (var i = 0; i < _visible.Count; i++)
        {
            if (string.Equals(_visible[i].Label, label, StringComparison.Ordinal))
            {
                SelectedIndex = i;
                return true;
            }
        }

        return false;
    }

    /// <summary>Index of the selection, for restoring it after a rebuild.</summary>
    public int Index => SelectedIndex;

    private void Refilter()
    {
        _visible.Clear();

        if (Query.Length == 0)
        {
            _visible.AddRange(_all);
        }
        else
        {
            // Rank on the label first, then the detail and status, so "stored" finds configured
            // providers and "opus" finds the model.
            _visible.AddRange(Fuzzy.Rank(
                Query,
                _all,
                item => [item.Label, item.Detail, item.Status]));
        }

        SelectedIndex = Math.Clamp(SelectedIndex, 0, Math.Max(0, _visible.Count - 1));
    }

    /// <summary>
    /// Total rows this selector will occupy. Callers use it to place the block above the footer.
    /// </summary>
    public int Height
    {
        get
        {
            var rows = 2;                       // rule + search
            if (Notice.Length > 0) rows++;
            if (Title.Length > 0) rows++;
            if (Status.Length > 0) rows++;
            if (Hint.Length > 0) rows++;
            if (Selected?.Description is { Length: > 0 }) rows++;

            rows += Math.Min(Math.Max(_visible.Count, 1), MaxVisible);
            rows++;                             // position counter
            return rows;
        }
    }

    /// <summary>
    /// Renders the selector as plain text lines, for a console-mode caller that prints rather
    /// than painting a buffer.
    /// </summary>
    /// <param name="width">Available columns.</param>
    /// <param name="maxVisible">Rows of items to show.</param>
    /// <remarks>
    /// The point of this existing is that the filtering, ranking, and selection rules live in one
    /// place. A line-oriented client keeps its own frame handling but must not keep its own copy
    /// of the matching logic — that is how two widget systems drift apart.
    /// </remarks>
    public List<string> RenderLines(int width, int maxVisible = 10)
    {
        var lines = new List<string>();

        if (Title.Length > 0)
            lines.Add(Title);

        if (Notice.Length > 0)
            lines.Add(Notice);

        lines.Add(Query.Length > 0 ? $"filter: {Query}" : "");

        if (_visible.Count == 0)
        {
            lines.Add($"  no matches for \"{Query}\"");
        }
        else
        {
            var first = Math.Max(0, Math.Min(SelectedIndex - maxVisible + 1, _visible.Count - maxVisible));

            for (var i = first; i < _visible.Count && i < first + maxVisible; i++)
            {
                var item = _visible[i];
                var marker = i == SelectedIndex ? "> " : "  ";
                var current = item.IsCurrent ? "✓ " : "  ";

                var line = $"{marker}{current}{item.Label}";
                if (item.Detail.Length > 0)
                    line += $"  {item.Detail}";
                if (item.Status.Length > 0)
                    line += $"  {item.Status}";

                lines.Add(line.Length > width ? line[..Math.Max(0, width - 1)] + "…" : line);
            }
        }

        lines.Add($"({Math.Min(SelectedIndex + 1, _visible.Count)}/{_visible.Count})");

        if (Selected?.Description is { Length: > 0 } description)
            lines.Add(description);

        if (Hint.Length > 0)
            lines.Add(Hint);

        return lines;
    }

    /// <summary>
    /// Draws the selector so its last row lands on <paramref name="bottomRow"/>.
    /// </summary>
    public void Render(
        TerminalBuffer buffer,
        int width,
        int bottomRow,
        string ruleFg,
        string textFg,
        string dimFg,
        string accentFg,
        string selectedBg)
    {
        // Build top-down, then place bottom-up so the block hugs the footer.
        var lines = new List<(string Text, string Fg, bool Bold, bool Highlight)>();

        lines.Add((new string('─', Math.Max(4, width - 2)), ruleFg, false, false));

        if (Notice.Length > 0)
            lines.Add(("  " + Notice, dimFg, false, false));

        if (Title.Length > 0)
            lines.Add(("  " + Title, textFg, true, false));

        // Search row. The arrow marks it as the focused input; the caret follows the text so it
        // reads as a field you are typing into rather than a static label.
        lines.Add(("  → " + Query + "▏", textFg, false, false));

        if (_visible.Count == 0)
        {
            lines.Add(("  no matches", dimFg, false, false));
        }
        else
        {
            var first = Math.Max(0, Math.Min(SelectedIndex - MaxVisible + 1, _visible.Count - MaxVisible));

            for (var i = first; i < _visible.Count && i < first + MaxVisible; i++)
            {
                var item = _visible[i];
                var selected = i == SelectedIndex;
                var pointer = selected ? "→ " : "  ";

                var label = item.Label;
                var detail = item.Detail;
                var status = item.Status;

                var text = new StringBuilder("  ");
                text.Append(pointer);
                text.Append(item.IsCurrent ? "✓ " : "  ");
                text.Append(label);

                if (detail.Length > 0)
                {
                    var labelWidth = Math.Min(40, Math.Max(20, width / 3));
                    if (text.Length < 2 + labelWidth)
                        text.Append(' ', 2 + labelWidth - text.Length);
                    else
                        text.Append("  ");
                    text.Append(detail);
                }

                if (status.Length > 0)
                    text.Append("  ").Append(status);

                var fg = item.IsCurrent ? accentFg : selected ? "\x1b[97m" : dimFg;
                lines.Add((text.ToString(), fg, selected, selected));
            }
        }

        lines.Add(($"  ({Math.Min(SelectedIndex + 1, _visible.Count)}/{_visible.Count})", dimFg, false, false));

        if (Selected?.Description is { Length: > 0 } description)
            lines.Add(("  " + description, dimFg, false, false));

        if (Status.Length > 0)
            lines.Add(("  " + Status, dimFg, false, false));

        if (Hint.Length > 0)
            lines.Add(("  " + Hint, dimFg, false, false));

        var y = bottomRow;
        for (var i = lines.Count - 1; i >= 0 && y >= 1; i--, y--)
        {
            var (text, fg, bold, highlight) = lines[i];

            // Clear the whole row first. The selector is drawn over a live transcript, so
            // without this the previous frame's text shows through wherever the new line is
            // shorter — which reads as garbled rows rather than a list.
            buffer.FillRect(0, y, width, 1, ' ', bg: highlight ? selectedBg : "");

            buffer.Print(0, y, text, fg: fg, bold: bold,
                bg: highlight ? selectedBg : "", maxWidth: width);
        }
    }
}
#endif
