param(
    [Parameter(Mandatory = $true)]
    [string]$IconPath
)

$ErrorActionPreference = 'Stop'
$resolvedIconPath = (Resolve-Path -LiteralPath $IconPath).Path
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$references = @(
    (Join-Path $frameworkDirectory 'System.dll'),
    (Join-Path $frameworkDirectory 'System.Drawing.dll'),
    (Join-Path $frameworkDirectory 'System.Xaml.dll'),
    (Join-Path $frameworkDirectory 'WPF/WindowsBase.dll'),
    (Join-Path $frameworkDirectory 'WPF/PresentationCore.dll')
)

# Test the consumers, rather than duplicating the SVG renderer or DIB encoder.
# Everything remains in memory; no windows, settings or installed files change.
$testSource = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Drawing.Color;

public static class AppIconNativeTests
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
    // WPF and GDI can round alpha blending differently by up to two RGB levels.
    // Apply this bound to every pixel; there is no allowance for corrupted pixels.
    private const int MaximumRgbDifference = 2;
    private static int assertions;
    private static int renderedPixels;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
        assertions++;
    }

    private static Dictionary<int, BitmapFrame> DecodeFrames(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Check(bytes.Length >= 6 + 16 * Sizes.Length, "ICO directory is complete.");
        Check(BitConverter.ToUInt16(bytes, 0) == 0 && BitConverter.ToUInt16(bytes, 2) == 1,
            "ICO header identifies an icon.");
        Check(BitConverter.ToUInt16(bytes, 4) == Sizes.Length, "ICO contains nine images.");
        for (int index = 0; index < Sizes.Length; index++)
        {
            int entry = 6 + 16 * index;
            int width = bytes[entry] == 0 ? 256 : bytes[entry];
            int height = bytes[entry + 1] == 0 ? 256 : bytes[entry + 1];
            uint length = BitConverter.ToUInt32(bytes, entry + 8);
            uint offset = BitConverter.ToUInt32(bytes, entry + 12);
            Check(width == Sizes[index] && height == Sizes[index], "ICO directory image dimensions match.");
            Check(bytes[entry + 2] == 0 && bytes[entry + 3] == 0
                && BitConverter.ToUInt16(bytes, entry + 4) == 1
                && BitConverter.ToUInt16(bytes, entry + 6) == 32,
                "ICO directory declares one 32-bit plane and no palette.");
            Check(length > 0 && offset >= 6 + 16 * Sizes.Length
                && (long)offset + length <= bytes.Length, "ICO image payload stays within the file.");
        }

        var result = new Dictionary<int, BitmapFrame>();
        using (var stream = File.OpenRead(path))
        {
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Check(decoder.Frames.Count == Sizes.Length, "WPF decodes all nine ICO images.");
            foreach (BitmapFrame frame in decoder.Frames)
            {
                Check(frame.PixelWidth == frame.PixelHeight && Array.IndexOf(Sizes, frame.PixelWidth) >= 0
                    && !result.ContainsKey(frame.PixelWidth), "WPF frame dimensions are unique and expected.");
                frame.Freeze();
                result.Add(frame.PixelWidth, frame);
            }
        }
        return result;
    }

    private static Bitmap ReferenceBitmap(BitmapFrame frame)
    {
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int width = frame.PixelWidth, height = frame.PixelHeight, stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < height; y++)
                Marshal.Copy(pixels, y * stride, IntPtr.Add(data.Scan0, y * data.Stride), stride);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    private static void CompareRendering(Icon icon, Bitmap reference, Color background, string state)
    {
        int size = reference.Width;
        Check(icon.Width == size && icon.Height == size, "Native Icon selects the requested image size.");
        using (var actual = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        using (var expected = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (Graphics graphics = Graphics.FromImage(expected))
            {
                graphics.Clear(background);
                graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                graphics.DrawImageUnscaled(reference, 0, 0);
            }
            using (Graphics graphics = Graphics.FromImage(actual))
            {
                graphics.Clear(background);
                graphics.DrawIcon(icon, new Rectangle(0, 0, size, size));
            }
            int maximum = 0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    Color native = actual.GetPixel(x, y), wanted = expected.GetPixel(x, y);
                    int difference = Math.Max(Math.Abs(native.R - wanted.R),
                        Math.Max(Math.Abs(native.G - wanted.G), Math.Abs(native.B - wanted.B)));
                    if (difference > MaximumRgbDifference)
                        throw new InvalidDataException(String.Format(
                            "Native icon corruption: {0}px {1} on {2}, pixel ({3},{4}), expected RGB({5},{6},{7}), actual RGB({8},{9},{10}), delta {11} exceeds {12}.",
                            size, state, background.Name, x, y, wanted.R, wanted.G, wanted.B,
                            native.R, native.G, native.B, difference, MaximumRgbDifference));
                    maximum = Math.Max(maximum, difference);
                    renderedPixels++;
                }
            assertions++;
            Console.WriteLine("PASS: native " + size + "px " + state + " on " + background.Name
                + ", maximum RGB difference " + maximum + ".");
        }
    }

    public static void Run(string path)
    {
        assertions = 0;
        renderedPixels = 0;
        Dictionary<int, BitmapFrame> frames = DecodeFrames(path);
        foreach (int size in Sizes)
        {
            if (size == 256) continue; // Largest PNG remains available to WPF/window consumers.
            using (Bitmap reference = ReferenceBitmap(frames[size]))
            {
                Icon clone = null;
                try
                {
                    using (var stream = File.OpenRead(path))
                    using (var original = new Icon(stream, new Size(size, size)))
                    {
                        CompareRendering(original, reference, Color.Black, "source-alive");
                        CompareRendering(original, reference, Color.White, "source-alive");
                        clone = (Icon)original.Clone();
                    }
                    // Match AppIcon.CreateTrayIcon: clone survives decoder and stream disposal.
                    CompareRendering(clone, reference, Color.Black, "clone-after-disposal");
                    CompareRendering(clone, reference, Color.White, "clone-after-disposal");
                }
                finally { if (clone != null) clone.Dispose(); }
            }
        }
        Console.WriteLine("App icon tests passed: " + assertions + " assertions, " + renderedPixels
            + " native rendered pixels; WPF nine-frame decoding and per-pixel GDI compatibility.");
    }
}
'@

try {
    Add-Type -TypeDefinition $testSource -ReferencedAssemblies $references
    [AppIconNativeTests]::Run($resolvedIconPath)
} catch {
    Write-Error -Message $_.Exception.GetBaseException().Message -ErrorAction Continue
    exit 1
}
