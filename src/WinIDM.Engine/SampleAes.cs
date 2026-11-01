using System.Security.Cryptography;

namespace WinIDM.Engine;

/// <summary>
/// SAMPLE-AES decryption for MPEG-TS HLS segments. Only H.264 streams (type 0x1B) and AAC ADTS (type 0x0F) are recognized;
/// other streams (PAT, PMT, other types) are left untouched. Byte sizes do not change, so the output is the same length as the input.
/// <para>
/// Implemented rules (AES-128-CBC with the segment key and IV):
/// </para>
/// <list type="bullet">
/// <item>Video: for every NAL of type 1 (non-IDR slice) and 5 (IDR slice), the 1-byte NAL header and the first 32 bytes
/// after it stay clear. The remainder is split into full 16-byte blocks; blocks 0, 10, 20 ... are encrypted and
/// the rest stay clear (1:9 pattern). The IV is reset for each NAL, and the CBC chain only spans the encrypted blocks.</item>
/// <item>AAC audio: the ADTS header stays clear. All full 16-byte blocks of each frame are encrypted, and the IV is reset for each frame.</item>
/// </list>
/// <para>
/// These rules are interpreted from the HLS SAMPLE-AES specification and have not been verified against real encoder streams.
/// The tests use self-generated material (see the SampleAes tests).
/// </para>
/// </summary>
internal static class SampleAesDecryptor
{
    private const int PacketSize = 188;
    private const int SyncByte = 0x47;
    private const int BlockSize = 16;
    private const int ClearLeadBytes = 32;
    private const int EncryptedBlockStride = 10;
    private const int StreamTypeH264 = 0x1B;
    private const int StreamTypeAdts = 0x0F;

    /// <summary>
    /// Decrypts an MPEG-TS SAMPLE-AES segment. Throws <see cref="TransferException"/> with code 184 if the segment is not TS
    /// or has no PAT/PMT tables.
    /// </summary>
    public static byte[] DecryptTransportStream(byte[] data, byte[] key, byte[] iv)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(iv);

        if (data.Length == 0 || data.Length % PacketSize != 0 || !AllPacketsSynced(data))
        {
            throw new TransferException(EngineErrorCodes.UnsupportedPlaylist,
                "SAMPLE-AES segment is not valid MPEG-TS. SAMPLE-AES is only supported for MPEG-TS (not fMP4).",
                retryable: false);
        }

        var pids = FindStreams(data)
                   ?? throw new TransferException(EngineErrorCodes.UnsupportedPlaylist,
                       "No PAT/PMT tables found in the SAMPLE-AES segment.", retryable: false);

        var output = (byte[])data.Clone();
        if (pids.VideoPid is { } video)
        {
            ProcessElementaryStream(output, video, es => DecryptH264(es, key, iv));
        }

        if (pids.AudioPid is { } audio)
        {
            ProcessElementaryStream(output, audio, es => DecryptAdts(es, key, iv));
        }

        return output;
    }

    // ------------------------------------------------------------------
    // MPEG-TS mapping
    // ------------------------------------------------------------------

    private static bool AllPacketsSynced(byte[] data)
    {
        for (var pos = 0; pos < data.Length; pos += PacketSize)
        {
            if (data[pos] != SyncByte) return false;
        }

        return true;
    }

    private static int PidOf(byte[] data, int pos) => ((data[pos + 1] & 0x1F) << 8) | data[pos + 2];

    private static bool PayloadUnitStart(byte[] data, int pos) => (data[pos + 1] & 0x40) != 0;

    /// <summary>Payload offset within the packet, or -1 if there is no payload.</summary>
    private static int PayloadStart(byte[] data, int pos)
    {
        var control = (data[pos + 3] >> 4) & 0x3;
        if ((control & 0x1) == 0) return -1;

        var offset = pos + 4;
        if ((control & 0x2) != 0) offset += 1 + data[offset];
        return offset < pos + PacketSize ? offset : -1;
    }

    /// <summary>Start of the PSI section (after pointer_field) for a PUSI packet, or -1.</summary>
    private static int SectionStart(byte[] data, int pos)
    {
        if (!PayloadUnitStart(data, pos)) return -1;

        var payload = PayloadStart(data, pos);
        if (payload < 0) return -1;

        var section = payload + 1 + data[payload];

        // The PAT/PMT header needs 12 bytes within the same packet.
        return section + 12 <= pos + PacketSize ? section : -1;
    }

    /// <summary>Finds the video (H.264) and audio (ADTS) PIDs from the PAT and then the first PMT.</summary>
    private static (int? VideoPid, int? AudioPid)? FindStreams(byte[] data)
    {
        int? pmtPid = null;
        for (var pos = 0; pos < data.Length && pmtPid is null; pos += PacketSize)
        {
            if (PidOf(data, pos) != 0) continue;

            var section = SectionStart(data, pos);
            if (section < 0 || data[section] != 0x00) continue;

            var sectionLength = ((data[section + 1] & 0x0F) << 8) | data[section + 2];
            var end = Math.Min(section + 3 + sectionLength - 4, pos + PacketSize);
            for (var p = section + 8; p + 4 <= end; p += 4)
            {
                var program = (data[p] << 8) | data[p + 1];
                if (program == 0) continue;

                pmtPid = ((data[p + 2] & 0x1F) << 8) | data[p + 3];
                break;
            }
        }

        if (pmtPid is null) return null;

        for (var pos = 0; pos < data.Length; pos += PacketSize)
        {
            if (PidOf(data, pos) != pmtPid) continue;

            var section = SectionStart(data, pos);
            if (section < 0 || data[section] != 0x02) continue;

            var sectionLength = ((data[section + 1] & 0x0F) << 8) | data[section + 2];
            var programInfoLength = ((data[section + 10] & 0x0F) << 8) | data[section + 11];
            var end = Math.Min(section + 3 + sectionLength - 4, pos + PacketSize);

            int? video = null;
            int? audio = null;
            var p = section + 12 + programInfoLength;
            while (p + 5 <= end)
            {
                var type = data[p];
                var pid = ((data[p + 1] & 0x1F) << 8) | data[p + 2];
                var infoLength = ((data[p + 3] & 0x0F) << 8) | data[p + 4];
                if (type == StreamTypeH264) video ??= pid;
                else if (type == StreamTypeAdts) audio ??= pid;
                p += 5 + infoLength;
            }

            return (video, audio);
        }

        return null;
    }

    /// <summary>
    /// Collects the elementary payload bytes (after the PES header) for one PID, calls <paramref name="decrypt"/>
    /// on the combined stream, and writes those bytes back to their original packet positions.
    /// </summary>
    private static void ProcessElementaryStream(byte[] buffer, int pid, Action<byte[]> decrypt)
    {
        var ranges = new List<(int Offset, int Length)>();
        var pesHeaderRemaining = 0;

        for (var pos = 0; pos + PacketSize <= buffer.Length; pos += PacketSize)
        {
            if (PidOf(buffer, pos) != pid) continue;

            var start = PayloadStart(buffer, pos);
            if (start < 0) continue;

            if (PayloadUnitStart(buffer, pos))
            {
                // PES header: 00 00 01 stream_id, PES_packet_length, flags, PES_header_data_length, then data.
                var valid = start + 9 <= pos + PacketSize && buffer[start] == 0 && buffer[start + 1] == 0 && buffer[start + 2] == 1;
                if (!valid) continue;

                pesHeaderRemaining = 9 + buffer[start + 8];
            }

            var skip = Math.Min(pesHeaderRemaining, pos + PacketSize - start);
            pesHeaderRemaining -= skip;
            start += skip;

            var length = pos + PacketSize - start;
            if (length > 0) ranges.Add((start, length));
        }

        var total = ranges.Sum(r => r.Length);
        if (total == 0) return;

        var es = new byte[total];
        var at = 0;
        foreach (var (offset, length) in ranges)
        {
            Buffer.BlockCopy(buffer, offset, es, at, length);
            at += length;
        }

        decrypt(es);

        at = 0;
        foreach (var (offset, length) in ranges)
        {
            Buffer.BlockCopy(es, at, buffer, offset, length);
            at += length;
        }
    }

    // ------------------------------------------------------------------
    // H.264 video (Annex B)
    // ------------------------------------------------------------------

    private static void DecryptH264(byte[] es, byte[] key, byte[] iv)
    {
        foreach (var (header, end) in NalUnits(es))
        {
            var nalType = es[header] & 0x1F;
            if (nalType is not (1 or 5)) continue;

            var first = header + 1 + ClearLeadBytes;
            var blocks = new List<int>();
            for (var block = 0; first + (block + 1) * BlockSize <= end; block++)
            {
                if (block % EncryptedBlockStride == 0) blocks.Add(first + block * BlockSize);
            }

            if (blocks.Count == 0) continue;

            var cipher = new byte[blocks.Count * BlockSize];
            for (var i = 0; i < blocks.Count; i++) Buffer.BlockCopy(es, blocks[i], cipher, i * BlockSize, BlockSize);

            var plain = CbcDecrypt(cipher, key, iv);
            for (var i = 0; i < blocks.Count; i++) Buffer.BlockCopy(plain, i * BlockSize, es, blocks[i], BlockSize);
        }
    }

    /// <summary>
    /// Span of each NAL: (position of the NAL header byte, index after the last byte). Trailing zero bytes (before
    /// the next start code) are excluded.
    /// </summary>
    private static IEnumerable<(int Header, int End)> NalUnits(byte[] es)
    {
        var starts = new List<int>();
        for (var i = 0; i + 2 < es.Length; i++)
        {
            if (es[i] == 0 && es[i + 1] == 0 && es[i + 2] == 1)
            {
                starts.Add(i + 3);
                i += 2;
            }
        }

        for (var n = 0; n < starts.Count; n++)
        {
            var begin = starts[n];
            var limit = n + 1 < starts.Count ? starts[n + 1] - 3 : es.Length;
            var end = limit;
            while (end > begin && es[end - 1] == 0) end--;
            if (end > begin) yield return (begin, end);
        }
    }

    // ------------------------------------------------------------------
    // AAC audio (ADTS)
    // ------------------------------------------------------------------

    private static void DecryptAdts(byte[] es, byte[] key, byte[] iv)
    {
        var pos = 0;
        while (pos + 7 <= es.Length)
        {
            // ADTS sync: 12 bits of 1, layer 00.
            if (es[pos] != 0xFF || (es[pos + 1] & 0xF0) != 0xF0 || (es[pos + 1] & 0x06) != 0)
            {
                pos++;
                continue;
            }

            var headerLength = (es[pos + 1] & 0x01) == 1 ? 7 : 9;
            var frameLength = ((es[pos + 3] & 0x03) << 11) | (es[pos + 4] << 3) | ((es[pos + 5] & 0xE0) >> 5);
            if (frameLength < headerLength || pos + frameLength > es.Length) break;

            var payload = pos + headerLength;
            var count = (frameLength - headerLength) / BlockSize * BlockSize;
            if (count > 0)
            {
                var cipher = new byte[count];
                Buffer.BlockCopy(es, payload, cipher, 0, count);
                var plain = CbcDecrypt(cipher, key, iv);
                Buffer.BlockCopy(plain, 0, es, payload, count);
            }

            pos += frameLength;
        }
    }

    // ------------------------------------------------------------------
    // Cryptography
    // ------------------------------------------------------------------

    /// <summary>AES-128-CBC without padding. <paramref name="data"/> must be a multiple of 16 bytes.</summary>
    internal static byte[] CbcDecrypt(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, 0, data.Length);
    }
}
