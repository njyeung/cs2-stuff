using System.Diagnostics;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;

namespace TheatreScreen;

/// <summary>
/// Experiment: sends pre-encoded Opus as CSVCMsg_VoiceData straight from CounterStrikeSharp,
/// mirroring the Audio extension's packets, to learn whether voice works without that extension.
/// </summary>
public sealed class VoiceTest(Action<string> report)
{
    private const int FrameMs = 10; // 480 samples at 48 kHz, as the embedded packets were encoded.
    private const int LeadPackets = 3; // Sent ahead of the clock so the client never starves.
    private static readonly byte[][] Tone = LoadTone();
    private static uint _section;

    private RecipientFilter? _recipients;
    private int _speaker, _batch, _messageId, _sent, _messages;
    private long _started;

    public bool Running => _recipients != null;

    public string Start(int speakerSlot, RecipientFilter recipients, int batch)
    {
        _messageId = UserMessage.FindIdByName("CSVCMsg_VoiceData");
        if (_messageId <= 0) _messageId = 47; // CS2's id for CSVCMsg_VoiceData, as the Audio extension uses.
        _speaker = speakerSlot;
        _batch = Math.Clamp(batch, 1, 8);
        _recipients = recipients;
        _sent = _messages = 0;
        _section++;
        _started = Stopwatch.GetTimestamp();
        return $"message id {_messageId}, speaker slot {_speaker}, {recipients.Count} recipients, " +
               $"{Tone.Length} packets, {_batch} per message";
    }

    public void Stop() => _recipients = null;

    public void Tick()
    {
        if (_recipients is not { } recipients) return;
        var due = Math.Min(Tone.Length, (int)(Stopwatch.GetElapsedTime(_started).TotalMilliseconds / FrameMs) + LeadPackets);
        try
        {
            while (_sent < due)
            {
                var count = Math.Min(_batch, due - _sent);
                Send(recipients, _sent, count);
                _sent += count;
            }
        }
        catch (Exception ex)
        {
            Stop();
            report($"failed after {_messages} messages: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        if (_sent < Tone.Length) return;
        Stop();
        report($"finished: {_messages} messages, {_sent} packets in {Stopwatch.GetElapsedTime(_started).TotalSeconds:F2}s");
    }

    private void Send(RecipientFilter recipients, int first, int count)
    {
        using var message = UserMessage.FromId(_messageId);
        var data = Tone.Skip(first).Take(count).SelectMany(packet => packet).ToArray();
        message.SetInt("client", _speaker);
        message.SetUInt64("xuid", 0);
        message.SetInt("audio.format", 2); // VOICEDATA_FORMAT_OPUS
        message.SetBytes("audio.voice_data", data);
        message.SetInt("audio.sequence_bytes", 0);
        message.SetUInt("audio.section_number", _section);
        message.SetUInt("audio.sample_rate", 48000);
        message.SetUInt("audio.num_packets", (uint)count);
        var end = 0;
        for (var i = first; i < first + count; i++)
            message.AddUInt("audio.packet_offsets", (uint)(end += Tone[i].Length));
        message.SetFloat("audio.voice_level", 0.5f);
        if (_messages == 0)
            report($"first message ({message.Name}):\n{message.DebugString}");
        message.Send(recipients);
        _messages++;
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
