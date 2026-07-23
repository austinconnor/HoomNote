using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using HoomNote.Core.Documents;

namespace HoomNote.Infrastructure.Import;

/// <summary>
/// Reads the independently documented Samsung Notes SDOCX page container. Samsung has multiple
/// coordinate encodings in circulation, so each stroke is validated against its stored bounds and
/// decoded with the best matching layout. Unsupported objects are skipped without flattening ink.
/// Format references: https://github.com/bxff/samsung-notes-format (MIT) and the public format
/// documentation at https://github.com/twangodev/sdocx.
/// </summary>
internal static class SamsungNotesImportParser
{
    private static readonly HashSet<byte> SizedObjectTypes = [1, 2, 3, 4, 7, 8, 10, 11, 13, 14, 15, 17, 19, 20, 21, 23];
    private static readonly HashSet<byte> StrokeObjectTypes = [1, 15];
    private const long MaximumEntryBytes = 256L * 1024 * 1024;
    private const double MaximumPressure = 1400d;

    internal sealed record SamsungEmbeddedImage(
        int PageIndex, RectD Bounds, int ZIndex, string FileName, byte[] Data);
    internal sealed record ParsedSamsungNote(
        IReadOnlyList<NotePage> Pages,
        IReadOnlyList<SamsungEmbeddedImage> Images,
        IReadOnlyList<string> Warnings);
    private sealed record DecodedStroke(List<PointD> Points, int PointCount, int DeltaOffset);
    private sealed record StrokeMetadata(string Color, float Width, int MarkerOffset);
    private sealed record SamsungImagePlacement(int PageIndex, RectD Bounds, int ZIndex, int MediaIndex);

    public static Task<ParsedSamsungNote> ParseAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() => Parse(path, cancellationToken), cancellationToken);

    private static ParsedSamsungNote Parse(string path, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(path);
        // ZIP entry order matches Samsung's page order; alphabetic UUID sorting does not.
        var entries = archive.Entries
            .Where(entry => entry.FullName.EndsWith(".page", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (entries.Length == 0)
            throw new InvalidDataException("This Samsung Notes file does not contain any readable .page records.");

        var documentPaperColor = ReadDocumentPaperColor(archive);
        var mediaEntries = archive.Entries
            .Where(entry => entry.FullName.StartsWith("media/", StringComparison.OrdinalIgnoreCase) &&
                            IsSupportedImageExtension(Path.GetExtension(entry.FullName)))
            .OrderBy(entry => MediaOrdinal(entry.FullName))
            .ToArray();
        var pages = new List<NotePage>(entries.Length);
        var imagePlacements = new List<SamsungImagePlacement>();
        var nextMediaIndex = 0;
        var skippedObjects = 0;
        foreach (var (entry, pageIndex) in entries.Select((value, index) => (value, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Length <= 0 || entry.Length > MaximumEntryBytes)
                throw new InvalidDataException($"Samsung Notes page {pageIndex + 1} has an invalid size.");
            using var input = entry.Open();
            using var memory = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
            input.CopyTo(memory);
            pages.Add(ParsePage(memory.ToArray(), pageIndex, documentPaperColor, imagePlacements,
                ref nextMediaIndex, ref skippedObjects));
        }

        var images = new List<SamsungEmbeddedImage>();
        foreach (var placement in imagePlacements)
        {
            if (placement.MediaIndex < 0 || placement.MediaIndex >= mediaEntries.Length)
            {
                skippedObjects++;
                continue;
            }
            var media = mediaEntries[placement.MediaIndex];
            if (media.Length <= 0 || media.Length > MaximumEntryBytes)
            {
                skippedObjects++;
                continue;
            }
            using var mediaInput = media.Open();
            using var mediaMemory = new MemoryStream((int)Math.Min(media.Length, int.MaxValue));
            mediaInput.CopyTo(mediaMemory);
            images.Add(new SamsungEmbeddedImage(placement.PageIndex, placement.Bounds, placement.ZIndex,
                Path.GetFileName(media.FullName), mediaMemory.ToArray()));
        }

        var warnings = new List<string>
        {
            "Samsung handwriting was imported as editable vector ink with its original colors, widths, pressure, and page color."
        };
        if (images.Count > 0)
            warnings.Add($"{images.Count} placed Samsung image(s) were imported at their original page positions.");
        if (skippedObjects > 0)
            warnings.Add($"{skippedObjects} unsupported Samsung object(s), such as placed media or typed content, were skipped safely.");
        return new ParsedSamsungNote(pages, images, warnings);
    }

    private static NotePage ParsePage(byte[] data, int pageIndex, string? documentPaperColor,
        List<SamsungImagePlacement> imagePlacements, ref int nextMediaIndex, ref int skippedObjects)
    {
        var pageNumber = pageIndex + 1;
        if (data.Length < 32) throw new InvalidDataException($"Samsung Notes page {pageNumber} is truncated.");
        var baseOffset = checked((int)ReadUInt32(data, 0));
        var sourceWidth = ReadUInt32(data, 0x16);
        var sourceHeight = ReadUInt32(data, 0x1A);
        if (sourceWidth == 0 || sourceHeight == 0 || baseOffset < 0 || baseOffset >= data.Length)
            throw new InvalidDataException($"Samsung Notes page {pageNumber} has invalid dimensions or offsets.");

        const double targetWidth = 816;
        var scale = targetWidth / sourceWidth;
        var targetHeight = Math.Clamp(sourceHeight * scale, 256, 16384);
        var paperColor = ReadPagePaperColor(data, baseOffset) ?? documentPaperColor ?? "#FFFDF8";
        var defaultInkColor = IsDarkColor(paperColor) ? "#F5F5F5" : "#111111";
        var strokes = ReadStrokes(data, baseOffset, scale, defaultInkColor, pageIndex, imagePlacements,
            ref nextMediaIndex, ref skippedObjects);

        return new NotePage
        {
            Title = $"Page {pageNumber}",
            Size = new SizeD(targetWidth, targetHeight),
            Template = PageTemplate.For(PageTemplateKind.Blank) with { PaperColor = paperColor },
            Objects = [.. strokes]
        };
    }

    private static List<InkStrokeObject> ReadStrokes(byte[] data, int position, double scale, string defaultInkColor,
        int pageIndex, List<SamsungImagePlacement> imagePlacements, ref int nextMediaIndex, ref int skippedObjects)
    {
        var strokes = new List<InkStrokeObject>();
        try
        {
            var layerCount = ReadUInt16(data, position);
            position += 4;
            if (layerCount > 4096) throw new InvalidDataException("Samsung Notes layer count is invalid.");
            for (var layer = 0; layer < layerCount; layer++)
            {
                Ensure(data, position, 16);
                position += 12;
                var contentFlags = data[position - 1];
                position += 4;
                if ((contentFlags & 0x01) != 0) position += 1;
                if ((contentFlags & 0x02) != 0) position += 4;
                foreach (var bit in new[] { 0x04, 0x08 })
                {
                    if ((contentFlags & bit) == 0) continue;
                    var length = ReadInt16(data, position);
                    position += 2 + (length > 0 ? checked(length * 2) : 0);
                }
                if ((contentFlags & 0x10) != 0) position += 8;
                if ((contentFlags & 0x20) != 0) position += 4;

                var objectCount = ReadUInt32(data, position);
                position += 4;
                if (objectCount > 1_000_000) throw new InvalidDataException("Samsung Notes object count is invalid.");
                for (var objectIndex = 0U; objectIndex < objectCount; objectIndex++)
                {
                    Ensure(data, position, 3);
                    var type = data[position];
                    position += 3;
                    if (!SizedObjectTypes.Contains(type))
                    {
                        skippedObjects++;
                        continue;
                    }
                    var size = checked((int)ReadUInt32(data, position));
                    position += 4;
                    Ensure(data, position, size);
                    if (StrokeObjectTypes.Contains(type))
                    {
                        var stroke = ParseStroke(data.AsSpan(position, size), scale, defaultInkColor);
                        if (stroke is not null) strokes.Add(stroke with { ZIndex = checked((int)objectIndex) });
                        else skippedObjects++;
                    }
                    else if (type == 3 && ParseObjectBounds(data.AsSpan(position, size), scale) is { } bounds)
                    {
                        imagePlacements.Add(new SamsungImagePlacement(pageIndex, bounds,
                            checked((int)objectIndex), nextMediaIndex++));
                    }
                    else skippedObjects++;
                    position += size;
                }
                Ensure(data, position, 32);
                position += 32;
            }
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException or EndOfStreamException)
        {
            throw new InvalidDataException("A Samsung Notes page record is malformed or unsupported.", exception);
        }
        return strokes;
    }

    private static RectD? ParseObjectBounds(ReadOnlySpan<byte> data, double scale)
    {
        try
        {
            if (data.Length < 80) return null;
            var position = 4;
            if (ReadInt16(data, position) != 0) return null;
            position += 6;
            var flagLength = data[position];
            position += 3 + Math.Max(0, flagLength - 2);
            position += 9;
            var uuidLength = ReadInt16(data, position);
            position += 2 + Math.Max(0, (int)uuidLength);
            position += 8;
            Ensure(data, position, 32);
            var left = ReadDouble(data, position);
            var top = ReadDouble(data, position + 8);
            var right = ReadDouble(data, position + 16);
            var bottom = ReadDouble(data, position + 24);
            if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) ||
                !double.IsFinite(bottom) || right <= left || bottom <= top) return null;
            return new RectD(left * scale, top * scale, (right - left) * scale, (bottom - top) * scale);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException or EndOfStreamException)
        {
            return null;
        }
    }

    private static bool IsSupportedImageExtension(string extension) => extension.ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp";

    private static int MediaOrdinal(string fullName)
    {
        var fileName = Path.GetFileName(fullName);
        var separator = fileName.IndexOf('@');
        return separator > 0 && int.TryParse(fileName.AsSpan(0, separator), out var ordinal)
            ? ordinal
            : int.MaxValue;
    }

    private static InkStrokeObject? ParseStroke(ReadOnlySpan<byte> data, double scale, string defaultInkColor)
    {
        try
        {
            if (data.Length < 80) return null;
            var position = 4;
            if (ReadInt16(data, position) != 0) return null;
            position += 2;
            var variableOffset = checked((int)ReadUInt32(data, position));
            position += 4;
            var flagLength = data[position];
            position += 3 + Math.Max(0, flagLength - 2);
            position += 9;
            var uuidLength = ReadInt16(data, position);
            position += 2 + Math.Max(0, (int)uuidLength);
            position += 8;
            Ensure(data, position, 32);
            var left = ReadDouble(data, position);
            var top = ReadDouble(data, position + 8);
            var right = ReadDouble(data, position + 16);
            var bottom = ReadDouble(data, position + 24);
            position += 32;
            if (!(new[] { left, top, right, bottom }).All(double.IsFinite)) return null;

            var payloadOffset = variableOffset > 0 && variableOffset < data.Length ? variableOffset : position + 5;
            var payload = data[payloadOffset..];
            var decoded = DecodePoints(payload, right - left, bottom - top);
            if (decoded is null || decoded.Points.Count == 0) return null;
            var metadata = ReadStrokeMetadata(payload, defaultInkColor);
            var pressures = DecodePressure(payload, decoded.PointCount, decoded.DeltaOffset, metadata.MarkerOffset);

            var minX = decoded.Points.Min(point => point.X);
            var minY = decoded.Points.Min(point => point.Y);
            var imported = decoded.Points.Select((point, index) => new InkPoint(
                (point.X - minX + left) * scale,
                (point.Y - minY + top) * scale,
                PressureForPoint(pressures, index),
                TimestampMicroseconds: index * 1_000L)).ToList();
            var pressureEnabled = pressures.Count > 0 && pressures.Max() - pressures.Min() > 0.01f;
            var width = Math.Clamp(metadata.Width * (float)scale, 0.25f, 96f);
            var highlighter = width >= 8f;
            return new InkStrokeObject
            {
                Points = imported,
                Style = new InkStyle
                {
                    Tool = highlighter ? InkToolKind.Highlighter : InkToolKind.Pen,
                    Color = metadata.Color,
                    Width = width,
                    Opacity = highlighter ? 0.35f : 1f,
                    PressureEnabled = pressureEnabled && !highlighter,
                    PressureSensitivity = pressureEnabled ? 0.9f : 0f,
                    // Samsung has already fitted these high-density samples. Preserve its
                    // original centerline instead of applying HoomNote's live-ink fitter again.
                    Smoothing = 0f,
                    PreserveSourceGeometry = true
                }
            };
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException or EndOfStreamException)
        {
            return null;
        }
    }

    private static DecodedStroke? DecodePoints(ReadOnlySpan<byte> payload, double storedWidth, double storedHeight)
    {
        if (payload.Length < 64) return null;
        var padded = payload.Length >= 32 && payload[16..32].IndexOfAnyExcept((byte)0) < 0;
        var countOffset = padded ? 50 : 34;
        var deltaOffset = padded ? 76 : 60;
        if (payload.Length < deltaOffset + 4) return null;
        var count = checked((int)ReadUInt32(payload, countOffset));
        if (count is < 1 or > 200_000) return null;
        var deltaCount = Math.Min(Math.Max(0, count - 1), (payload.Length - deltaOffset) / 4);

        // Samsung stores each coordinate delta as one complete little-endian 16-bit
        // signed-magnitude fixed-point word (15 magnitude bits, 5 fractional bits).
        // Treating the high byte as a standalone sign/metadata flag truncates movement
        // above 7.96875 px and collapses ordinary handwriting into dots around its anchor.
        // The stored bounds are useful for positioning and validation, but must not be
        // used to choose a different decoder independently for every stroke.
        var points = DecodeCoordinates(payload, deltaOffset, deltaCount);
        RemoveTerminator(points);
        if (points.Count == 0 || !BoundsArePlausible(points, storedWidth, storedHeight)) return null;
        return new DecodedStroke(points, count, deltaOffset);
    }

    private static List<PointD> DecodeCoordinates(ReadOnlySpan<byte> payload, int offset, int count)
    {
        var points = new List<PointD>(count + 1) { new(0, 0) };
        var x = 0d;
        var y = 0d;
        for (var index = 0; index < count && offset + 4 <= payload.Length; index++, offset += 4)
        {
            x += DecodeCoordinateDelta(ReadUInt16(payload, offset));
            y += DecodeCoordinateDelta(ReadUInt16(payload, offset + 2));
            points.Add(new PointD(x, y));
        }
        return points;
    }

    private static void RemoveTerminator(List<PointD> points)
    {
        // Samsung appends two transport/terminator points. Keeping them creates hooks at stroke
        // ends, while removing them still preserves a one-point tap as a vector dot.
        if (points.Count > 2) points.RemoveRange(points.Count - 2, 2);
    }

    private static bool BoundsArePlausible(IReadOnlyList<PointD> points, double storedWidth, double storedHeight)
    {
        if (points.Count == 0 || !double.IsFinite(storedWidth) || !double.IsFinite(storedHeight)) return false;
        var width = points.Max(point => point.X) - points.Min(point => point.X);
        var height = points.Max(point => point.Y) - points.Min(point => point.Y);
        if (!double.IsFinite(width) || !double.IsFinite(height)) return false;
        // Bounds can include nib width and rounding, especially for dots and near-axis strokes.
        // Only reject grossly corrupt geometry; never rescale a genuine stroke to make it fit.
        return width <= Math.Abs(storedWidth) * 4 + 32 && height <= Math.Abs(storedHeight) * 4 + 32;
    }

    private static double DecodeCoordinateDelta(ushort value)
    {
        var magnitude = (value & 0x7FFF) / 32d;
        return (value & 0x8000) != 0 ? -magnitude : magnitude;
    }

    private static StrokeMetadata ReadStrokeMetadata(ReadOnlySpan<byte> payload, string defaultInkColor)
    {
        for (var offset = payload.Length - 14; offset >= 0; offset--)
        {
            ReadOnlySpan<byte> markerTail = [0x00, 0x01, 0x00, 0x00, 0x00];
            if (payload[offset] is not (0x02 or 0x03) ||
                !payload.Slice(offset + 1, 5).SequenceEqual(markerTail)) continue;
            var after = offset + 6;
            if (after + 4 > payload.Length) break;
            if (payload[after + 3] == 0xFF && after + 8 <= payload.Length)
            {
                var color = $"#{payload[after + 2]:X2}{payload[after + 1]:X2}{payload[after]:X2}";
                return new StrokeMetadata(color, ReadValidWidth(payload, after + 4), offset);
            }
            return new StrokeMetadata(defaultInkColor, ReadValidWidth(payload, after), offset);
        }
        return new StrokeMetadata(defaultInkColor, 0.8f, payload.Length);
    }

    private static float ReadValidWidth(ReadOnlySpan<byte> data, int offset)
    {
        Ensure(data, offset, 4);
        var width = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data[offset..]));
        return float.IsFinite(width) && width is >= 0.05f and <= 96f ? width : 0.8f;
    }

    private static List<float> DecodePressure(ReadOnlySpan<byte> payload, int pointCount, int deltaOffset, int markerOffset)
    {
        var channelCount = Math.Max(0, pointCount - 1);
        var offset = deltaOffset + checked(channelCount * 4) + 4;
        if (channelCount == 0 || offset < 0 || offset + checked(channelCount * 8) > Math.Min(payload.Length, markerOffset)) return [];
        var values = new List<float>(channelCount);
        var cumulative = 0;
        for (var index = 0; index < channelCount; index++, offset += 2)
        {
            var magnitude = payload[offset];
            cumulative += (payload[offset + 1] & 0x80) == 0 ? magnitude : -magnitude;
            if (cumulative is < -100 or > 2800) return [];
            values.Add((float)Math.Clamp(cumulative / MaximumPressure, 0.01, 1));
        }
        return values;
    }

    private static float PressureForPoint(IReadOnlyList<float> pressures, int pointIndex)
    {
        if (pressures.Count == 0) return 0.65f;
        return pressures[Math.Clamp(pointIndex - 1, 0, pressures.Count - 1)];
    }

    private static string? ReadPagePaperColor(ReadOnlySpan<byte> data, int baseOffset)
    {
        var offset = baseOffset switch
        {
            0x90 => 0x84,
            0xA6 => 0x80,
            _ => 0xA4
        };
        if (offset + 4 > data.Length || data[offset + 3] != 0xFF) return null;
        return $"#{data[offset + 2]:X2}{data[offset + 1]:X2}{data[offset]:X2}";
    }

    private static string? ReadDocumentPaperColor(ZipArchive archive)
    {
        var entry = archive.GetEntry("note.note");
        if (entry is null || entry.Length <= 0 || entry.Length > MaximumEntryBytes) return null;
        using var input = entry.Open();
        using var memory = new MemoryStream((int)entry.Length);
        input.CopyTo(memory);
        var data = memory.ToArray();
        ReadOnlySpan<byte> marker = [0x18, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00];
        for (var offset = 0; offset + 12 <= data.Length; offset++)
        {
            if (!data.AsSpan(offset, marker.Length).SequenceEqual(marker) || data[offset + 11] != 0xFF) continue;
            return $"#{data[offset + 8]:X2}{data[offset + 9]:X2}{data[offset + 10]:X2}";
        }
        return null;
    }

    private static bool IsDarkColor(string color)
    {
        if (color.Length != 7 || color[0] != '#' ||
            !byte.TryParse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red) ||
            !byte.TryParse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green) ||
            !byte.TryParse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue)) return false;
        return red * 0.2126 + green * 0.7152 + blue * 0.0722 < 105;
    }

    private static void Ensure(ReadOnlySpan<byte> data, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > data.Length - count) throw new EndOfStreamException();
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset)
    {
        Ensure(data, offset, 2);
        return BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    }

    private static short ReadInt16(ReadOnlySpan<byte> data, int offset)
    {
        Ensure(data, offset, 2);
        return BinaryPrimitives.ReadInt16LittleEndian(data[offset..]);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
    {
        Ensure(data, offset, 4);
        return BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    }

    private static double ReadDouble(ReadOnlySpan<byte> data, int offset)
    {
        Ensure(data, offset, 8);
        return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(data[offset..]));
    }
}
