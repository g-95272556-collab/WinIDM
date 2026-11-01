using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace WinIDM.Browser;

/// <summary>DNS record types supported by the resolver.</summary>
public enum DnsRecordType : ushort
{
    A = 1,
    AAAA = 28,
}

/// <summary>One address record in a DNS reply.</summary>
internal readonly record struct DnsAnswer(IPAddress Address, uint Ttl);

/// <summary>A DNS reply that has been parsed.</summary>
internal sealed record DnsReply(int Rcode, IReadOnlyList<DnsAnswer> Answers);

/// <summary>
/// DNS message encoding and parsing (RFC 1035) for A/AAAA queries.
/// Name compression pointers are supported with a jump limit to avoid loops.
/// </summary>
internal static class DnsWireFormat
{
    private const int HeaderLength = 12;
    private const int MaxNameLength = 255;
    private const int MaxPointerJumps = 32;

    public static byte[] BuildQuery(string asciiName, DnsRecordType type, ushort id)
    {
        var buffer = new List<byte>(HeaderLength + asciiName.Length + 6);

        Span<byte> header = stackalloc byte[HeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        header[2] = 0x01; // RD = 1
        header[3] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1); // QDCOUNT
        buffer.AddRange(header.ToArray());

        foreach (string label in asciiName.Split('.'))
        {
            if (label.Length == 0 || label.Length > 63)
                throw new ArgumentException("Invalid DNS label.", nameof(asciiName));
            byte[] bytes = Encoding.ASCII.GetBytes(label);
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }
        buffer.Add(0);

        buffer.Add((byte)((ushort)type >> 8));
        buffer.Add((byte)((ushort)type & 0xFF));
        buffer.Add(0x00);
        buffer.Add(0x01); // class IN
        return buffer.ToArray();
    }

    public static DnsReply ParseResponse(ReadOnlySpan<byte> message, ushort expectedId)
    {
        if (message.Length < HeaderLength) throw new FormatException("DNS message is too short.");

        ushort id = BinaryPrimitives.ReadUInt16BigEndian(message);
        if (id != expectedId) throw new FormatException("DNS reply ID does not match.");

        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        if ((flags & 0x8000) == 0) throw new FormatException("DNS message is not a reply.");
        int rcode = flags & 0x000F;

        int qdCount = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        int anCount = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);

        int pos = HeaderLength;
        for (int i = 0; i < qdCount; i++)
        {
            ReadName(message, ref pos);
            pos += 4; // QTYPE + QCLASS
            EnsureAvailable(message, pos, 0);
        }

        var answers = new List<DnsAnswer>();
        for (int i = 0; i < anCount; i++)
        {
            ReadName(message, ref pos);
            EnsureAvailable(message, pos, 10);
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(message[pos..]);
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(message[(pos + 4)..]);
            int rdLength = BinaryPrimitives.ReadUInt16BigEndian(message[(pos + 8)..]);
            pos += 10;
            EnsureAvailable(message, pos, rdLength);

            var rdata = message.Slice(pos, rdLength);
            if (type == (ushort)DnsRecordType.A && rdLength == 4)
                answers.Add(new DnsAnswer(new IPAddress(rdata), ttl));
            else if (type == (ushort)DnsRecordType.AAAA && rdLength == 16)
                answers.Add(new DnsAnswer(new IPAddress(rdata), ttl));
            pos += rdLength;
        }

        return new DnsReply(rcode, answers);
    }

    /// <summary>Read a DNS name and advance <paramref name="pos"/> past the original name (not past the pointer).</summary>
    private static string ReadName(ReadOnlySpan<byte> message, ref int pos)
    {
        var sb = new StringBuilder();
        int cursor = pos;
        bool jumped = false;
        int jumps = 0;

        while (true)
        {
            if (cursor >= message.Length) throw new FormatException("DNS name exceeds the message.");
            byte len = message[cursor];

            if ((len & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= message.Length) throw new FormatException("Truncated DNS pointer.");
                if (++jumps > MaxPointerJumps) throw new FormatException("DNS pointer loop.");
                int target = ((len & 0x3F) << 8) | message[cursor + 1];
                if (!jumped) pos = cursor + 2;
                jumped = true;
                cursor = target;
                continue;
            }

            if ((len & 0xC0) != 0) throw new FormatException("Unsupported DNS label type.");

            cursor++;
            if (len == 0)
            {
                if (!jumped) pos = cursor;
                break;
            }

            if (cursor + len > message.Length) throw new FormatException("Truncated DNS label.");
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(message.Slice(cursor, len)));
            cursor += len;
            if (sb.Length > MaxNameLength) throw new FormatException("DNS name is too long.");
        }

        return sb.ToString();
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> message, int pos, int count)
    {
        if (pos < 0 || pos + count > message.Length) throw new FormatException("Truncated DNS message.");
    }
}
