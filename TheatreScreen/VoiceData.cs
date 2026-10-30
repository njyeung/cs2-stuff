namespace TheatreScreen;

/// <summary>
/// Serializes CSVCMsg_VoiceData by hand, as CS2's netmessages.proto defines it, because
/// CounterStrikeSharp cannot set its nested audio fields. TheatreVoice posts the bytes.
/// </summary>
public static class VoiceData
{
    public const int MessageId = 47; // CSVCMsg_VoiceData
    public const uint SampleRate = 48000;
    private const int FormatOpus = 2; // VOICEDATA_FORMAT_OPUS

    /// <param name="speakerSlot">Player slot the voice appears to come from (client_deprecated).</param>
    /// <param name="entity">Speaker's entity index; negative leaves the field unset.</param>
    /// <param name="section">Identifies one continuous stream; change it when a new one starts.</param>
    /// <param name="sequenceBytes">Opus bytes sent earlier in this section (the Audio extension always sends 0).</param>
    /// <param name="sampleOffset">Samples sent earlier in this section; null leaves it unset, as the Audio extension does.</param>
    /// <param name="packets">Consecutive Opus packets sent together in this message.</param>
    public static byte[] Build(int speakerSlot, int entity, uint section, int sequenceBytes, uint? sampleOffset,
        IReadOnlyList<byte[]> packets, float voiceLevel = 0.5f)
    {
        var audio = new ProtoWriter();
        audio.Varint(1, FormatOpus);
        audio.Bytes(2, packets.SelectMany(packet => packet).ToArray());
        audio.Varint(3, (ulong)(long)sequenceBytes);
        audio.Varint(4, section);
        audio.Varint(5, SampleRate);
        if (sampleOffset is { } offset)
            audio.Varint(6, offset); // uncompressed_sample_offset
        audio.Varint(7, (ulong)packets.Count);
        var offsets = new ProtoWriter();
        var end = 0;
        foreach (var packet in packets)
            offsets.RawVarint((ulong)(end += packet.Length)); // Each packet's end within voice_data.
        audio.Bytes(8, offsets.ToArray()); // packet_offsets is packed.
        audio.Fixed32(9, BitConverter.SingleToUInt32Bits(voiceLevel));

        var message = new ProtoWriter();
        message.Bytes(1, audio.ToArray());
        message.Varint(2, (ulong)(long)speakerSlot); // client_deprecated, formerly client
        message.Fixed64(4, 0); // xuid
        if (entity >= 0)
            message.Varint(8, (ulong)entity);
        return message.ToArray();
    }

    private sealed class ProtoWriter
    {
        private readonly MemoryStream _buffer = new();

        public void Varint(int field, ulong value)
        {
            RawVarint((ulong)field << 3);
            RawVarint(value);
        }

        public void Bytes(int field, byte[] value)
        {
            RawVarint((ulong)field << 3 | 2);
            RawVarint((ulong)value.Length);
            _buffer.Write(value);
        }

        public void Fixed32(int field, uint value)
        {
            RawVarint((ulong)field << 3 | 5);
            Span<byte> bytes = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            _buffer.Write(bytes);
        }

        public void Fixed64(int field, ulong value)
        {
            RawVarint((ulong)field << 3 | 1);
            Span<byte> bytes = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            _buffer.Write(bytes);
        }

        public void RawVarint(ulong value)
        {
            for (; value >= 0x80; value >>= 7)
                _buffer.WriteByte((byte)(value | 0x80));
            _buffer.WriteByte((byte)value);
        }

        public byte[] ToArray() => _buffer.ToArray();
    }
}
