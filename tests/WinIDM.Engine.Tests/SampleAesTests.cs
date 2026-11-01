using System.Text;
using WinIDM.Core.Models;
using WinIDM.Engine;
using WinIDM.Engine.Tests.Support;

namespace WinIDM.Engine.Tests;

/// <summary>
/// SAMPLE-AES decryption for MPEG-TS. Segments are generated and encrypted in the test (<see cref="SampleAesFixture"/>),
/// then decrypted by the engine code. This proves consistency and TS mapping, not conformance to a real encoder.
/// </summary>
public sealed class SampleAesTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 16).Select(i => (byte)(0x10 + i)).ToArray();
    private static readonly byte[] Iv = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();

    private static byte[] NonZero(int length, int seed)
    {
        var random = new System.Random(seed);
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = (byte)random.Next(1, 256);
        return bytes;
    }

    private static byte[] Decrypt(byte[] data, byte[]? key = null) =>
        SampleAesDecryptor.DecryptTransportStream(data, key ?? Key, Iv);

    private static string FinalPath(DownloadItem item) => Path.Combine(item.SaveDirectory, item.FileName);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void GeneratedSegment_DecryptsBackToPlainTransportStream(int seed)
    {
        var segment = SampleAesFixture.Generate(Key, Iv, seed);

        Assert.Equal(segment.Plain, Decrypt(segment.Encrypted));
    }

    [Fact]
    public void Encryption_ChangesOnlyPayloadBytesMarkedAsEncrypted()
    {
        // Fixture check: bytes marked as clear (TS header, PES, PAT/PMT, clear NAL/ADTS parts) do not change.
        var segment = SampleAesFixture.Generate(Key, Iv, seed: 7);

        Assert.True(segment.EncryptedBytes.Count(b => b) > 0);
        Assert.NotEqual(segment.Plain, segment.Encrypted);
        for (var i = 0; i < segment.Plain.Length; i++)
        {
            if (!segment.EncryptedBytes[i]) Assert.True(segment.Plain[i] == segment.Encrypted[i], $"Clear byte {i} changed.");
        }
    }

    [Fact]
    public void NalTypesOtherThanSliceAreNeverEncrypted()
    {
        // 0x67 = SPS (7), 0x06 = SEI (6): stay clear even when long.
        var segment = SampleAesFixture.Build(Key, Iv,
            [new SampleAesFixture.Nal(0x67, NonZero(500, 11)), new SampleAesFixture.Nal(0x06, NonZero(400, 12))],
            []);

        Assert.DoesNotContain(true, segment.EncryptedBytes);
        Assert.Equal(segment.Plain, segment.Encrypted);
        Assert.Equal(segment.Plain, Decrypt(segment.Encrypted));
    }

    [Fact]
    public void IdenticalSliceNals_DecryptCorrectly_BecauseIvResetsPerNal()
    {
        var payload = NonZero(300, 21);
        var segment = SampleAesFixture.Build(Key, Iv,
            [new SampleAesFixture.Nal(0x41, payload), new SampleAesFixture.Nal(0x41, payload)],
            []);

        Assert.Equal(segment.Plain, Decrypt(segment.Encrypted));
    }

    [Fact]
    public void AdtsFrames_OnlyFullBlocksAreEncrypted_AndRoundTrip()
    {
        // 40-byte frame: 32 bytes encrypted (2 blocks), 8 bytes stay clear. 47-byte frame: 32 bytes encrypted.
        var segment = SampleAesFixture.Build(Key, Iv, [], [NonZero(40, 31), NonZero(47, 32)]);

        Assert.Equal(64, segment.EncryptedBytes.Count(b => b));
        Assert.Equal(segment.Plain, Decrypt(segment.Encrypted));
    }

    [Fact]
    public void WrongKey_DoesNotRecoverPlainPayload()
    {
        var segment = SampleAesFixture.Generate(Key, Iv, seed: 8);
        var wrong = Enumerable.Range(0, 16).Select(i => (byte)(0xEE - i)).ToArray();

        Assert.NotEqual(segment.Plain, Decrypt(segment.Encrypted, wrong));
    }

    [Fact]
    public void NonTransportStreamData_IsRejectedWith184()
    {
        var ex = Assert.Throws<TransferException>(() => Decrypt(new byte[200]));

        Assert.Equal(EngineErrorCodes.UnsupportedPlaylist, ex.Code);
        Assert.Contains("MPEG-TS", ex.Message);
        Assert.False(ex.Retryable);
    }

    [Fact]
    public void TransportStreamWithoutPat_IsRejectedWith184()
    {
        // Two null TS packets (PID 0x1FFF) without PAT/PMT.
        var data = new byte[188 * 2];
        for (var pos = 0; pos < data.Length; pos += 188)
        {
            data[pos] = 0x47;
            data[pos + 1] = 0x1F;
            data[pos + 2] = 0xFF;
            data[pos + 3] = 0x10;
        }

        var ex = Assert.Throws<TransferException>(() => Decrypt(data));

        Assert.Equal(EngineErrorCodes.UnsupportedPlaylist, ex.Code);
        Assert.Contains("PAT", ex.Message);
    }

    [Fact]
    public async Task SampleAesMpegTs_IsDownloadedDecryptedAndSavedThroughEngine()
    {
        await using var h = new EngineHarness(s => s.Set(SettingKeys.ConvertTsToMp4, false));
        await using var server = new LocalHttpServer();
        var seg0 = SampleAesFixture.Generate(Key, Iv, seed: 61);
        var seg1 = SampleAesFixture.Generate(Key, Iv, seed: 62);

        server.Handler = req => req.Target switch
        {
            "/stream/media.m3u8" => Text(
                "#EXTM3U\n#EXT-X-TARGETDURATION:4\n" +
                $"#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"/key.bin\",IV=0x{Convert.ToHexString(Iv)}\n" +
                "#EXTINF:4,\n/stream/s0.ts\n#EXTINF:4,\n/stream/s1.ts\n#EXT-X-ENDLIST\n"),
            "/key.bin" => new ServerResponse { Status = 200, Reason = "OK", Body = Key },
            "/stream/s0.ts" => new ServerResponse { Status = 200, Reason = "OK", Body = seg0.Encrypted },
            "/stream/s1.ts" => new ServerResponse { Status = 200, Reason = "OK", Body = seg1.Encrypted },
            _ => new ServerResponse { Status = 404, Reason = "Not Found" },
        };

        var id = await h.Engine.AddAsync(new DownloadRequest { Url = server.BaseUrl + "/stream/media.m3u8", StartImmediately = true });
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(DownloadKind.Hls, item.Kind);
        Assert.Equal(seg0.Plain.Concat(seg1.Plain).ToArray(), File.ReadAllBytes(FinalPath(item)));
    }

    private static ServerResponse Text(string body) => new()
    {
        Status = 200,
        Reason = "OK",
        Body = Encoding.UTF8.GetBytes(body),
    };
}
