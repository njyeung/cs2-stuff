using System.Diagnostics;

namespace TheatreScreen;

/// <summary>
/// Experiment: sends pre-encoded Opus as CSVCMsg_VoiceData through TheatreVoice, paced like
/// the Audio extension, to learn whether voice works without that extension and ffmpeg.
/// </summary>
public sealed class VoiceTest(Action<string> report)
{
    private const int FrameMs = 10; // 480 samples at 48 kHz, as the embedded packets were encoded.
    private const int LeadPackets = 3; // Sent ahead of the clock so the client never starves.
    private static readonly byte[][] Tone = LoadTone();
    private static uint _section;

    private TheatreVoice? _voice;
    private ulong _recipients;
    private int _speaker, _entity, _batch, _sent, _messages;
    private long _started;

    public string Start(TheatreVoice voice, int speakerSlot, int entity, ulong recipients, int batch)
    {
        _voice = voice;
        _speaker = speakerSlot;
        _entity = entity;
        _batch = Math.Clamp(batch, 1, 8);
        _recipients = recipients;
        _sent = _messages = 0;
        _section++;
        _started = Stopwatch.GetTimestamp();
        return $"speaker slot {_speaker}, entity {_entity}, recipients 0x{recipients:x}, " +
               $"{Tone.Length} packets, {_batch} per message";
    }

    public void Stop() => _voice = null;

    public void Tick()
    {
        if (_voice is not { } voice) return;
        var due = Math.Min(Tone.Length, (int)(Stopwatch.GetElapsedTime(_started).TotalMilliseconds / FrameMs) + LeadPackets);
        try
        {
            while (_sent < due)
            {
                var count = Math.Min(_batch, due - _sent);
                var data = VoiceData.Build(_speaker, _entity, _section, Tone[_sent..(_sent + count)]);
                voice.Post(VoiceData.MessageId, data, _recipients);
                if (_messages++ == 0)
                    report($"first message posted: {data.Length} bytes");
                _sent += count;
            }
        }
        catch (Exception ex)
        {
            Stop();
            report($"failed after {_messages} messages: {ex.Message}");
            return;
        }
        if (_sent < Tone.Length) return;
        Stop();
        report($"finished: {_messages} messages, {_sent} packets in {Stopwatch.GetElapsedTime(_started).TotalSeconds:F2}s");
    }

    /// <summary>The embedded tone: 10 ms Opus packets, each prefixed by its little-endian u16 length.</summary>
    private static byte[][] LoadTone()
    {
        using var stream = typeof(VoiceTest).Assembly.GetManifestResourceStream("TheatreScreen.VoiceTest")
            ?? throw new InvalidOperationException("Embedded voice test tone is missing.");
        using var reader = new BinaryReader(stream);
        var packets = new List<byte[]>();
        while (stream.Position < stream.Length)
            packets.Add(reader.ReadBytes(reader.ReadUInt16()));
        return [.. packets];
    }
}
