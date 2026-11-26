using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    // All occurrences of the VCP mark use these three silhouettes, including the icon frames.
    private const string MarkPath = """
        F0 M 0,0 H 25 L 48,50 L 72,0 H 96 L 58,78 H 37 Z
        M 119,0 H 188 L 174,18 H 128 L 116,30 V 47 L 128,60 H 175 L 187,78 H 119 L 97,57 V 21 Z
        M 197,0 H 264 L 282,18 V 42 L 264,58 H 219 V 78 H 197 Z
        M 219,18 V 40 H 253 L 260,33 V 24 L 254,18 Z
        """;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--icon-only"))
            throw new ArgumentException("Pass the Brand output directory and optionally --icon-only.");
        var directory = Path.GetFullPath(args[0]);
        var mark = Geometry.Parse(MarkPath);
        mark.Freeze();
        if (args.Length == 1)
            File.WriteAllBytes(Path.Combine(directory, "VCP-Title.png"), RenderTitle(mark));
        WriteIcon(Path.Combine(directory, "VCP.ico"), mark);
    }

    private static byte[] RenderTitle(Geometry mark)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new ScaleTransform(3, 3));
            drawing.PushTransform(new TranslateTransform(3, 3));
            drawing.DrawGeometry(Brushes.White, null, mark);
            DrawCaption(drawing, mark.Bounds.Width);
            drawing.Pop();
            drawing.Pop();
        }
        return RenderPng(visual, 864, 336);
    }

    private static void DrawCaption(DrawingContext drawing, double markWidth)
    {
        var typeface = new Typeface(new FontFamily("Bahnschrift"), FontStyles.Normal,
            FontWeights.SemiBold, FontStretches.Normal);
        var letters = "VISUAL CONTROL PLANNER".Select(letter => new FormattedText(letter.ToString(),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 15, Brushes.White, 1)).ToArray();
        const double spacing = 3.1;
        double width = letters.Sum(letter => letter.WidthIncludingTrailingWhitespace) + (letters.Length - 1) * spacing;
        double x = (markWidth - width) / 2;
        foreach (var letter in letters)
        {
            drawing.DrawGeometry(Brushes.White, null, letter.BuildGeometry(new Point(x, 91)));
            x += letter.WidthIncludingTrailingWhitespace + spacing;
        }
    }

    private static byte[] RenderIcon(Geometry mark, int size)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            double outlinePixels = Math.Max(1, size * 0.028);
            double scale = (size * 0.95 - outlinePixels * 2) / mark.Bounds.Width;
            drawing.PushTransform(new TranslateTransform((size - mark.Bounds.Width * scale) / 2,
                (size - mark.Bounds.Height * scale) / 2));
            drawing.PushTransform(new ScaleTransform(scale, scale));
            var outline = new Pen(Brushes.Black, 2 * outlinePixels / scale) { LineJoin = PenLineJoin.Round };
            drawing.DrawGeometry(null, outline, mark);
            drawing.DrawGeometry(Brushes.White, null, mark);
            drawing.Pop();
            drawing.Pop();
        }
        return RenderPng(visual, size, size);
    }

    private static byte[] RenderPng(DrawingVisual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void WriteIcon(string path, Geometry mark)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var images = sizes.Select(size => RenderIcon(mark, size)).ToArray();
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (var index = 0; index < sizes.Length; index++)
        {
            byte dimension = (byte)(sizes[index] % 256);
            writer.Write(dimension);
            writer.Write(dimension);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(images[index].Length);
            writer.Write(offset);
            offset += images[index].Length;
        }
        foreach (var image in images)
            writer.Write(image);
    }
}
