using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LumePDF.Pdf;

/// <summary>
/// The text layer of one page: every character PDFium reports, in its order, with its box.
/// Boxes are normalized (0..1) to the page as if it had no /Rotate, so lines run left to right
/// whatever the rotation; points and rectangles are converted from and to the page as displayed.
/// Characters PDFium generates (spaces and line breaks between runs) have an empty box.
/// No PDFium calls here: hit testing, words, lines, highlight rectangles and copied text.
/// </summary>
public sealed class PageText
{
    public static readonly PageText Empty = new("", [], 0, 1);

    readonly NormRect[] _boxes;
    readonly double _aspect;
    readonly double _top = double.MaxValue, _bottom = double.MinValue;

    /// <param name="boxes">Upright box of each character, or none when only the text is needed.</param>
    /// <param name="rotation">Quarter turns clockwise from upright to displayed: the page's /Rotate.</param>
    /// <param name="aspect">Height over width of the upright page, so distances count the same both ways.</param>
    public PageText(string text, NormRect[] boxes, int rotation, double aspect)
    {
        if (boxes.Length != 0 && boxes.Length != text.Length)
            throw new ArgumentException("One box per character.", nameof(boxes));
        Text = text;
        _boxes = boxes;
        Rotation = rotation & 3;
        _aspect = aspect > 0 ? aspect : 1;
        foreach (var b in boxes)
        {
            if (!IsHittable(b))
                continue;
            _top = Math.Min(_top, b.Y);
            _bottom = Math.Max(_bottom, b.Y + b.Height);
        }
        foreach (char c in text)
        {
            if (!char.IsWhiteSpace(c) && !char.IsControl(c))
            {
                HasText = true;
                break;
            }
        }
    }

    public string Text { get; }
    public int Length => Text.Length;
    public int Rotation { get; }
    /// <summary>Whether there is anything worth selecting: scanned pages have no text at all.</summary>
    public bool HasText { get; }

    // ───────────────────────────── Rotation ─────────────────────────────

    /// <summary>Upright coordinates of a point given on the page as displayed.</summary>
    public static (double X, double Y) ToUpright(double x, double y, int rotation) => (rotation & 3) switch
    {
        1 => (y, 1 - x),
        2 => (1 - x, 1 - y),
        3 => (1 - y, x),
        _ => (x, y),
    };

    /// <summary>Displayed coordinates of an upright point.</summary>
    public static (double X, double Y) ToDisplay(double x, double y, int rotation) => (rotation & 3) switch
    {
        1 => (1 - y, x),
        2 => (1 - x, 1 - y),
        3 => (y, 1 - x),
        _ => (x, y),
    };

    public static NormRect ToUpright(NormRect r, int rotation) => Map(r, rotation, ToUpright);
    public static NormRect ToDisplay(NormRect r, int rotation) => Map(r, rotation, ToDisplay);

    static NormRect Map(NormRect r, int rotation, Func<double, double, int, (double, double)> map)
    {
        var (x1, y1) = map(r.X, r.Y, rotation);
        var (x2, y2) = map(r.X + r.Width, r.Y + r.Height, rotation);
        return new NormRect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
    }

    // ───────────────────────────── Hit testing ─────────────────────────────

    static bool IsHittable(NormRect b) => b.Width > 0 && b.Height > 0;

    /// <summary>
    /// Caret (0..Length, between characters) for a pointer at a displayed point: before or after
    /// the character it points at. Above all the text is the start of the page, below it the end,
    /// like in a text block, so a drag through margins and gaps between pages behaves.
    /// </summary>
    public int CaretAt(double x, double y)
    {
        (x, y) = ToUpright(x, y, Rotation);
        if (_boxes.Length == 0 || y < _top)
            return 0;
        if (y > _bottom)
            return Length;
        int i = Nearest(x, y);
        if (i < 0)
            return 0;
        var b = _boxes[i];
        return x < b.X + b.Width / 2 ? i : i + 1;
    }

    /// <summary>The character under a displayed point, or -1 when the point is not over text.</summary>
    public int CharAt(double x, double y)
    {
        if (!IsOverText(x, y))
            return -1;
        (x, y) = ToUpright(x, y, Rotation);
        return Nearest(x, y);
    }

    /// <summary>Whether a displayed point is on a character, with a little slack around it.</summary>
    public bool IsOverText(double x, double y)
    {
        (x, y) = ToUpright(x, y, Rotation);
        foreach (var b in _boxes)
        {
            if (!IsHittable(b))
                continue;
            double padY = b.Height * 0.15, padX = padY * _aspect;
            if (x >= b.X - padX && x <= b.X + b.Width + padX && y >= b.Y - padY && y <= b.Y + b.Height + padY)
                return true;
        }
        return false;
    }

    // On the line under the point, the horizontally closest character (the next column over
    // included); off every line, the closest one overall. -1 without any box.
    int Nearest(double x, double y)
    {
        int best = -1, onLine = -1;
        double bestDistance = double.MaxValue, lineDistance = double.MaxValue;
        for (int i = 0; i < _boxes.Length; i++)
        {
            var b = _boxes[i];
            if (!IsHittable(b))
                continue;
            double dx = Math.Max(0, Math.Max(b.X - x, x - b.X - b.Width));
            double dy = Math.Max(0, Math.Max(b.Y - y, y - b.Y - b.Height)) * _aspect;
            if (dy == 0 && dx < lineDistance)
            {
                lineDistance = dx;
                onLine = i;
            }
            double d = dx * dx + dy * dy;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        return onLine >= 0 ? onLine : best;
    }

    // ───────────────────────────── Words and lines ─────────────────────────────

    static bool IsLineBreak(char c) => c is '\r' or '\n';

    /// <summary>
    /// What a double click selects around a character, as in browsers: a word (letters and digits,
    /// with inner apostrophes and decimal separators), a run of spaces, or the single character.
    /// </summary>
    public (int Start, int End) WordAt(int index)
    {
        if (index < 0 || index >= Length)
            return (Math.Clamp(index, 0, Length), Math.Clamp(index, 0, Length));
        Func<int, bool> same = IsWordChar(index) ? IsWordChar
            : Text[index] is ' ' or '\t' or ' ' ? i => Text[i] is ' ' or '\t' or ' '
            : i => i == index;
        int start = index, end = index + 1;
        while (start > 0 && same(start - 1))
            start--;
        while (end < Length && same(end))
            end++;
        return (start, end);
    }

    bool IsWordChar(int i)
    {
        char c = Text[i];
        if (char.IsLetterOrDigit(c))
            return true;
        switch (char.GetUnicodeCategory(c))
        {
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.SpacingCombiningMark:
            case UnicodeCategory.ConnectorPunctuation:
                return true;
        }
        // l'albero, dell’anno, 3.14, 1,5: joiners count with a letter or digit on both sides.
        return c is '\'' or '’' ? Between(char.IsLetter) : c is '.' or ',' && Between(char.IsDigit);

        bool Between(Func<char, bool> kind) => i > 0 && i + 1 < Length && kind(Text[i - 1]) && kind(Text[i + 1]);
    }

    /// <summary>What a triple click selects: the line around a character, without its line break.</summary>
    public (int Start, int End) LineAt(int index)
    {
        index = Math.Clamp(index, 0, Length);
        int start = index, end = index;
        while (start > 0 && !IsLineBreak(Text[start - 1]))
            start--;
        while (end < Length && !IsLineBreak(Text[end]))
            end++;
        return (start, end);
    }

    // ───────────────────────────── Selection output ─────────────────────────────

    /// <summary>
    /// Highlight rectangles for the characters between two carets, on the page as displayed:
    /// one per run of characters on the same line, spaces between words included.
    /// </summary>
    public List<NormRect> GetRects(int start, int end)
    {
        var rects = new List<NormRect>();
        end = Math.Min(end, _boxes.Length);
        double left = 0, top = 0, right = 0, bottom = 0;
        bool open = false;
        for (int i = Math.Max(0, start); i < end; i++)
        {
            var b = _boxes[i];
            if (IsLineBreak(Text[i]))
            {
                Flush();
                continue;
            }
            if (!IsHittable(b))
                continue;
            double overlap = Math.Min(bottom, b.Y + b.Height) - Math.Max(top, b.Y);
            bool sameLine = open && overlap >= 0.5 * Math.Min(bottom - top, b.Height)
                && b.X >= right - b.Height * _aspect;
            if (!sameLine)
            {
                Flush();
                (left, top, right, bottom, open) = (b.X, b.Y, b.X + b.Width, b.Y + b.Height, true);
                continue;
            }
            left = Math.Min(left, b.X);
            right = Math.Max(right, b.X + b.Width);
            top = Math.Min(top, b.Y);
            bottom = Math.Max(bottom, b.Y + b.Height);
        }
        Flush();
        return rects;

        void Flush()
        {
            if (open)
                rects.Add(ToDisplay(new NormRect(left, top, right - left, bottom - top), Rotation));
            open = false;
        }
    }

    /// <summary>
    /// Text between two carets, ready for the clipboard: Windows line breaks, no trailing spaces
    /// before them, no control characters (some PDFs map glyphs to them).
    /// </summary>
    public string GetText(int start, int end)
    {
        start = Math.Clamp(start, 0, Length);
        end = Math.Clamp(end, start, Length);
        var sb = new StringBuilder(end - start);
        for (int i = start; i < end; i++)
        {
            char c = Text[i];
            if (IsLineBreak(c))
            {
                if (c == '\r' && i + 1 < end && Text[i + 1] == '\n')
                    i++;
                TrimEndSpaces(sb);
                sb.Append("\r\n");
            }
            else if (c == '\t' || !(char.IsControl(c) || c is '￾' or '￿'))
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    public static void TrimEndSpaces(StringBuilder sb)
    {
        int n = sb.Length;
        while (n > 0 && sb[n - 1] is ' ' or '\t')
            n--;
        sb.Length = n;
    }
}
