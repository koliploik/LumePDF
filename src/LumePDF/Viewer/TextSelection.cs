using System;
using System.Collections.Generic;
using System.Text;
using LumePDF.Pdf;

namespace LumePDF.Viewer;

/// <summary>A caret in the document: a page, and a position between that page's characters.</summary>
public readonly record struct TextPosition(int Page, int Index) : IComparable<TextPosition>
{
    /// <summary>Index meaning "end of the page" when its text hasn't been loaded.</summary>
    public const int PageEnd = int.MaxValue;

    public int CompareTo(TextPosition other) => Page != other.Page ? Page.CompareTo(other.Page) : Index.CompareTo(other.Index);

    public static bool operator <(TextPosition a, TextPosition b) => a.CompareTo(b) < 0;
    public static bool operator >(TextPosition a, TextPosition b) => a.CompareTo(b) > 0;
    public static bool operator <=(TextPosition a, TextPosition b) => a.CompareTo(b) <= 0;
    public static bool operator >=(TextPosition a, TextPosition b) => a.CompareTo(b) >= 0;
}

/// <summary>
/// Text selection across pages, as browsers do it: the unit first clicked (a caret, a word or a
/// line) is the anchor, and dragging extends from it to the same kind of unit under the pointer,
/// in whichever direction that lies.
/// </summary>
public sealed class TextSelection
{
    TextPosition _anchorStart, _anchorEnd;

    public TextPosition Start { get; private set; }
    public TextPosition End { get; private set; }
    public bool IsEmpty => Start == End;

    public void Begin(TextPosition start, TextPosition end)
    {
        _anchorStart = Start = start;
        _anchorEnd = End = end;
    }

    /// <summary>Selects from the anchor to the unit [start, end) under the pointer; the anchor stays.</summary>
    public void ExtendTo(TextPosition start, TextPosition end)
    {
        if (start < _anchorStart)
        {
            Start = start;
            End = _anchorEnd;
        }
        else
        {
            Start = _anchorStart;
            End = end > _anchorEnd ? end : _anchorEnd;
        }
    }

    public void SelectAll(int pageCount)
    {
        if (pageCount > 0)
            Begin(new(0, 0), new(pageCount - 1, TextPosition.PageEnd));
        else
            Clear();
    }

    public void Clear() => Begin(default, default);

    /// <summary>The selected characters of one page, or null when none of them are.</summary>
    public (int Start, int End)? RangeOn(int page, int length)
    {
        if (IsEmpty || page < Start.Page || page > End.Page)
            return null;
        int start = page == Start.Page ? Math.Min(Start.Index, length) : 0;
        int end = page == End.Page ? Math.Min(End.Index, length) : length;
        return start < end ? (start, end) : null;
    }

    /// <summary>Whether the selection covers any text; null when some of its pages aren't loaded.</summary>
    public bool? HasText(Func<int, PageText?> loadedText)
    {
        if (IsEmpty)
            return false;
        bool unknown = false;
        for (int page = Start.Page; page <= End.Page; page++)
        {
            if (loadedText(page) is not { } text)
                unknown = true;
            else if (RangeOn(page, text.Length) is (int start, int end) && text.GetText(start, end).Trim().Length > 0)
                return true;
        }
        return unknown ? null : false;
    }

    /// <summary>The selected text for the clipboard, one line break between pages.</summary>
    /// <param name="pages">Text of every page from Start.Page to End.Page.</param>
    public string GetText(IReadOnlyList<PageText> pages)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < pages.Count; i++)
        {
            if (RangeOn(Start.Page + i, pages[i].Length) is not (int start, int end))
                continue;
            string part = pages[i].GetText(start, end);
            if (part.Length == 0)
                continue;
            if (sb.Length > 0 && sb[^1] != '\n')
            {
                PageText.TrimEndSpaces(sb);
                sb.Append("\r\n");
            }
            sb.Append(part);
        }
        return sb.ToString();
    }
}

/// <summary>Counts quick clicks on the same spot: 1, 2 (word), 3 (line), then over again.</summary>
public sealed class ClickCounter
{
    long _time = long.MinValue;
    double _x, _y;
    int _count;

    /// <param name="timeMs">Time of the press in milliseconds.</param>
    /// <param name="interval">Longest pause between clicks of a series (the system double-click time).</param>
    /// <param name="slop">How far, in DIPs, the pointer may wander between them.</param>
    public int Press(long timeMs, double x, double y, long interval, double slop)
    {
        bool series = timeMs - _time <= interval && Math.Abs(x - _x) <= slop && Math.Abs(y - _y) <= slop;
        _count = series ? _count % 3 + 1 : 1;
        (_time, _x, _y) = (timeMs, x, y);
        return _count;
    }
}
