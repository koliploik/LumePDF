using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace LumePDF.Pdf;

// PDFium is single-threaded, so all native calls are serialized on one background thread.
// Work items are small (one page render, one page search) so the UI stays responsive
// and stale requests can be skipped cheaply through their cancellation token.
public static class PdfWorker
{
    static readonly BlockingCollection<Action> Queue = new();
    static int _started;

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        var thread = new Thread(() =>
        {
            Pdfium.FPDF_InitLibrary();
            foreach (var work in Queue.GetConsumingEnumerable())
                work();
        })
        {
            IsBackground = true,
            Name = "PDFium",
        };
        thread.Start();
    }

    public static Task<T> Run<T>(Func<T> func, CancellationToken ct = default)
    {
        Start();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }
}
