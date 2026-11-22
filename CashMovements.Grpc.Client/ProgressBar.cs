using System.Globalization;

namespace CashMovements.Grpc.Client;

/// <summary>
/// Draws export progress on one console line, redrawn at most 10 times a second:
/// <c>[############--------]  62%  93,000 / 150,000 lines  4.1 MB  48,200 lines/s  ETA 1s</c>.
/// When output is redirected to a file, it writes a plain line every 10% instead.
/// </summary>
public sealed class ProgressBar
{
    private const int Width = 20;
    private static readonly TimeSpan RedrawInterval = TimeSpan.FromMilliseconds(100);

    private readonly TextWriter _out;
    private readonly bool _interactive;
    private TimeSpan? _lastDrawn; // null until the first redraw
    private int _lastDecile = -1;
    private int _lastLength;
    private bool _drawn;

    public ProgressBar(TextWriter? output = null, bool? interactive = null)
    {
        _out = output ?? Console.Out;
        _interactive = interactive ?? !Console.IsOutputRedirected;
    }

    public void Report(ExportProgress progress)
    {
        if (_interactive)
        {
            var finished = progress.LinesWritten >= progress.LineCount;
            if (!finished && _lastDrawn is { } last && progress.Elapsed - last < RedrawInterval)
                return;

            _lastDrawn = progress.Elapsed;
            var line = Format(progress);
            _out.Write("\r" + line.PadRight(_lastLength));
            _lastLength = line.Length;
            _drawn = true;
        }
        else
        {
            var decile = (int)(progress.Fraction * 10);
            if (decile == _lastDecile)
                return;

            _lastDecile = decile;
            _out.WriteLine(Format(progress));
        }
    }

    /// <summary>Ends the progress line, so whatever is written next starts on a new line.</summary>
    public void Finish()
    {
        if (_interactive && _drawn)
            _out.WriteLine();
        _drawn = false;
    }

    public static string Format(ExportProgress p)
    {
        var filled = (int)Math.Round(p.Fraction * Width);
        var bar = new string('#', filled) + new string('-', Width - filled);
        var c = CultureInfo.InvariantCulture;

        var text = string.Format(
            c,
            "[{0}] {1,4:0}%  {2:N0} / {3:N0} lines  {4}  {5:N0} lines/s",
            bar,
            p.Fraction * 100,
            p.LinesWritten,
            p.LineCount,
            Size(p.BytesWritten),
            p.LinesPerSecond);

        return p.Remaining is { } remaining && p.LinesWritten < p.LineCount
            ? text + "  ETA " + Duration(remaining)
            : text;
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1024 => (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " bytes",
    };

    public static string Duration(TimeSpan t)
        => t.TotalSeconds < 60
            ? Math.Ceiling(t.TotalSeconds).ToString("0", CultureInfo.InvariantCulture) + "s"
            : $"{(int)t.TotalMinutes}m {t.Seconds:00}s";
}
