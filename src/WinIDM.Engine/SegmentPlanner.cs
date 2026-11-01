using WinIDM.Core.Models;

namespace WinIDM.Engine;

/// <summary>
/// Segment (part) planner for multi-connection downloads (01 section 3.1, 9.4 A).
/// Segment count = min(n, ceil(size / 2 MiB)); segments are written directly at offsets in the final file.
/// </summary>
public static class SegmentPlanner
{
    /// <summary>Base block size used to compute the segment limit (2 MiB).</summary>
    public const long BlockSize = 2L * 1024 * 1024;

    /// <summary>Large file threshold for the advanced method (32 MiB).</summary>
    public const long AdvancedThreshold = 32L * 1024 * 1024;

    /// <summary>
    /// Computes the number of segments to use.
    /// </summary>
    /// <param name="length">File size in bytes; a value &lt;= 0 means unknown.</param>
    /// <param name="requestedSegments">Requested segment count (capped at 32).</param>
    /// <param name="domainPartLimit">Per-domain part limit; 0 means no limit.</param>
    /// <param name="advanced">If true and the file is &gt;= 32 MiB without a domain limit, the limit doubles (max 64).</param>
    public static int CountSegments(long length, int requestedSegments, int domainPartLimit, bool advanced)
    {
        if (length <= 0) return 1;

        var n = requestedSegments > 0 ? requestedSegments : 10;
        n = Math.Min(n, SettingDefaults.MaxParts);

        if (domainPartLimit <= 0 && advanced && length >= AdvancedThreshold)
        {
            n = Math.Min(n * 2, SettingDefaults.MaxPartsAdvanced);
        }

        var blocks = (length + BlockSize - 1) / BlockSize;
        if (blocks <= 1) return 1;

        n = (int)Math.Min(n, blocks);

        if (domainPartLimit > 0 && domainPartLimit < Math.Min(blocks, n))
        {
            n = domainPartLimit;
        }

        return Math.Max(1, n);
    }

    /// <summary>
    /// Splits <paramref name="length"/> into segments of nearly equal size. Ranges are inclusive.
    /// Remaining bytes are distributed to the first segments, so the total equals the file size exactly.
    /// </summary>
    public static List<SegmentInfo> Plan(long length, int requestedSegments, int domainPartLimit = 0, bool advanced = true, long downloadId = 0)
    {
        if (length <= 0)
        {
            return
            [
                new SegmentInfo { DownloadId = downloadId, Index = 0, Start = 0, End = -1, Downloaded = 0, State = SegmentState.Pending },
            ];
        }

        var count = CountSegments(length, requestedSegments, domainPartLimit, advanced);
        var baseSize = length / count;
        var remainder = length % count;

        var segments = new List<SegmentInfo>(count);
        long start = 0;
        for (var i = 0; i < count; i++)
        {
            var size = baseSize + (i < remainder ? 1 : 0);
            segments.Add(new SegmentInfo
            {
                DownloadId = downloadId,
                Index = i,
                Start = start,
                End = start + size - 1,
                Downloaded = 0,
                State = SegmentState.Pending,
            });
            start += size;
        }

        return segments;
    }
}
