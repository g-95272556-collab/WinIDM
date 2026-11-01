using System.Security.Cryptography;

namespace WinIDM.Engine.Tests.Support;

/// <summary>
/// SAMPLE-AES MPEG-TS segment generator for tests. Encryption is implemented here (test code), not with the decryptor code,
/// following the same rules as the <c>SampleAesDecryptor</c> documentation: NAL 1/5 with 32 clear bytes and a 1:9 pattern;
/// ADTS frames with full blocks encrypted and the IV reset. Both sides follow the same interpretation, so the test
/// proves consistency and TS mapping, not conformance to a real encoder.
/// </summary>
public static class SampleAesFixture
{
    public const int VideoPid = 0x100;
    public const int AudioPid = 0x101;
    public const int PmtPid = 0x1000;

    /// <summary>Plain segment, encrypted version, and the map of encrypted TS bytes (true = payload byte changed).</summary>
    public sealed record Segment(byte[] Plain, byte[] Encrypted, bool[] EncryptedBytes);

    /// <summary>One video NAL: the header byte (including the type) and the payload after it. The start code alternates.</summary>
    public sealed record Nal(byte Header, byte[] Payload);

    private sealed record Chunk(int Pid, byte StreamId, byte[] Plain, byte[] Encrypted, bool[] EncryptedMask);

    /// <summary>Generate a deterministic segment with random NALs and ADTS frames (payload bytes are non-zero, so there are no false start codes).</summary>
    public static Segment Generate(byte[] key, byte[] iv, int seed, int nalCount = 10, int frameCount = 10)
    {
        var random = new System.Random(seed);
        var nals = new List<Nal>();
        byte[] Payload(int length)
        {
            var bytes = new byte[length];
            for (var i = 0; i < length; i++) bytes[i] = (byte)random.Next(1, 256);
            return bytes;
        }

        // Mixed types: 5 and 1 (encrypted), 6 and 7 (stay clear).
        byte[] types = [0x65, 0x41, 0x06, 0x67];
        for (var i = 0; i < nalCount; i++)
        {
            nals.Add(new Nal(types[i % types.Length], Payload(random.Next(60, 1200))));
        }

        var frames = new List<byte[]>();
        for (var i = 0; i < frameCount; i++) frames.Add(Payload(random.Next(30, 300)));

        return Build(key, iv, nals, frames);
    }

    /// <summary>Build a segment from the given NALs and ADTS frames. Video and audio alternate (video first).</summary>
    public static Segment Build(byte[] key, byte[] iv, IReadOnlyList<Nal> nals, IReadOnlyList<byte[]> frames)
    {
        var chunks = new List<Chunk>();
        var max = Math.Max(nals.Count, frames.Count);
        for (var i = 0; i < max; i++)
        {
            if (i < nals.Count) chunks.Add(VideoChunk(key, iv, nals[i], i));
            if (i < frames.Count) chunks.Add(AudioChunk(key, iv, frames[i]));
        }

        var plain = Mux(chunks, useEncrypted: false, out _);
        var encrypted = Mux(chunks, useEncrypted: true, out var mask);
        return new Segment(plain, encrypted, mask);
    }

    /// <summary>Encrypt one NAL: 32 bytes after the header stay clear, then blocks 0, 10, ... are encrypted (CBC, IV reset).</summary>
    private static Chunk VideoChunk(byte[] key, byte[] iv, Nal nal, int index)
    {
        var startCode = index % 2 == 0 ? new byte[] { 0, 0, 0, 1 } : new byte[] { 0, 0, 1 };
        var plain = startCode.Concat(new[] { nal.Header }).Concat(nal.Payload).ToArray();
        var encrypted = (byte[])plain.Clone();
        var mask = new bool[plain.Length];

        var type = nal.Header & 0x1F;
        if (type is 1 or 5)
        {
            var first = startCode.Length + 1 + 32;
            var previous = iv;
            var blockIndex = 0;
            for (var offset = first; offset + 16 <= plain.Length; offset += 16, blockIndex++)
            {
                if (blockIndex % 10 != 0) continue;

                var xor = new byte[16];
                for (var i = 0; i < 16; i++) xor[i] = (byte)(plain[offset + i] ^ previous[i]);
                var cipher = EncryptBlock(key, xor);
                Array.Copy(cipher, 0, encrypted, offset, 16);
                for (var i = 0; i < 16; i++) mask[offset + i] = true;
                previous = cipher;
            }
        }

        return new Chunk(VideoPid, 0xE0, plain, encrypted, mask);
    }

    /// <summary>Encrypt one ADTS frame: the 7-byte header stays clear; full 16-byte blocks are encrypted with the IV reset.</summary>
    private static Chunk AudioChunk(byte[] key, byte[] iv, byte[] payload)
    {
        const int header = 7;
        var frameLength = header + payload.Length;
        var plain = new byte[frameLength];
        plain[0] = 0xFF;
        plain[1] = 0xF1; // MPEG-4, layer 00, protection_absent = 1
        plain[2] = 0x50;
        plain[3] = (byte)(0x80 | ((frameLength >> 11) & 0x03));
        plain[4] = (byte)((frameLength >> 3) & 0xFF);
        plain[5] = (byte)(((frameLength & 0x07) << 5) | 0x1F);
        plain[6] = 0xFC;
        Array.Copy(payload, 0, plain, header, payload.Length);

        var encrypted = (byte[])plain.Clone();
        var mask = new bool[plain.Length];
        var count = payload.Length / 16 * 16;
        var previous = iv;
        for (var offset = 0; offset < count; offset += 16)
        {
            var xor = new byte[16];
            for (var i = 0; i < 16; i++) xor[i] = (byte)(plain[header + offset + i] ^ previous[i]);
            var cipher = EncryptBlock(key, xor);
            Array.Copy(cipher, 0, encrypted, header + offset, 16);
            for (var i = 0; i < 16; i++) mask[header + offset + i] = true;
            previous = cipher;
        }

        return new Chunk(AudioPid, 0xC0, plain, encrypted, mask);
    }

    private static byte[] EncryptBlock(byte[] key, byte[] block)
    {
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(block, 0, 16);
    }

    // ------------------------------------------------------------------
    // Simple TS muxing: PAT, PMT, then PES for each part.
    // ------------------------------------------------------------------

    private static byte[] Mux(IReadOnlyList<Chunk> chunks, bool useEncrypted, out bool[] mask)
    {
        var ts = new List<byte>();
        var tsMask = new List<bool>();
        var counters = new Dictionary<int, int>();

        AppendPayload(ts, tsMask, counters, 0, pusi: true, Psi(new byte[] { 0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00, 0x00, 0x01, 0xF0, 0x00, 0, 0, 0, 0 }),
            new bool[17]);

        var pmt = new byte[]
        {
            0x02, 0xB0, 0x17, 0x00, 0x01, 0xC1, 0x00, 0x00,
            0xE0 | (VideoPid >> 8), VideoPid & 0xFF, 0xF0, 0x00,
            0x1B, 0xE0 | (VideoPid >> 8), VideoPid & 0xFF, 0xF0, 0x00,
            0x0F, 0xE0 | (AudioPid >> 8), AudioPid & 0xFF, 0xF0, 0x00,
            0, 0, 0, 0,
        };
        AppendPayload(ts, tsMask, counters, PmtPid, pusi: true, Psi(pmt), new bool[pmt.Length + 1]);

        foreach (var chunk in chunks)
        {
            var es = useEncrypted ? chunk.Encrypted : chunk.Plain;
            var pes = new byte[9 + es.Length];
            pes[2] = 1;
            pes[3] = chunk.StreamId;
            pes[6] = 0x80;
            Array.Copy(es, 0, pes, 9, es.Length);

            var pesMask = new bool[pes.Length];
            if (useEncrypted) Array.Copy(chunk.EncryptedMask, 0, pesMask, 9, es.Length);

            AppendPes(ts, tsMask, counters, chunk.Pid, pes, pesMask);
        }

        mask = tsMask.ToArray();
        return ts.ToArray();
    }

    /// <summary>PSI payload: pointer_field 0 followed by the table (CRC left empty; the parser does not check it).</summary>
    private static byte[] Psi(byte[] section) => new byte[] { 0x00 }.Concat(section).ToArray();

    private static void AppendPes(List<byte> ts, List<bool> tsMask, Dictionary<int, int> counters, int pid, byte[] pes, bool[] pesMask)
    {
        var offset = 0;
        var first = true;
        while (offset < pes.Length)
        {
            var take = Math.Min(184, pes.Length - offset);
            AppendPacket(ts, tsMask, counters, pid, first, pes.AsSpan(offset, take), pesMask.AsSpan(offset, take));
            offset += take;
            first = false;
        }
    }

    private static void AppendPayload(List<byte> ts, List<bool> tsMask, Dictionary<int, int> counters, int pid, bool pusi, byte[] payload, bool[] payloadMask)
    {
        var offset = 0;
        var first = pusi;
        while (offset < payload.Length)
        {
            var take = Math.Min(184, payload.Length - offset);
            AppendPacket(ts, tsMask, counters, pid, first, payload.AsSpan(offset, take), payloadMask.AsSpan(offset, take));
            offset += take;
            first = false;
        }
    }

    private static void AppendPacket(List<byte> ts, List<bool> tsMask, Dictionary<int, int> counters, int pid, bool pusi,
        ReadOnlySpan<byte> data, ReadOnlySpan<bool> dataMask)
    {
        counters.TryGetValue(pid, out var counter);
        counters[pid] = (counter + 1) & 0x0F;

        var packet = new byte[188];
        var packetMask = new bool[188];
        packet[0] = 0x47;
        packet[1] = (byte)((pusi ? 0x40 : 0) | ((pid >> 8) & 0x1F));
        packet[2] = (byte)(pid & 0xFF);

        var payloadStart = 4;
        if (data.Length < 184)
        {
            // Fill the remainder with an adaptation field (stuffing) so the packet stays 188 bytes.
            var afl = 183 - data.Length;
            packet[3] = (byte)(0x30 | counter);
            packet[4] = (byte)afl;
            if (afl > 0)
            {
                packet[5] = 0x00;
                for (var i = 6; i <= 4 + afl; i++) packet[i] = 0xFF;
            }

            payloadStart = 188 - data.Length;
        }
        else
        {
            packet[3] = (byte)(0x10 | counter);
        }

        for (var i = 0; i < data.Length; i++)
        {
            packet[payloadStart + i] = data[i];
            packetMask[payloadStart + i] = dataMask[i];
        }

        ts.AddRange(packet);
        tsMask.AddRange(packetMask);
    }
}
