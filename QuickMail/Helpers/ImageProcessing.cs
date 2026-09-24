using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickMail.Helpers;

/// <summary>A picture ready to go into a message: its bytes, type and pixel size as it is seen (orientation applied).</summary>
public sealed record PreparedImage(byte[] Bytes, string ContentType, int PixelWidth, int PixelHeight);

/// <summary>Why a picture could not be taken.</summary>
public enum ImageRejection { None, NotAPicture, TooLarge }

/// <summary>
/// Reading, shrinking and re-encoding pictures for compose (#729), through WPF's own imaging
/// (WIC), so no library is added. Mail clients reliably show PNG, JPEG and GIF; anything else
/// WIC can read (BMP, TIFF, ICO) is converted to PNG before it is sent.
///
/// Every method here is safe to run off the UI thread — the bitmaps it makes are frozen — and
/// the compose window does run the decoding and encoding on the thread pool, so a large photo
/// never freezes the window.
/// </summary>
public static class ImageProcessing
{
    /// <summary>Pictures wider than this are offered a shrink (Kelly's decision, #729).</summary>
    public const int ShrinkThreshold = 1600;

    /// <summary>A message larger than this gets a one-time warning.</summary>
    public const long LargeMessageBytes = 10L * 1024 * 1024;

    /// <summary>
    /// The most pixels a picture may declare. Decoding allocates four bytes a pixel, so a small
    /// file that claims to be 30000 by 30000 would ask for gigabytes; anything past this is
    /// refused from its header, before any pixels are read.
    /// </summary>
    public const long MaxPixels = 60_000_000;

    /// <summary>Files larger than this are not read at all.</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;

    /// <summary>
    /// Reads a picture, converting it to a format mail clients show when it is not one. Returns
    /// null with the reason when the bytes are not a picture WPF can read, or are too large.
    /// </summary>
    public static PreparedImage? Prepare(byte[] bytes, out ImageRejection rejection)
    {
        rejection = ImageRejection.NotAPicture;
        try
        {
            var header = ReadHeader(bytes);
            if (header is null) return null;
            var (width, height, orientation, type) = header.Value;
            if ((long)width * height > MaxPixels)
            {
                rejection = ImageRejection.TooLarge;
                return null;
            }

            rejection = ImageRejection.None;
            var (seenWidth, seenHeight) = SwapsAxes(orientation) ? (height, width) : (width, height);
            if (type is not null)
                return new PreparedImage(bytes, type, seenWidth, seenHeight);

            // Not a format mail clients show: decode (orientation applied) and send as PNG.
            var upright = Upright(Decode(bytes), orientation);
            return new PreparedImage(Encode(upright, "image/png"), "image/png", upright.PixelWidth, upright.PixelHeight);
        }
        catch (Exception ex) when (IsImagingFailure(ex))
        {
            rejection = ImageRejection.NotAPicture;
            return null;
        }
    }

    /// <inheritdoc cref="Prepare(byte[], out ImageRejection)"/>
    public static PreparedImage? Prepare(byte[] bytes) => Prepare(bytes, out _);

    /// <summary>A picture from the clipboard, as PNG.</summary>
    public static PreparedImage FromBitmap(BitmapSource bitmap) =>
        new(Encode(bitmap, "image/png"), "image/png", bitmap.PixelWidth, bitmap.PixelHeight);

    /// <summary>
    /// The picture scaled down to <paramref name="maxWidth"/> pixels wide, keeping its shape and
    /// turned upright (a phone photo's orientation is a tag WPF ignores, and the re-encoded file
    /// carries no tags). JPEG stays JPEG; anything else becomes PNG — a GIF keeps only its first
    /// frame. A picture already that narrow comes back unchanged.
    /// </summary>
    public static PreparedImage Shrink(PreparedImage image, int maxWidth)
    {
        if (image.PixelWidth <= maxWidth) return image;
        var orientation = ReadHeader(image.Bytes)?.Orientation ?? 1;
        var upright = Upright(Decode(image.Bytes), orientation);
        double scale = (double)maxWidth / upright.PixelWidth;
        var scaled = new TransformedBitmap(upright, new ScaleTransform(scale, scale));
        scaled.Freeze();
        var type = image.ContentType == "image/jpeg" ? "image/jpeg" : "image/png";
        return new PreparedImage(Encode(scaled, type), type, scaled.PixelWidth, scaled.PixelHeight);
    }

    /// <summary>
    /// A frozen, upright bitmap for drawing the picture in the editor. A picture wider than the
    /// editor shows (×2 for high-DPI screens) is decoded at that width so a large photo does not
    /// hold its full size in memory; a smaller one keeps its own size. Null when the bytes
    /// cannot be read or declare too many pixels.
    /// </summary>
    public static BitmapSource? ForDisplay(byte[] bytes)
    {
        try
        {
            var header = ReadHeader(bytes);
            if (header is null) return null;
            var (width, height, orientation, _) = header.Value;
            if ((long)width * height > MaxPixels) return null;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            int displayWidth = (int)(RichTextDocumentConverter.MaxEditorImageWidth * 2);
            if (width > displayWidth)
                bitmap.DecodePixelWidth = displayWidth;
            bitmap.EndInit();
            bitmap.Freeze();
            return Upright(bitmap, orientation);
        }
        catch (Exception ex) when (IsImagingFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Pixel size, EXIF orientation (1 when absent) and — for PNG, JPEG and GIF — the content
    /// type, read from the header without decoding any pixels. Null when it is not a picture.
    /// </summary>
    private static (int Width, int Height, int Orientation, string? ContentType)? ReadHeader(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            var type = decoder switch
            {
                PngBitmapDecoder => "image/png",
                JpegBitmapDecoder => "image/jpeg",
                GifBitmapDecoder => "image/gif",
                _ => null,
            };
            int orientation = 1;
            if (frame.Metadata is BitmapMetadata metadata)
            {
                try
                {
                    if (metadata.ContainsQuery("/app1/ifd/{ushort=274}")
                        && metadata.GetQuery("/app1/ifd/{ushort=274}") is ushort o && o is >= 1 and <= 8)
                        orientation = o;
                }
                catch (Exception ex) when (IsImagingFailure(ex)) { /* no usable orientation */ }
            }
            return (frame.PixelWidth, frame.PixelHeight, orientation, type);
        }
        catch (Exception ex) when (IsImagingFailure(ex))
        {
            return null;
        }
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze();
        return frame;
    }

    /// <summary>EXIF orientations 5–8 are turned a quarter, so width and height swap.</summary>
    private static bool SwapsAxes(int orientation) => orientation is >= 5 and <= 8;

    /// <summary>The bitmap turned and mirrored as its EXIF orientation says it should be seen.</summary>
    private static BitmapSource Upright(BitmapSource source, int orientation)
    {
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1) } },
            8 => new RotateTransform(270),
            _ => null,
        };
        if (transform is null) return source;
        var turned = new TransformedBitmap(source, transform);
        turned.Freeze();
        return turned;
    }

    private static byte[] Encode(BitmapSource source, string contentType)
    {
        BitmapEncoder encoder = contentType == "image/jpeg"
            ? new JpegBitmapEncoder { QualityLevel = 85 }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static bool IsImagingFailure(Exception ex) =>
        ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException
            or IOException or OutOfMemoryException or OverflowException
            or System.Runtime.InteropServices.COMException;

    /// <summary>The file types Insert Image offers.</summary>
    public const string OpenFileFilter =
        "Pictures (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff|All files (*.*)|*.*";

    /// <summary>True for a file name that looks like a picture — for choosing embed over attach on drop.</summary>
    public static bool IsImageFileName(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff";
}
