using System.Runtime.InteropServices;

namespace Lieve.Pdf;

// Raw bindings to pdfium.dll (Google PDFium, as packaged by bblanchon/pdfium-binaries).
// PDFium is not thread-safe: every call must go through PdfWorker.
internal static unsafe partial class Pdfium
{
    const string Lib = "pdfium";

    public const int FPDF_ANNOT = 0x01;
    public const int FPDFBitmap_BGRA = 4;

    public const uint FPDF_ERR_FILE = 2;
    public const uint FPDF_ERR_FORMAT = 3;
    public const uint FPDF_ERR_PASSWORD = 4;
    public const uint FPDF_ERR_SECURITY = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct PdfFileAccess
    {
        public uint FileLen; // "unsigned long" is 32-bit on Windows
        public delegate* unmanaged<nint, uint, byte*, uint, int> GetBlock;
        public nint Param;
    }

    [LibraryImport(Lib)] public static partial void FPDF_InitLibrary();
    [LibraryImport(Lib)] public static partial uint FPDF_GetLastError();

    [LibraryImport(Lib)] public static partial nint FPDF_LoadCustomDocument(PdfFileAccess* access, byte* password);
    [LibraryImport(Lib)] public static partial void FPDF_CloseDocument(nint doc);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageCount(nint doc);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageSizeByIndex(nint doc, int index, double* width, double* height);

    [LibraryImport(Lib)] public static partial nint FPDF_LoadPage(nint doc, int index);
    [LibraryImport(Lib)] public static partial void FPDF_ClosePage(nint page);
    [LibraryImport(Lib)]
    public static partial int FPDF_PageToDevice(nint page, int startX, int startY, int sizeX, int sizeY, int rotate,
        double pageX, double pageY, int* deviceX, int* deviceY);

    [LibraryImport(Lib)] public static partial nint FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);
    [LibraryImport(Lib)] public static partial void FPDFBitmap_Destroy(nint bitmap);
    [LibraryImport(Lib)]
    public static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY,
        int rotate, int flags);

    [LibraryImport(Lib)] public static partial nint FPDFText_LoadPage(nint page);
    [LibraryImport(Lib)] public static partial void FPDFText_ClosePage(nint textPage);
    [LibraryImport(Lib)] public static partial nint FPDFText_FindStart(nint textPage, char* findWhat, uint flags, int startIndex);
    [LibraryImport(Lib)] public static partial int FPDFText_FindNext(nint search);
    [LibraryImport(Lib)] public static partial int FPDFText_GetSchResultIndex(nint search);
    [LibraryImport(Lib)] public static partial int FPDFText_GetSchCount(nint search);
    [LibraryImport(Lib)] public static partial void FPDFText_FindClose(nint search);
    [LibraryImport(Lib)] public static partial int FPDFText_CountRects(nint textPage, int startIndex, int count);
    [LibraryImport(Lib)]
    public static partial int FPDFText_GetRect(nint textPage, int rectIndex, double* left, double* top, double* right, double* bottom);

    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetFirstChild(nint doc, nint bookmark);
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetNextSibling(nint doc, nint bookmark);
    [LibraryImport(Lib)] public static partial uint FPDFBookmark_GetTitle(nint bookmark, void* buffer, uint length);
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetDest(nint doc, nint bookmark);
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetAction(nint bookmark);
    [LibraryImport(Lib)] public static partial nint FPDFAction_GetDest(nint doc, nint action);
    [LibraryImport(Lib)] public static partial int FPDFDest_GetDestPageIndex(nint doc, nint dest);
}
