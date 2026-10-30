using TheatreScreen;

static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
}

// Expected bytes come from Google's protobuf library encoding the same message with the
// hl2sdk-cs2 netmessages.proto schema (which matches the live server's descriptor).
// Packet 2 is 200 bytes so voice_data, the audio field and an offset need multi-byte varints.
byte[][] packets = [[1, 2, 3], Enumerable.Repeat((byte)4, 200).ToArray(), [5]];
var expected = Convert.FromHexString(
    "0ae701080212cc01010203040404040404040404040404040404040404040404040404040404040404040404040404040404" +
    "0404040404040404040404040404040404040404040404040404040404040404040404040404040404040404040404040404" +
    "0404040404040404040404040404040404040404040404040404040404040404040404040404040404040404040404040404" +
    "0404040404040404040404040404040404040404040404040404040404040404040404040404040404040404040404040404" +
    "040404040404040404040405180020072880f7023803420503cb01cc014d0000003f10032100000000000000004004");
var actual = VoiceData.Build(speakerSlot: 3, entity: 4, section: 7, sequenceBytes: 0, sampleOffset: null, packets);
Check(actual.SequenceEqual(expected), $"CSVCMsg_VoiceData bytes differ:\n{Convert.ToHexString(actual)}");

// Without an entity the message ends after xuid.
var noEntity = VoiceData.Build(3, -1, 7, 0, null, packets);
Check(noEntity.AsSpan().SequenceEqual(expected.AsSpan(0, expected.Length - 2)), "Negative entity was serialized");

// Mid-stream: sequence_bytes 300 and uncompressed_sample_offset 1440 (both multi-byte varints).
var midStream = Convert.FromHexString(
    "0aeb01080212cc01010203040404040404040404040404040404040404040404040404040404040404040404040404040404" +
    "0404040404040404040404040404040404040404040404040404040404040404040404040404040404040404040404040404" +
    "0404040404040404040404040404040404040404040404040404040404040404040404040404040404040404040404040404" +
    "0404040404040404040404040404040404040404040404040404040404040404040404040404040404040404040404040404" +
    "04040404040404040404040518ac0220072880f70230a00b3803420503cb01cc014d0000003f100321000000000000000040" +
    "04");
var filled = VoiceData.Build(3, 4, 7, sequenceBytes: 300, sampleOffset: 1440, packets);
Check(filled.SequenceEqual(midStream), $"Mid-stream CSVCMsg_VoiceData bytes differ:\n{Convert.ToHexString(filled)}");

Console.WriteLine("VoiceData checks passed");
