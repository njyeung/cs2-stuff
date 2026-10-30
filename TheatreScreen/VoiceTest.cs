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
    private int _speaker, _entity, _batch, _messageId, _sent, _messages;
    private long _started;

    public bool Running => _recipients != null;

    /// <param name="entity">Speaker's entity index for the newer "entity" field; -1 leaves it unset.</param>
    public string Start(int speakerSlot, int entity, RecipientFilter recipients, int batch)
    {
        _messageId = UserMessage.FindIdByName("CSVCMsg_VoiceData");
        if (_messageId <= 0) _messageId = 47; // CS2's id for CSVCMsg_VoiceData, as the Audio extension uses.
        _speaker = speakerSlot;
        _entity = entity;
        _batch = Math.Clamp(batch, 1, 8);
        _recipients = recipients;
        _sent = _messages = 0;
        _section++;
        _started = Stopwatch.GetTimestamp();
        return $"message id {_messageId}, speaker slot {_speaker}, entity {_entity}, {recipients.Count} recipients, " +
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
        // Every field is attempted so one run reports all the fields CounterStrikeSharp rejects.
        var rejected = new List<string>();
        void Set(string field, Action<string> set)
        {
            try { set(field); }
            catch (Exception ex) { rejected.Add($"{field}: {ex.Message}"); }
        }
        // Current CS2 renamed "client" (still field 2) and added "entity"; the Audio extension sets only field 2.
        Set("client_deprecated", f => message.SetInt(f, _speaker));
        if (_entity >= 0) Set("entity", f => message.SetInt(f, _entity));
        Set("xuid", f => message.SetUInt64(f, 0));
        Set("audio.format", f => message.SetInt(f, 2)); // VOICEDATA_FORMAT_OPUS
        Set("audio.voice_data", f => message.SetBytes(f, data));
        Set("audio.sequence_bytes", f => message.SetInt(f, 0));
        Set("audio.section_number", f => message.SetUInt(f, _section));
        Set("audio.sample_rate", f => message.SetUInt(f, 48000));
        Set("audio.num_packets", f => message.SetUInt(f, (uint)count));
        Set("audio.packet_offsets", f =>
        {
            var end = 0;
            for (var i = first; i < first + count; i++)
                message.AddUInt(f, (uint)(end += Tone[i].Length));
        });
        Set("audio.voice_level", f => message.SetFloat(f, 0.5f));
        if (rejected.Count > 0)
            throw new InvalidOperationException("rejected fields: " + string.Join("; ", rejected));
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
