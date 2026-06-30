using System.Security.Cryptography;
using System.Text;

namespace AgentPulse.Infrastructure.Mutations;

internal sealed record TextFileSnapshot(
    string FullPath,
    string RelativePath,
    byte[] Bytes,
    string Text,
    TextEncodingDescriptor Encoding,
    string LineEnding,
    bool HasTrailingNewline,
    string Sha256,
    FileAttributes Attributes,
    UnixFileMode? UnixMode);

internal sealed record TextEncodingDescriptor(
    Encoding Encoding,
    string Name,
    int PreambleLength);

internal sealed record TextLineSegment(string Content, string Terminator);

internal static class TextFileCodec
{
    private static readonly UTF8Encoding Utf8NoBomStrict = new(false, true);
    private static readonly UTF8Encoding Utf8BomStrict = new(true, true);
    private static readonly UnicodeEncoding Utf16LeStrict = new(false, true, true);
    private static readonly UnicodeEncoding Utf16BeStrict = new(true, true, true);

    public static async Task<TextFileSnapshot> ReadAsync(
        string fullPath,
        string relativePath,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(fullPath);
        if (info.Length > maxBytes)
        {
            throw new MutationValidationException(
                $"The file '{relativePath}' is {info.Length} bytes and exceeds the maximum mutation size of {maxBytes} bytes.");
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        if (bytes.LongLength > maxBytes)
        {
            throw new MutationValidationException(
                $"The file '{relativePath}' changed while it was being read and now exceeds the maximum mutation size of {maxBytes} bytes.");
        }

        var descriptor = DetectEncoding(bytes);
        string text;
        try
        {
            text = descriptor.Encoding.GetString(
                bytes,
                descriptor.PreambleLength,
                bytes.Length - descriptor.PreambleLength);
        }
        catch (DecoderFallbackException)
        {
            throw new MutationValidationException(
                "The mutation tool only supports recognized text file encodings.");
        }

        var lineEnding = DetectLineEnding(text);
        var attributes = File.GetAttributes(fullPath);
        UnixFileMode? unixMode = null;
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                unixMode = File.GetUnixFileMode(fullPath);
            }
            catch (PlatformNotSupportedException)
            {
                unixMode = null;
            }
        }

        return new TextFileSnapshot(
            fullPath,
            relativePath,
            bytes,
            text,
            descriptor,
            lineEnding,
            HasTrailingNewline(text),
            ComputeSha256(bytes),
            attributes,
            unixMode);
    }

    public static byte[] EncodeNewFile(string content) => Utf8NoBomStrict.GetBytes(content);

    public static byte[] EncodePreserving(
        TextFileSnapshot snapshot,
        string content,
        bool normalizeLineEndings = true)
    {
        var value = normalizeLineEndings
            ? NormalizeLineEndings(content, snapshot.LineEnding)
            : content;
        var payload = snapshot.Encoding.Encoding.GetBytes(value);
        var preamble = snapshot.Encoding.Encoding.GetPreamble();
        if (preamble.Length == 0)
        {
            return payload;
        }

        var bytes = new byte[preamble.Length + payload.Length];
        Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
        Buffer.BlockCopy(payload, 0, bytes, preamble.Length, payload.Length);
        return bytes;
    }

    public static List<TextLineSegment> ParseLineSegments(string value)
    {
        var segments = new List<TextLineSegment>();
        var contentStart = 0;
        for (var index = 0; index < value.Length; index++)
        {
            string? terminator = null;
            if (value[index] == '\r')
            {
                terminator = index + 1 < value.Length && value[index + 1] == '\n'
                    ? "\r\n"
                    : "\r";
            }
            else if (value[index] == '\n')
            {
                terminator = "\n";
            }

            if (terminator is null)
            {
                continue;
            }

            segments.Add(new TextLineSegment(
                value[contentStart..index],
                terminator));
            index += terminator.Length - 1;
            contentStart = index + 1;
        }

        if (contentStart < value.Length)
        {
            segments.Add(new TextLineSegment(value[contentStart..], string.Empty));
        }

        return segments;
    }

    public static string RebuildLineSegments(IEnumerable<TextLineSegment> segments)
    {
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            builder.Append(segment.Content).Append(segment.Terminator);
        }

        return builder.ToString();
    }

    public static bool HasMixedLineEndings(string value)
    {
        string? first = null;
        foreach (var segment in ParseLineSegments(value))
        {
            if (segment.Terminator.Length == 0)
            {
                continue;
            }

            first ??= segment.Terminator;
            if (!string.Equals(first, segment.Terminator, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static string NormalizeLineEndings(string value, string lineEnding)
    {
        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", lineEnding, StringComparison.Ordinal);
    }

    public static string NormalizeForDiff(string value)
    {
        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    public static string ComputeSha256(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static string DescribeEncoding(byte[] bytes)
    {
        try
        {
            return DetectEncoding(bytes).Name;
        }
        catch (MutationValidationException)
        {
            return "unknown";
        }
    }

    public static string DescribeLineEnding(byte[] bytes)
    {
        try
        {
            var descriptor = DetectEncoding(bytes);
            var text = descriptor.Encoding.GetString(
                bytes,
                descriptor.PreambleLength,
                bytes.Length - descriptor.PreambleLength);
            if (!text.Contains('\r') && !text.Contains('\n'))
            {
                return "none";
            }

            return DetectLineEnding(text) switch
            {
                "\r\n" => "crlf",
                "\r" => "cr",
                _ => "lf",
            };
        }
        catch (Exception exception) when (exception is MutationValidationException or DecoderFallbackException)
        {
            return "unknown";
        }
    }

    public static string DetectLineEnding(string text)
    {
        var crlf = 0;
        var lf = 0;
        var cr = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    crlf++;
                    index++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[index] == '\n')
            {
                lf++;
            }
        }

        if (crlf >= lf && crlf >= cr && crlf > 0)
        {
            return "\r\n";
        }

        if (lf >= cr && lf > 0)
        {
            return "\n";
        }

        return cr > 0 ? "\r" : "\n";
    }

    public static bool HasTrailingNewline(string text) =>
        text.EndsWith("\n", StringComparison.Ordinal) ||
        text.EndsWith("\r", StringComparison.Ordinal);

    private static TextEncodingDescriptor DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return new TextEncodingDescriptor(Utf8BomStrict, "utf-8-bom", 3);
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            if ((bytes.Length - 2) % 2 != 0)
            {
                throw UnsupportedEncoding();
            }

            return new TextEncodingDescriptor(Utf16LeStrict, "utf-16-le-bom", 2);
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            if ((bytes.Length - 2) % 2 != 0)
            {
                throw UnsupportedEncoding();
            }

            return new TextEncodingDescriptor(Utf16BeStrict, "utf-16-be-bom", 2);
        }

        if (bytes.AsSpan().IndexOf((byte)0) >= 0)
        {
            throw UnsupportedEncoding();
        }

        try
        {
            _ = Utf8NoBomStrict.GetString(bytes);
            return new TextEncodingDescriptor(Utf8NoBomStrict, "utf-8", 0);
        }
        catch (DecoderFallbackException)
        {
            throw UnsupportedEncoding();
        }
    }

    private static MutationValidationException UnsupportedEncoding() =>
        new("The mutation tool only supports recognized text file encodings.");
}
