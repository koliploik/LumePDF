using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LumePDF.Tests;

/// <summary>Writes tiny one-page PDFs (US Letter, Helvetica) for tests that need a specific layout.</summary>
static class TestPdf
{
    public static string Sample => Path.Combine(AppContext.BaseDirectory, "samples", "test.pdf");

    /// <param name="content">Page content stream, e.g. "BT /F1 24 Tf 72 720 Td (Hello) Tj ET".</param>
    public static string Write(string content, int rotate = 0, string mediaBox = "0 0 612 792")
    {
        // Next to the test binaries, so nothing is left behind outside the build output.
        string dir = Path.Combine(AppContext.BaseDirectory, "generated");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{Guid.NewGuid():N}.pdf");
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [{mediaBox}] /Rotate {rotate} /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        return path;
    }
}
