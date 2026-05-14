using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Inklet.Editor;

/// <summary>
/// Adapts <see cref="RichEditBox"/> to a TextBox-style API. We migrated from TextBox
/// to RichEditBox in #41 because the WinUI 3 TextBox silently truncates text at
/// ~524288 characters, which made it impossible to open even moderately large files
/// (a 1 MB markdown file rendered only the first ~13700 lines and silently dropped
/// the rest).
///
/// <para>
/// RichEditBox has no character cap (it routes through RichEdit, which handles MB-scale
/// documents). The public APIs differ enough — Document-mediated text/selection access,
/// no built-in plain-text Cut/Copy/Paste/SelectAll — that wrapping them here keeps the
/// MainWindow code-behind unchanged shape.
/// </para>
/// </summary>
internal static class RichEditExtensions
{
    /// <summary>
    /// Returns the plain text in the editor. <see cref="ITextDocument.GetText"/> appends
    /// a sentinel <c>\r</c> at the end of the document — strip it so consumers see the
    /// same string they assigned via <see cref="SetPlainText"/>.
    /// </summary>
    public static string GetPlainText(this RichEditBox editor)
    {
        editor.Document.GetText(TextGetOptions.None, out var text);
        return text?.TrimEnd('\r') ?? string.Empty;
    }

    /// <summary>
    /// Replaces the editor's content with <paramref name="text"/>. Any rich formatting
    /// is dropped (we want a plain-text editor surface).
    /// </summary>
    public static void SetPlainText(this RichEditBox editor, string? text)
    {
        editor.Document.SetText(TextSetOptions.None, text ?? string.Empty);
    }

    public static int GetSelectionStart(this RichEditBox editor)
        => editor.Document.Selection.StartPosition;

    public static void SetSelectionStart(this RichEditBox editor, int position)
    {
        var sel = editor.Document.Selection;
        int len = sel.Length;
        sel.SetRange(position, position + len);
    }

    public static int GetSelectionLength(this RichEditBox editor)
        => editor.Document.Selection.Length;

    public static void SetSelectionLength(this RichEditBox editor, int length)
    {
        var sel = editor.Document.Selection;
        sel.SetRange(sel.StartPosition, sel.StartPosition + length);
    }

    public static string GetSelectedText(this RichEditBox editor)
        => editor.Document.Selection.Text?.TrimEnd('\r') ?? string.Empty;

    /// <summary>
    /// Selects the entire document. <c>SetRange(0, int.MaxValue)</c> is the documented
    /// idiom for "select to end" — RichEdit clamps to the document length internally.
    /// </summary>
    public static void DocumentSelectAll(this RichEditBox editor)
        => editor.Document.Selection.SetRange(0, int.MaxValue);

    public static void DocumentUndo(this RichEditBox editor) => editor.Document.Undo();
    public static void DocumentRedo(this RichEditBox editor) => editor.Document.Redo();

    /// <summary>Copies the current selection as plain text to the system clipboard.</summary>
    public static void CopyPlainSelection(this RichEditBox editor)
    {
        var text = editor.GetSelectedText();
        if (string.IsNullOrEmpty(text)) return;
        var pkg = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        pkg.SetText(text);
        Clipboard.SetContent(pkg);
    }

    /// <summary>Cuts the current selection as plain text to the system clipboard.</summary>
    public static void CutPlainSelection(this RichEditBox editor)
    {
        editor.CopyPlainSelection();
        editor.Document.Selection.Delete(TextRangeUnit.Character, 0);
    }

    /// <summary>
    /// Replaces the current selection with the clipboard's text. Always pastes as plain
    /// text — the editor surface is plain even though the underlying control supports rich.
    /// </summary>
    public static async System.Threading.Tasks.Task PastePlainAsync(this RichEditBox editor)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            var text = await content.GetTextAsync();
            if (string.IsNullOrEmpty(text)) return;
            editor.Document.Selection.SetText(TextSetOptions.None, text);
        }
        catch { /* best-effort */ }
    }
}
