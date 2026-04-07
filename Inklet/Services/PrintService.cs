using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Text;

namespace Inklet.Services;

// ---------------------------------------------------------------------------
// Native structures for PrintDlgEx (Win32 common print dialog)
// ---------------------------------------------------------------------------
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
internal struct PRINTDLGEX
{
    public uint lStructSize;
    public IntPtr hwndOwner;
    public IntPtr hDevMode;
    public IntPtr hDevNames;
    public IntPtr hDC;
    public uint Flags;
    public uint Flags2;
    public uint ExclusionFlags;
    public uint nPageRanges;
    public uint nMaxPageRanges;
    public IntPtr lpPageRanges;
    public uint nMinPage;
    public uint nMaxPage;
    public uint nCopies;
    public IntPtr hInstance;
    public IntPtr lpPrintTemplateName;
    public IntPtr lpCallback;
    public uint nPropertyPages;
    public IntPtr lphPropertyPages;
    public uint nStartPage;
    public uint dwResultAction;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
internal struct DEVMODE
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string dmDeviceName;
    public short dmSpecVersion;
    public short dmDriverVersion;
    public short dmSize;
    public short dmDriverExtra;
    public int dmFields;
    public short dmOrientation;
    public short dmPaperSize;
    public short dmPaperLength;
    public short dmPaperWidth;
    public short dmScale;
    public short dmCopies;
    public short dmDefaultSource;
    public short dmPrintQuality;
    public short dmColor;
    public short dmDuplex;
    public short dmYResolution;
    public short dmTTOption;
    public short dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string dmFormName;
    public short dmLogPixels;
    public int dmBitsPerPel;
    public int dmPelsWidth;
    public int dmPelsHeight;
    public int dmDisplayFlags;
    public int dmDisplayFrequency;
    public int dmICMMethod;
    public int dmICMIntent;
    public int dmMediaType;
    public int dmDitherType;
    public int dmReserved1;
    public int dmReserved2;
    public int dmPanningWidth;
    public int dmPanningHeight;
}

/// <summary>
/// Drives a GDI+ <see cref="PrintDocument"/> to produce Notepad-equivalent plain-text output.
/// Shows the Win32 system print dialog via <c>PrintDlgEx</c> (no WinForms dependency).
/// </summary>
internal sealed class PrintService
{
    // -----------------------------------------------------------------------
    // P/Invoke
    // -----------------------------------------------------------------------
    [DllImport("comdlg32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int PrintDlgEx(ref PRINTDLGEX lppd);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    private const uint PD_RETURNDC          = 0x00000100;
    private const uint PD_NOPAGENUMS        = 0x00000008;
    private const uint PD_NOSELECTION       = 0x00000004;
    private const uint PD_USEDEVMODECOPIES  = 0x00040000;
    private const uint PD_COLLATE           = 0x00000010;
    private const uint PD_RESULT_PRINT      = 1;
    private const uint START_PAGE_GENERAL   = 0xFFFFFFFF;

    // -----------------------------------------------------------------------
    // Layout constants (points)
    // -----------------------------------------------------------------------
    private const int HeaderBodyGap  = 6;
    private const int BodyFooterGap  = 6;

    // -----------------------------------------------------------------------
    // Construction parameters
    // -----------------------------------------------------------------------
    private readonly string _text;
    private readonly string _fileName;
    private readonly string _fontFamilyName;
    private readonly float  _fontSize;
    private readonly bool   _bold;
    private readonly bool   _italic;
    private readonly PrintPageSettings _pageSetup;

    // -----------------------------------------------------------------------
    // Per-job state (threaded through PrintPage callbacks)
    // -----------------------------------------------------------------------
    private List<string>? _wrappedLines;
    private int _currentLine;
    private int _totalPages;

    /// <param name="text">Text to print.</param>
    /// <param name="fileName">Displayed via the <c>&amp;f</c> token in header/footer.</param>
    /// <param name="fontFamilyName">Editor font family name.</param>
    /// <param name="fontSize">Base font size in points (not zoom-adjusted).</param>
    /// <param name="bold">Whether the editor font is bold.</param>
    /// <param name="italic">Whether the editor font is italic.</param>
    /// <param name="pageSetup">Margin / header / footer / paper settings.</param>
    internal PrintService(
        string text,
        string fileName,
        string fontFamilyName,
        float  fontSize,
        bool   bold,
        bool   italic,
        PrintPageSettings pageSetup)
    {
        _text          = text;
        _fileName      = fileName;
        _fontFamilyName = fontFamilyName;
        _fontSize      = Math.Max(6f, fontSize);
        _bold          = bold;
        _italic        = italic;
        _pageSetup     = pageSetup;
    }

    // -----------------------------------------------------------------------
    // Public entry point
    // -----------------------------------------------------------------------

    /// <summary>
    /// Shows the Win32 system Print dialog (parented to <paramref name="hwnd"/>),
    /// then prints. Returns <c>false</c> if the user cancelled.
    /// </summary>
    internal bool ShowDialogAndPrint(IntPtr hwnd)
    {
        var dlg = new PRINTDLGEX
        {
            lStructSize   = (uint)Marshal.SizeOf<PRINTDLGEX>(),
            hwndOwner     = hwnd,
            Flags         = PD_RETURNDC | PD_NOPAGENUMS | PD_NOSELECTION | PD_USEDEVMODECOPIES,
            nStartPage    = START_PAGE_GENERAL,
            nMinPage      = 1,
            nMaxPage      = 1,
            nMaxPageRanges = 1
        };

        int hr = PrintDlgEx(ref dlg);
        if (hr != 0 || dlg.dwResultAction != PD_RESULT_PRINT)
        {
            // Clean up any handles the dialog allocated
            FreeHandle(ref dlg.hDevMode);
            FreeHandle(ref dlg.hDevNames);
            if (dlg.hDC != IntPtr.Zero) DeleteDC(dlg.hDC);
            return false;
        }

        // Extract printer name from hDevNames so PrintDocument uses the right printer.
        string? printerName = GetPrinterName(dlg.hDevNames);
        int copies = (int)Math.Max(1, dlg.nCopies);
        bool collate = (dlg.Flags & PD_COLLATE) != 0;

        FreeHandle(ref dlg.hDevMode);
        FreeHandle(ref dlg.hDevNames);
        if (dlg.hDC != IntPtr.Zero) DeleteDC(dlg.hDC);

        // Now drive PrintDocument with the chosen printer.
        using var doc = BuildDocument(printerName, copies, collate);
        doc.Print();
        return true;
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    // -----------------------------------------------------------------------
    // PrintDocument construction
    // -----------------------------------------------------------------------

    private PrintDocument BuildDocument(string? printerName, int copies, bool collate)
    {
        var doc = new PrintDocument
        {
            DocumentName = System.IO.Path.GetFileName(_fileName)
        };

        if (!string.IsNullOrEmpty(printerName))
            doc.PrinterSettings.PrinterName = printerName;

        doc.PrinterSettings.Copies = (short)Math.Min(copies, short.MaxValue);
        doc.PrinterSettings.Collate = collate;
        doc.DefaultPageSettings.Margins  = _pageSetup.Margins;
        doc.DefaultPageSettings.Landscape = _pageSetup.Landscape;

        if (_pageSetup.PaperSize is not null)
            doc.DefaultPageSettings.PaperSize = _pageSetup.PaperSize;

        doc.BeginPrint += (_, _) =>
        {
            _currentLine  = 0;
            _wrappedLines = null;
        };
        doc.PrintPage += OnPrintPage;
        return doc;
    }

    // -----------------------------------------------------------------------
    // Page rendering
    // -----------------------------------------------------------------------

    private void OnPrintPage(object sender, PrintPageEventArgs e)
    {
        var g      = e.Graphics!;
        var bounds = e.MarginBounds;

        using var bodyFont = MakeFont(_fontSize);
        using var hfFont   = MakeFont(_fontSize * 0.9f);

        // Wrap all text once using the printable width from the first page.
        _wrappedLines ??= WordWrapText(g, _text, bodyFont, bounds.Width);

        float bodyLineHeight = bodyFont.GetHeight(g);
        float hfLineHeight   = hfFont.GetHeight(g);

        bool hasHeader = !string.IsNullOrEmpty(_pageSetup.Header);
        bool hasFooter = !string.IsNullOrEmpty(_pageSetup.Footer);

        float headerReserve = hasHeader ? hfLineHeight + HeaderBodyGap : 0f;
        float footerReserve = hasFooter ? hfLineHeight + BodyFooterGap : 0f;

        int linesPerPage = Math.Max(1,
            (int)((bounds.Height - headerReserve - footerReserve) / bodyLineHeight));

        // Total pages — recomputed each page (same value every time).
        _totalPages = Math.Max(1,
            (int)Math.Ceiling(_wrappedLines.Count / (double)linesPerPage));

        int currentPage = (_currentLine / linesPerPage) + 1;

        float y = bounds.Top;

        // Header
        if (hasHeader)
        {
            DrawHfLine(g, hfFont, _pageSetup.Header, bounds, y, currentPage, _totalPages);
            y += hfLineHeight + HeaderBodyGap;
        }

        float bodyTop = y;

        // Body
        for (int i = 0; i < linesPerPage && _currentLine < _wrappedLines.Count; i++, _currentLine++)
        {
            g.DrawString(
                _wrappedLines[_currentLine],
                bodyFont,
                Brushes.Black,
                new RectangleF(bounds.Left, bodyTop + i * bodyLineHeight, bounds.Width, bodyLineHeight),
                StringFormat.GenericTypographic);
        }

        // Footer
        if (hasFooter)
        {
            float footerY = bounds.Bottom - hfLineHeight;
            DrawHfLine(g, hfFont, _pageSetup.Footer, bounds, footerY, currentPage, _totalPages);
        }

        e.HasMorePages = _currentLine < _wrappedLines.Count;
    }

    // -----------------------------------------------------------------------
    // Header / footer rendering
    // -----------------------------------------------------------------------

    /// <summary>
    /// Renders a single header or footer line.
    /// Tab characters split the string into left / centre / right zones,
    /// matching Notepad's layout behaviour. Without tabs the text is centred.
    /// </summary>
    private void DrawHfLine(
        Graphics g, Font font, string template,
        Rectangle bounds, float y, int page, int totalPages)
    {
        string expanded = ExpandTokens(template, _fileName, page, totalPages);
        string[] parts  = expanded.Split('\t');

        var rect = new RectangleF(bounds.Left, y, bounds.Width, font.GetHeight(g) + 2);

        using var leftFmt = new StringFormat(StringFormat.GenericTypographic)
            { Alignment = StringAlignment.Near,   Trimming = StringTrimming.EllipsisCharacter };
        using var centreFmt = new StringFormat(StringFormat.GenericTypographic)
            { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        using var rightFmt = new StringFormat(StringFormat.GenericTypographic)
            { Alignment = StringAlignment.Far,    Trimming = StringTrimming.EllipsisCharacter };

        switch (parts.Length)
        {
            case 1:
                g.DrawString(parts[0], font, Brushes.Black, rect, centreFmt);
                break;
            case 2:
                g.DrawString(parts[0], font, Brushes.Black, rect, leftFmt);
                g.DrawString(parts[1], font, Brushes.Black, rect, rightFmt);
                break;
            default:
                g.DrawString(parts[0], font, Brushes.Black, rect, leftFmt);
                g.DrawString(parts[1], font, Brushes.Black, rect, centreFmt);
                g.DrawString(parts[2], font, Brushes.Black, rect, rightFmt);
                break;
        }
    }

    // -----------------------------------------------------------------------
    // Token expansion
    // -----------------------------------------------------------------------

    /// <summary>
    /// Expands Notepad-compatible tokens in a header/footer template.
    /// <list type="bullet">
    ///   <item><c>&amp;f</c> — file name</item>
    ///   <item><c>&amp;d</c> — short date</item>
    ///   <item><c>&amp;t</c> — short time</item>
    ///   <item><c>&amp;p</c> — current page number</item>
    ///   <item><c>&amp;P</c> — total page count</item>
    ///   <item><c>&amp;&amp;</c> — literal ampersand</item>
    /// </list>
    /// </summary>
    internal static string ExpandTokens(string template, string fileName, int page, int totalPages)
    {
        var now  = DateTime.Now;
        var sb   = new StringBuilder(template.Length + 32);
        string displayName = string.IsNullOrEmpty(fileName)
            ? "Untitled"
            : System.IO.Path.GetFileName(fileName);

        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] != '&' || i + 1 >= template.Length)
            {
                sb.Append(template[i]);
                continue;
            }

            char next = template[i + 1];
            switch (next)
            {
                case 'f': case 'F': sb.Append(displayName);           i++; break;
                case 'd': case 'D': sb.Append(now.ToShortDateString()); i++; break;
                case 't': case 'T': sb.Append(now.ToShortTimeString()); i++; break;
                case 'p':           sb.Append(page);                   i++; break;
                case 'P':           sb.Append(totalPages);             i++; break;
                case '&':           sb.Append('&');                    i++; break;
                default:            sb.Append(template[i]);                 break;
            }
        }

        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // Word-wrap
    // -----------------------------------------------------------------------

    private static List<string> WordWrapText(Graphics g, string text, Font font, float maxWidth)
    {
        var result = new List<string>(256);
        if (string.IsNullOrEmpty(text)) { result.Add(string.Empty); return result; }

        var fmt         = StringFormat.GenericTypographic;
        var sourceLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        foreach (var source in sourceLines)
        {
            if (source.Length == 0) { result.Add(string.Empty); continue; }

            var   words        = source.Split(' ');
            var   current      = new StringBuilder();
            float currentWidth = 0f;

            foreach (var word in words)
            {
                string probe      = current.Length == 0 ? word : " " + word;
                float  probeWidth = g.MeasureString(probe, font, int.MaxValue, fmt).Width;

                if (currentWidth + probeWidth > maxWidth && current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    current.Append(word);
                    currentWidth = g.MeasureString(word, font, int.MaxValue, fmt).Width;
                }
                else
                {
                    current.Append(probe);
                    currentWidth += probeWidth;
                }
            }

            if (current.Length > 0) result.Add(current.ToString());
        }

        return result;
    }

    // -----------------------------------------------------------------------
    // Font factory
    // -----------------------------------------------------------------------

    private Font MakeFont(float size)
    {
        var style = FontStyle.Regular;
        if (_bold)   style |= FontStyle.Bold;
        if (_italic) style |= FontStyle.Italic;

        try   { return new Font(_fontFamilyName, size, style, GraphicsUnit.Point); }
        catch { return new Font(FontFamily.GenericMonospace, size, style, GraphicsUnit.Point); }
    }

    // -----------------------------------------------------------------------
    // Native helpers
    // -----------------------------------------------------------------------

    private static string? GetPrinterName(IntPtr hDevNames)
    {
        if (hDevNames == IntPtr.Zero) return null;
        try
        {
            IntPtr ptr = GlobalLock(hDevNames);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                // DEVNAMES layout: wDriverOffset, wDeviceOffset, wOutputOffset, wDefault (each ushort)
                int deviceOffset = Marshal.ReadInt16(ptr, 2);
                return Marshal.PtrToStringAuto(ptr + deviceOffset * 2);
            }
            finally { GlobalUnlock(hDevNames); }
        }
        catch { return null; }
    }

    private static void FreeHandle(ref IntPtr handle)
    {
        if (handle != IntPtr.Zero) { GlobalFree(handle); handle = IntPtr.Zero; }
    }
}

// ---------------------------------------------------------------------------
// Page setup value object — shared between the dialog and PrintService
// ---------------------------------------------------------------------------

/// <summary>
/// Carries all user-configurable page layout settings for a print job.
/// </summary>
internal sealed class PrintPageSettings
{
    /// <summary>
    /// GDI+ margins in hundredths of an inch.
    /// Defaults match Notepad: left 1.25 in, right 1.25 in, top 1 in, bottom 1 in.
    /// </summary>
    internal Margins Margins { get; set; } = new(125, 125, 100, 100);

    /// <summary>Header template string. Empty string disables the header.</summary>
    internal string Header { get; set; } = "&f";

    /// <summary>Footer template string. Empty string disables the footer.</summary>
    internal string Footer { get; set; } = "Page &p of &P";

    /// <summary>Selected paper size, or <c>null</c> for the printer default.</summary>
    internal PaperSize? PaperSize { get; set; }

    /// <summary>Landscape orientation.</summary>
    internal bool Landscape { get; set; }
}
