using System.Text;

namespace LumePDF.Viewer;

/// <summary>
/// What the right-click menu offers in a given state. Kept apart from the XAML so that the
/// rules for showing and enabling its items can be tested.
/// </summary>
/// <param name="SelectionHasText">Null while some selected pages haven't been read yet.</param>
/// <param name="DocumentHasText">Null until a page with text turns up or every page has been checked.</param>
public readonly record struct PageMenuState(bool HasDocument, bool? SelectionHasText, bool? DocumentHasText,
    bool CanZoomIn, bool CanZoomOut)
{
    const int MaxSearchTerm = 80;
    const int MaxLabelTerm = 24;

    /// <summary>Without a document only "Apri" makes sense: everything else is hidden.</summary>
    public bool ShowDocumentItems => HasDocument;

    public bool CanCopy => HasDocument && (SelectionHasText ?? DocumentHasText == true);
    public bool CanSelectAll => HasDocument && DocumentHasText == true;

    /// <summary>The selected text as a search query, or null when it doesn't make one (empty, several lines, too long).</summary>
    public static string? SearchTerm(string? selected)
    {
        if (selected == null || selected.Contains('\n') || selected.Contains('\r'))
            return null;
        var sb = new StringBuilder(selected.Length);
        foreach (char c in selected.Trim())
        {
            if (!char.IsWhiteSpace(c))
                sb.Append(c);
            else if (sb[^1] != ' ')
                sb.Append(' ');
        }
        return sb.Length is > 0 and <= MaxSearchTerm ? sb.ToString() : null;
    }

    public static string FindLabel(string? term) =>
        term == null ? "Cerca…"
        : term.Length <= MaxLabelTerm ? $"Cerca «{term}»"
        : $"Cerca «{term[..(MaxLabelTerm - 1)].TrimEnd()}…»";
}
