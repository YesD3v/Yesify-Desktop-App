using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Yesify.Services;

/// <summary>
/// YouTube's fragmented MP4 audio sometimes carries an edit list (moov/trak/edts) that trims ~36 ms of
/// encoder priming. Windows Media Foundation mishandles edit lists in fragmented files and decodes
/// zero samples, so the song "ends" instantly. Renaming the box to "free" (same size, nothing moves)
/// makes every file decode; the only cost is those 36 ms of silence.
/// </summary>
public static class Mp4Fixer
{
    public static void RemoveEditList(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            // The moov box sits in the first few KB of these files.
            var head = new byte[(int)Math.Min(fs.Length, 64 * 1024)];
            fs.ReadExactly(head);

            var moov = Find(head, 0, head.Length, "moov");
            if (moov < 0) return;
            var trak = Find(head, moov + 8, End(head, moov), "trak");
            if (trak < 0) return;
            var edts = Find(head, trak + 8, End(head, trak), "edts");
            if (edts < 0) return;

            fs.Position = edts + 4;
            fs.Write("free"u8);
        }
        catch
        {
            // Not an MP4 (e.g. WebM fallback) or the file is in use: leave it alone.
        }
    }

    private static int End(byte[] d, int box) =>
        (int)Math.Min(d.Length, box + BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(box)));

    private static int Find(byte[] d, int start, int end, string type)
    {
        for (var i = start; i + 8 <= end;)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i));
            if (size < 8) return -1;
            if (Encoding.ASCII.GetString(d, i + 4, 4) == type) return i;
            i += (int)Math.Min(size, int.MaxValue - i);
        }
        return -1;
    }
}
