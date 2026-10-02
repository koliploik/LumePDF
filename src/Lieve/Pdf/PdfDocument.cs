using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Lieve.Pdf.Pdfium;

namespace Lieve.Pdf;

public sealed class PdfPasswordException : Exception;

/// <summary>A match; rectangles are normalized (0..1) to the page as displayed.</summary>
public sealed record SearchHit(int Page, NormRect[] Rects);

public readonly record struct NormRect(double X, double Y, double Width, double Height);

public sealed record OutlineItem(string Title, int Page, List<OutlineItem> Children);

/// <summary>A BGRA bitmap in native memory, owned by the caller.</summary>
public sealed unsafe partial class RenderedPage : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public byte* Pixels { get; private set; }

    internal RenderedPage(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = (byte*)NativeMemory.Alloc((nuint)width * (nuint)height * 4);
    }

    public ReadOnlySpan<byte> Span => new(Pixels, Width * Height * 4);

    public void Dispose()
    {
        if (Pixels != null)
        {
            NativeMemory.Free(Pixels);
            Pixels = null;
        }
    }
}

public sealed unsafe partial class PdfDocument : IDisposable
{
    // Device space used to normalize text rectangles through FPDF_PageToDevice.
    const int NormSpace = 1_000_000;

    nint _doc;
    readonly FileStream _stream;
    GCHandle _streamHandle;
    PdfFileAccess* _access;

    public string FilePath { get; }
    public int PageCount { get; }
    /// <summary>Page sizes in PDF points, with /Rotate already applied.</summary>
    public (double Width, double Height)[] PageSizes { get; }
    /// <summary>Median page size: what "fit width/page" targets, so one landscape page doesn't shrink the rest.</summary>
    public double TypicalPageWidth { get; }
    public double TypicalPageHeight { get; }

    PdfDocument(string path, FileStream stream, GCHandle handle, PdfFileAccess* access, nint doc)
    {
        FilePath = path;
        _stream = stream;
        _streamHandle = handle;
        _access = access;
        _doc = doc;

        PageCount = FPDF_GetPageCount(doc);
        PageSizes = new (double, double)[PageCount];
        for (int i = 0; i < PageCount; i++)
        {
            double w, h;
            if (FPDF_GetPageSizeByIndex(doc, i, &w, &h) == 0 || w <= 0 || h <= 0)
                (w, h) = (612, 792);
            PageSizes[i] = (w, h);
        }

        if (PageCount > 0)
        {
            var widths = PageSizes.Select(s => s.Width).Order().ToArray();
            var heights = PageSizes.Select(s => s.Height).Order().ToArray();
            TypicalPageWidth = widths[PageCount / 2];
            TypicalPageHeight = heights[PageCount / 2];
        }
        else
        {
            (TypicalPageWidth, TypicalPageHeight) = (612, 792);
        }
    }

    public static Task<PdfDocument> OpenAsync(string path, string? password) =>
        PdfWorker.Run(() => Open(path, password));

    static PdfDocument Open(string path, string? password)
    {
        // Share read/write/delete so the file is never locked against other programs.
        var stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
        if (stream.Length > uint.MaxValue)
        {
            stream.Dispose();
            throw new IOException("Il file supera i 4 GB.");
        }

        var handle = GCHandle.Alloc(stream);
        var access = (PdfFileAccess*)NativeMemory.Alloc((nuint)sizeof(PdfFileAccess));
        access->FileLen = (uint)stream.Length;
        access->GetBlock = &GetBlock;
        access->Param = GCHandle.ToIntPtr(handle);

        byte[]? pwd = password is null ? null : Encoding.UTF8.GetBytes(password + "\0");
        nint doc;
        fixed (byte* p = pwd)
            doc = FPDF_LoadCustomDocument(access, p);

        if (doc == 0)
        {
            uint err = FPDF_GetLastError();
            NativeMemory.Free(access);
            handle.Free();
            stream.Dispose();
            throw err switch
            {
                FPDF_ERR_PASSWORD => new PdfPasswordException(),
                FPDF_ERR_FORMAT => new InvalidDataException("Il file non è un PDF valido o è danneggiato."),
                FPDF_ERR_SECURITY => new InvalidDataException("Schema di protezione non supportato."),
                FPDF_ERR_FILE => new IOException("Impossibile leggere il file."),
                _ => new InvalidDataException($"Errore PDFium {err}."),
            };
        }

        return new PdfDocument(path, stream, handle, access, doc);
    }

    [UnmanagedCallersOnly]
    static int GetBlock(nint param, uint position, byte* buffer, uint size)
    {
        try
        {
            var stream = (FileStream)GCHandle.FromIntPtr(param).Target!;
            stream.Position = position;
            stream.ReadExactly(new Span<byte>(buffer, (int)size));
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    public Task<RenderedPage> RenderAsync(int index, int width, int height, CancellationToken ct) =>
        PdfWorker.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            EnsureOpen();
            var result = new RenderedPage(width, height);
            nint page = FPDF_LoadPage(_doc, index);
            nint bitmap = FPDFBitmap_CreateEx(width, height, FPDFBitmap_BGRA, result.Pixels, width * 4);
            try
            {
                FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                if (page != 0)
                    FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, FPDF_ANNOT);
            }
            finally
            {
                FPDFBitmap_Destroy(bitmap);
                if (page != 0)
                    FPDF_ClosePage(page);
            }
            return result;
        }, ct);

    public Task<List<SearchHit>> SearchPageAsync(int index, string query, CancellationToken ct) =>
        PdfWorker.Run(() =>
        {
            EnsureOpen();
            var hits = new List<SearchHit>();
            nint page = FPDF_LoadPage(_doc, index);
            if (page == 0)
                return hits;
            nint text = FPDFText_LoadPage(page);
            try
            {
                if (text == 0)
                    return hits;
                fixed (char* q = query)
                {
                    nint search = FPDFText_FindStart(text, q, 0, 0);
                    while (FPDFText_FindNext(search) != 0)
                    {
                        int start = FPDFText_GetSchResultIndex(search);
                        int count = FPDFText_GetSchCount(search);
                        int n = FPDFText_CountRects(text, start, count);
                        var rects = new NormRect[Math.Max(n, 0)];
                        for (int r = 0; r < n; r++)
                        {
                            double left, top, right, bottom;
                            FPDFText_GetRect(text, r, &left, &top, &right, &bottom);
                            rects[r] = ToNormalized(page, left, top, right, bottom);
                        }
                        if (rects.Length > 0)
                            hits.Add(new SearchHit(index, rects));
                    }
                    FPDFText_FindClose(search);
                }
            }
            finally
            {
                if (text != 0)
                    FPDFText_ClosePage(text);
                FPDF_ClosePage(page);
            }
            return hits;
        }, ct);

    static NormRect ToNormalized(nint page, double left, double top, double right, double bottom)
    {
        int x1, y1, x2, y2;
        FPDF_PageToDevice(page, 0, 0, NormSpace, NormSpace, 0, left, top, &x1, &y1);
        FPDF_PageToDevice(page, 0, 0, NormSpace, NormSpace, 0, right, bottom, &x2, &y2);
        double x = Math.Min(x1, x2), y = Math.Min(y1, y2);
        return new NormRect(x / NormSpace, y / NormSpace,
            Math.Abs(x2 - x1) / (double)NormSpace, Math.Abs(y2 - y1) / (double)NormSpace);
    }

    public Task<List<OutlineItem>> GetOutlineAsync() =>
        PdfWorker.Run(() =>
        {
            EnsureOpen();
            var items = new List<OutlineItem>();
            int budget = 10_000; // guards against malformed, cyclic outlines
            ReadOutline(FPDFBookmark_GetFirstChild(_doc, 0), items, 0, ref budget);
            return items;
        });

    void ReadOutline(nint bookmark, List<OutlineItem> into, int depth, ref int budget)
    {
        while (bookmark != 0 && budget-- > 0 && depth < 32)
        {
            var item = new OutlineItem(ReadTitle(bookmark), ReadDestPage(bookmark), []);
            into.Add(item);
            ReadOutline(FPDFBookmark_GetFirstChild(_doc, bookmark), item.Children, depth + 1, ref budget);
            bookmark = FPDFBookmark_GetNextSibling(_doc, bookmark);
        }
    }

    static string ReadTitle(nint bookmark)
    {
        uint bytes = FPDFBookmark_GetTitle(bookmark, null, 0);
        if (bytes <= 2)
            return "";
        var buffer = new char[bytes / 2];
        fixed (char* p = buffer)
            FPDFBookmark_GetTitle(bookmark, p, bytes);
        return new string(buffer, 0, buffer.Length - 1).Trim();
    }

    int ReadDestPage(nint bookmark)
    {
        nint dest = FPDFBookmark_GetDest(_doc, bookmark);
        if (dest == 0)
        {
            nint action = FPDFBookmark_GetAction(bookmark);
            if (action != 0)
                dest = FPDFAction_GetDest(_doc, action);
        }
        return dest == 0 ? -1 : FPDFDest_GetDestPageIndex(_doc, dest);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void EnsureOpen()
    {
        if (_doc == 0)
            throw new OperationCanceledException();
    }

    public void Dispose()
    {
        PdfWorker.Run(() =>
        {
            if (_doc == 0)
                return 0;
            FPDF_CloseDocument(_doc);
            _doc = 0;
            NativeMemory.Free(_access);
            _access = null;
            _streamHandle.Free();
            _stream.Dispose();
            return 0;
        });
    }
}
