using System.Buffers.Binary;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TheatreScreen;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static async Task Until(Func<bool> condition, string message)
{
    using var stop = new CancellationTokenSource(4000);
    while (!condition()) { if (stop.IsCancellationRequested) throw new Exception(message); await Task.Delay(5); }
}
static byte[] Header(int width = 1, int height = 1, int fps = 10)
{
    var bytes = new byte[16];
    var fields = new[] { 0x314d5354, width, height, fps };
    for (var i = 0; i < fields.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), fields[i]);
    return bytes;
}
static byte[] Record(char tag, uint index, byte[] data)
{
    var bytes = new byte[5 + (tag == 'A' ? 2 : 0) + data.Length];
    bytes[0] = (byte)tag; BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), index);
    if (tag == 'A') BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(5), (ushort)data.Length);
    data.CopyTo(bytes, bytes.Length - data.Length);
    return bytes;
}
static byte[] Body(int frames)
{
    using var body = new MemoryStream(); body.Write(Header());
    for (uint i = 0; i < frames; i++)
    {
        body.Write(Record('V', i, [(byte)i, 0, 0]));
        for (uint j = 0; j < 10; j++) body.Write(Record('A', i * 10 + j, [0xF0, (byte)j]));
    }
    return body.ToArray();
}
static async Task Invalid(Func<Task> read, string message)
{
    try { await read(); throw new Exception(message); }
    catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { }
}

var header = await AvStreamHeader.ReadAsync(new FragmentedStream(Header()), default);
Check(header == new AvStreamHeader(1, 1, 10), "Fragmented TSM1 header");
foreach (var bytes in new[] { Header(width: 0), Header(height: 257), Header(fps: 31), new byte[16], Header()[..12] })
    await Invalid(async () => { await AvStreamHeader.ReadAsync(new MemoryStream(bytes), default); }, "Accepted malformed header");
var frames = new List<uint>(); var packets = new List<uint>();
var body = Body(6)[16..];
await AvRecords.ReadAsync(new FragmentedStream(body), header,
    (i, rgb) => { frames.Add(i); Check(rgb.Length == 3, "Frame length"); return ValueTask.CompletedTask; },
    (i, packet) => { packets.Add(i); Check(packet.Length == 2, "Opus length"); return ValueTask.CompletedTask; }, default);
Check(frames.SequenceEqual(Enumerable.Range(0, 6).Select(i => (uint)i)) && packets.Count == 60, "Lost interleaved records");
foreach (var bad in new[] { body[..^1], Record('X', 0, []), Record('V', 2, [0, 0, 0]), Record('A', 1, [1]), Record('A', 0, []), Array.Empty<byte>() })
    await Invalid(() => AvRecords.ReadAsync(new MemoryStream(bad), header,
        (_, _) => ValueTask.CompletedTask, (_, _) => ValueTask.CompletedTask, default), "Accepted malformed record/absent track");

// Deterministic tick clock: batches, sequence bytes, sample offsets, mute-independent
// consumption, starvation, resumption without a burst, and the final partial batch.
var queue = new Queue<byte[]>(Enumerable.Range(0, 30).Select(_ => new byte[] { 1, 2 }));
var clock = new AvPlayback();
var messages = new List<(int Count, int Bytes, uint Samples)>();
byte[][] Take(int count) => Enumerable.Range(0, count).Select(_ => queue.Dequeue()).ToArray();
void Send(byte[][] p, int bytes, uint samples) => messages.Add((p.Length, bytes, samples));
void Tick(double delta, bool ready = true, bool ended = false) => clock.Tick(delta, ready, ended, () => queue.Count, Take, Send);
Tick(1, ready: false);
Check(clock.SentPackets == 0, "Advanced before prebuffer");
Tick(0);
Check(messages.Single() == (3, 0, 0), "Initial three-packet lead");
for (var i = 0; i < 9; i++) Tick(0.03);
Check(clock.SentPackets == 30 && Math.Abs(clock.Seconds - 0.30) < 1e-8, "Audio clock drift");
Check(messages[1] == (3, 6, 1440), "Running sequence/sample offsets");
for (var i = 0; i < 100; i++) Tick(0.03);
Check(clock.SentPackets == 30, "Clock moved during audio starvation");
foreach (var _ in Enumerable.Range(0, 30)) queue.Enqueue([1, 2]);
Tick(0.03);
Check(clock.SentPackets == 33, "Starvation resume burst");
Tick(10);
Check(clock.SentPackets == 39, "Unbounded hitch catch-up");
queue.Clear(); queue.Enqueue([1, 2]); queue.Enqueue([1, 2]);
Tick(0.02, ended: true);
Check(messages[^1].Count == 2 && clock.SentPackets == 41, "Lost EOF partial batch");

// Real HTTP lifecycle: EOF stays pending until buffered sound is played; bounded
// queues stop reading even if the producer sent a whole VOD immediately.
var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
var endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/av");
// Match the reported >5 s video-only tail. The last one/two audio packets
// cannot be consumed as a full batch until the reader reaches EOF.
using var videoTail = new MemoryStream();
videoTail.Write(Body(4));
for (uint i = 4; i < 110; i++) videoTail.Write(Record('V', i, [(byte)i, 0, 0]));
using var videoBurst = new MemoryStream();
videoBurst.Write(videoTail.ToArray());
for (uint i = 40; i < 100; i++) videoBurst.Write(Record('A', i, [0xF0, (byte)i]));
var responses = new[] { Body(6), Body(100), Encoding.UTF8.GetBytes("unavailable fixture"), Body(1)[..^1], videoTail.ToArray(), videoBurst.ToArray() };
var server = Task.Run(async () =>
{
    for (var i = 0; i < responses.Length; i++)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var network = client.GetStream();
        var request = new List<byte>(); var b = new byte[1];
        while (await network.ReadAsync(b) != 0)
        {
            request.Add(b[0]);
            if (request.Count >= 4 && request.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
        }
        var status = i == 2 ? "502 Bad Gateway" : "200 OK";
        await network.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nConnection: close\r\n\r\n"));
        await network.WriteAsync(responses[i]);
    }
});
using (var stream = new AvStream(endpoint, Console.WriteLine))
{
    await Until(() => stream.InputEnded, "Finite input did not end");
    Check(stream.Error == null && !stream.Finished && stream.Ready, "EOF discarded queued audio");
    Check(stream.VideoAt(0)!.Pixel(0, 0, 1, 1).R == 0, "Missing first video");
    Check(stream.VideoAt(0.29)!.Pixel(0, 0, 1, 1).R == 2, "Video advanced beyond audio clock");
    Check(stream.VideoAt(0.29) == null, "Future video dequeued early");
    Check(stream.VideoAt(0.3)!.Pixel(0, 0, 1, 1).R == 3, "Video index/FPS timing");
    var playback = new AvPlayback();
    for (var i = 0; i < 100 && !stream.Finished; i++)
        playback.Tick(0.016, stream.Ready, stream.InputEnded, () => stream.BufferedAudio, stream.TakeAudio, (_, _, _) => { });
    Check(stream.Finished && playback.SentPackets == 60 && stream.Error == null, "EOF did not drain audio");
    stream.Dispose();
}
using (var stream = new AvStream(endpoint, Console.WriteLine))
{
    await Until(() => stream.Ready, "No prebuffer"); await Task.Delay(100);
    Check(!stream.InputEnded && stream.BufferedAudio <= 500 && stream.BufferedVideo <= 50, "VOD buffering is unbounded");
    stream.Dispose();
    await Until(() => stream.InputEnded, "Cancellation left queue write blocked");
}
using (var stream = new AvStream(endpoint, Console.WriteLine))
{
    await Until(() => stream.InputEnded, "HTTP error did not end");
    Check(stream.Error?.Contains("502") == true && stream.Error.Contains("unavailable fixture"), "Lost feeder diagnostics");
}
using (var stream = new AvStream(endpoint, Console.WriteLine))
{
    await Until(() => stream.InputEnded, "Truncated AV did not end");
    Check(stream.Error != null, "Truncated AV silently accepted");
}
using (var stream = new AvStream(endpoint, Console.WriteLine))
{
    await Until(() => stream.Ready, "Tail fixture never buffered");
    var playback = new AvPlayback();
    for (var i = 0; i < 400 && !stream.Finished; i++)
    {
        playback.Tick(0.03, stream.Ready, stream.InputEnded, () => stream.BufferedAudio, stream.TakeAudio, (_, _, _) => { });
        stream.VideoAt(playback.Seconds);
        Check(stream.BufferedVideo <= 50 && stream.BufferedAudio <= 500, "Tail recovery exceeded buffer limits");
        await Task.Delay(5);
    }
    Check(stream.Error == null && stream.Finished && playback.SentPackets == 40,
        $"Video-only tail blocked EOF: packets={playback.SentPackets}, audio={stream.BufferedAudio}, video={stream.BufferedVideo}, inputEnded={stream.InputEnded}, error={stream.Error}");
    Check(stream.DroppedVideoFrames > 0, "Tail fixture did not exercise overflow recovery");
}
// Overflow can also hide later audio records, not just EOF. Keep the clock
// synchronized and deliver every audio packet despite an oversized video burst.
using (var stream = new AvStream(endpoint, Console.WriteLine))
{
    await Until(() => stream.Ready, "Burst fixture never buffered");
    var playback = new AvPlayback();
    for (var i = 0; i < 500 && !stream.Finished; i++)
    {
        playback.Tick(0.03, stream.Ready, stream.InputEnded, () => stream.BufferedAudio, stream.TakeAudio, (_, _, _) => { });
        var frame = stream.VideoAt(playback.Seconds);
        if (frame != null) Check(frame.Pixel(0, 0, 1, 1).R / 10d <= playback.Seconds, "Overflow recovery advanced video beyond audio");
        Check(stream.BufferedVideo <= 50 && stream.BufferedAudio <= 500, "Burst recovery exceeded buffer limits");
        await Task.Delay(5);
    }
    Check(stream.Finished && stream.Error == null && playback.SentPackets == 100,
        $"Video burst hid subsequent audio: consumed={playback.SentPackets}, ended={stream.InputEnded}, error={stream.Error}");
}
await server.WaitAsync(TimeSpan.FromSeconds(3)); listener.Stop();

var stalled = new TcpListener(IPAddress.Loopback, 0); stalled.Start();
using (var stream = new AvStream(new Uri($"http://127.0.0.1:{((IPEndPoint)stalled.LocalEndpoint).Port}"), Console.WriteLine))
{
    using var peer = await stalled.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
    var network = peer.GetStream(); var buffer = new byte[4096];
    await network.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    stream.Dispose();
    Check(await network.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)) == 0, "Cancel left HTTP request open");
    await Until(() => stream.InputEnded, "Cancel did not finish receiver");
}
stalled.Stop();
// Optional live feeder integration: pass its /av URL as the sole argument.
if (args.Length == 1)
{
    using var live = new AvStream(new Uri(args[0]), Console.WriteLine);
    var playback = new AvPlayback();
    var seenFrames = 0;
    var seenMessages = 0;
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    while (!live.Finished && live.Error == null)
    {
        limit.Token.ThrowIfCancellationRequested();
        playback.Tick(0.015, live.Ready, live.InputEnded, () => live.BufferedAudio, live.TakeAudio,
            (p, bytes, samples) =>
            {
                var message = VoiceData.Build(3, 4, 1, bytes, samples, p);
                Check(message.Length > p.Sum(packet => packet.Length), "Voice message serialization failed");
                seenMessages++;
            });
        if (live.VideoAt(playback.Seconds) != null) seenFrames++;
        await Task.Delay(15, limit.Token);
    }
    Check(live.Error == null && playback.SentPackets > 0 && seenFrames > 0 && seenMessages > 0,
        $"End-to-end feeder playback failed: {live.Error}");
    Console.WriteLine($"PASS: real feeder to voice serialization: {playback.SentPackets} packets, {seenMessages} messages, {seenFrames} displayed frames.");
}
Console.WriteLine("PASS: TSM1 parsing, bounds, HTTP lifecycle, EOF drain, cancellation, packet clock, underrun recovery, video overflow/EOF recovery, sequence offsets and video sync.");

sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
        base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], token);
}
