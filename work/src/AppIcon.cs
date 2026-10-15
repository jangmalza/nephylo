using System.Drawing;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace UsageWidget
{
    internal static class AppIcon
    {
        private const string ResourceName = "UsageWidget.AppIcon.ico";

        internal static Icon CreateTrayIcon()
        {
            try
            {
                using (var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName))
                {
                    if (stream != null)
                    {
                        using (var icon = new Icon(stream, Forms.SystemInformation.SmallIconSize))
                        {
                            // The caller owns this clone after both the decoder and stream close.
                            return (Icon)icon.Clone();
                        }
                    }
                }
            }
            catch { }

            // Test executables do not embed application resources. Never dispose a shared stock icon.
            return (Icon)SystemIcons.Application.Clone();
        }

        internal static ImageSource CreateWindowIcon()
        {
            try
            {
                using (var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName))
                {
                    if (stream == null) return null;
                    var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    BitmapFrame largest = null;
                    foreach (var frame in decoder.Frames)
                    {
                        if (largest == null || frame.PixelWidth * frame.PixelHeight > largest.PixelWidth * largest.PixelHeight)
                            largest = frame;
                    }
                    if (largest == null) return null;
                    largest.Freeze();
                    return largest;
                }
            }
            catch { return null; }
        }
    }
}
