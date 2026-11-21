using PdfEditor.Core.Interop;

namespace PdfEditor.Core;

/// <summary>How a drawn path is painted. A path with neither a fill nor a stroke is not drawn.</summary>
public sealed record PathStyle
{
    /// <summary>The colour inside the path; null leaves it see-through.</summary>
    public PdfColor? Fill { get; init; }
    /// <summary>The colour of the outline; null draws no outline.</summary>
    public PdfColor? Stroke { get; init; }
    /// <summary>Outline thickness in points.</summary>
    public double StrokeWidth { get; init; } = 1;
    /// <summary>
    /// How a path that crosses itself, or has an outline inside another, is filled. False (the
    /// default) fills what the outlines wind round, as fonts do, so a hole is an outline drawn the
    /// other way round; true fills what lies inside an odd number of outlines.
    /// </summary>
    public bool EvenOdd { get; init; }
    /// <summary>Round ends and corners on the outline; false gives square ends and sharp corners.</summary>
    public bool RoundEnds { get; init; }
}

/// <summary>
/// Draws many things on one page in a single pass. Drawing one thing at a time through
/// <see cref="PdfDocument.AddShape"/> and <see cref="PdfDocument.AddText"/> writes the page again
/// each time, which is slow for a page of thousands of marks (a printed score, a diagram); a
/// canvas writes it once, when <see cref="PdfDocument.Draw"/> finishes. Coordinates are in view
/// space, in points from the top-left of the page as displayed.
/// </summary>
public sealed unsafe class PageCanvas
{
    private readonly PdfDocument _owner;
    private readonly PdfDocument.PageHandle _page;

    internal PageCanvas(PdfDocument owner, PdfDocument.PageHandle page)
    {
        _owner = owner;
        _page = page;
    }

    /// <summary>Draws a path given in view space.</summary>
    public void Path(IReadOnlyList<ShapeStep> steps, PathStyle style) => Path(steps, style, 1, 0, 0, flip: false);

    /// <summary>
    /// Draws a path whose coordinates are in a y-up system of its own (as a glyph outline is),
    /// scaled by <paramref name="scale"/> and placed so that its origin lies at (<paramref name="x"/>,
    /// <paramref name="y"/>) in view space.
    /// </summary>
    public void PlacedPath(IReadOnlyList<ShapeStep> steps, PathStyle style, double scale, double x, double y) => Path(steps, style, scale, x, y, flip: true);

    private void Path(IReadOnlyList<ShapeStep> steps, PathStyle style, double scale, double dx, double dy, bool flip)
    {
        if (style.Fill is null && style.Stroke is null) return;
        IntPtr path = IntPtr.Zero;
        (float X, float Y) User(PdfPoint p)
        {
            var (x, y) = _page.ViewToUser.Apply(dx + p.X * scale, flip ? dy - p.Y * scale : dy + p.Y * scale);
            return ((float)x, (float)y);
        }
        foreach (var step in steps)
        {
            var (x, y) = User(step.End);
            switch (step.Kind)
            {
                case 'M' when path == IntPtr.Zero:
                    path = Native.FPDFPageObj_CreateNewPath(x, y);
                    if (path == IntPtr.Zero) throw new PdfException("Could not draw the path.");
                    break;
                case 'M': Native.FPDFPath_MoveTo(path, x, y); break;
                case 'L' when path != IntPtr.Zero: Native.FPDFPath_LineTo(path, x, y); break;
                case 'C' when path != IntPtr.Zero:
                    var (x1, y1) = User(step.Control1);
                    var (x2, y2) = User(step.Control2);
                    Native.FPDFPath_BezierTo(path, x1, y1, x2, y2, x, y);
                    break;
                case 'Z' when path != IntPtr.Zero: Native.FPDFPath_Close(path); break;
            }
        }
        if (path == IntPtr.Zero) return;
        if (style.Fill is { } f) Native.FPDFPageObj_SetFillColor(path, f.R, f.G, f.B, f.A);
        if (style.Stroke is { } s)
        {
            Native.FPDFPageObj_SetStrokeColor(path, s.R, s.G, s.B, s.A);
            Native.FPDFPageObj_SetStrokeWidth(path, (float)(style.StrokeWidth * (flip ? scale : 1)));
            Native.FPDFPageObj_SetLineCap(path, style.RoundEnds ? 1 : 0);
            Native.FPDFPageObj_SetLineJoin(path, style.RoundEnds ? 1 : 0);
        }
        // Fill modes: 0 none, 1 alternate (even-odd), 2 winding.
        Native.FPDFPath_SetDrawMode(path, style.Fill is null ? 0 : style.EvenOdd ? 1 : 2, style.Stroke is null ? 0 : 1);
        Native.FPDFPage_InsertObject(_page.Handle, path);
        _page.NewObjects.Add(path);
        _page.Dirty = true;
    }

    /// <summary>Draws a straight line of the given thickness.</summary>
    public void Line(PdfPoint from, PdfPoint to, double width, PdfColor? colour = null, bool roundEnds = false) =>
        Path([new('M', from), new('L', to)], new PathStyle { Stroke = colour ?? PdfColor.Black, StrokeWidth = width, RoundEnds = roundEnds });

    /// <summary>Draws a filled rectangle.</summary>
    public void Rectangle(double x, double y, double width, double height, PdfColor fill) =>
        Path([new('M', new PdfPoint(x, y)), new('L', new PdfPoint(x + width, y)), new('L', new PdfPoint(x + width, y + height)), new('L', new PdfPoint(x, y + height)), new('Z', new PdfPoint(x, y))],
             new PathStyle { Fill = fill });

    /// <summary>Draws one line of text with its start on the baseline at (x, baseline).</summary>
    public void Text(double x, double baseline, string text, TextStyle? style = null)
    {
        style ??= new TextStyle();
        if (style.Size <= 0) throw new ArgumentOutOfRangeException(nameof(style), "Font size must be positive.");
        if (text.Length == 0) return;
        // Text space has y up; flip it so the glyphs are upright in view space, then go to user space.
        var matrix = Affine.Concat(new Affine(1, 0, 0, -1, x, baseline), _page.ViewToUser);
        _owner.InsertTextObject(_page, text, style, matrix, out _);
    }

    /// <summary>The width a line of text takes in a standard font, in points.</summary>
    public double MeasureText(string text, TextStyle? style = null)
    {
        style ??= new TextStyle();
        return _owner.MeasureStandardText(text, style);
    }
}

public sealed unsafe partial class PdfDocument
{
    /// <summary>
    /// Draws on a page with a <see cref="PageCanvas"/>: everything drawn is added on top of what is
    /// there, and the page is written once, when <paramref name="draw"/> returns.
    /// </summary>
    public void Draw(int pageIndex, Action<PageCanvas> draw)
    {
        using var page = OpenPage(pageIndex);
        draw(new PageCanvas(this, page));
    }

    /// <summary>The width of a line of text in a standard font, in points.</summary>
    internal double MeasureStandardText(string text, TextStyle style)
    {
        var font = StandardFontHandle(StandardFontName(style));
        if (font == IntPtr.Zero) return text.Length * style.Size * 0.5;
        double total = 0;
        foreach (char ch in text)
        {
            float width = 0;
            if (Native.FPDFFont_GetGlyphWidth(font, (uint)ch, style.Size, &width) != 0) total += width;
            else total += style.Size * 0.5;
        }
        return total;
    }
}
