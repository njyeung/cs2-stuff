using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TheatreScreen;

static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
}
static byte[] Header(int width = 2, int height = 1, int fps = 5, int count = 0)
{
    var bytes = new byte[20];
    var values = new[] { 0x31565354, width, height, fps, count };
    for (var i = 0; i < values.Length; i++)
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
    return bytes;
}
static async Task Until(Func<bool> predicate, int milliseconds, string failure)
{
    using var timeout = new CancellationTokenSource(milliseconds);
    while (!predicate())
    {
        if (timeout.IsCancellationRequested) throw new Exception(failure);
        await Task.Delay(10);
    }
}

var header = await VideoStreamHeader.ReadAsync(new FragmentedStream(Header()), default);
Check(header == new VideoStreamHeader(2, 1, 5, 0), "Fragmented header failed");
foreach (var bytes in new[] { Header(width: 0), Header(width: 257), Header(fps: 31), Header(count: -1), new byte[20] })
{
    try
    {
        await VideoStreamHeader.ReadAsync(new MemoryStream(bytes), default);
        throw new Exception("Malformed header accepted");
    }
    catch (InvalidDataException) { }
}
var pixel = new VideoFrame(2, 1, [255, 0, 0, 0, 0, 255]);
Check(pixel.Pixel(0, 0, 4, 2).ToArgb() == Color.Red.ToArgb(), "Red channel/resampling failed");
Check(pixel.Pixel(3, 1, 4, 2).ToArgb() == Color.Blue.ToArgb(), "Blue channel/resampling failed");

// An unpaced HTTP producer must not cause playback to consume the entire VOD at once.
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/stream");
var reports = new ConcurrentQueue<string>();
var server = Task.Run(async () =>
{
    for (var connection = 0; connection < 2; connection++)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var network = client.GetStream();
        var request = new List<byte>();
        var one = new byte[1];
        while (await network.ReadAsync(one) > 0)
        {
            request.Add(one[0]);
            if (request.Count >= 4 && Encoding.ASCII.GetString(request.ToArray()).EndsWith("\r\n\r\n")) break;
        }
        using var body = new MemoryStream();
        body.Write(Header());
        if (connection == 0)
            for (var i = 0; i < 10; i++) body.Write([(byte)i, 0, 0, (byte)i, 0, 0]);
        else
            body.Write([200, 0, 0, 200, 0, 0, 255, 255, 255]); // One complete frame, one truncated.
        await network.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
        await network.WriteAsync(body.ToArray());
    }
});
using (var receiver = new VideoStream(endpoint, reports.Enqueue))
{
    await Until(() => receiver.Latest != null, 3000, "First HTTP frame missing");
    await Task.Delay(300);
    var red = receiver.Latest!.Pixel(0, 0, 2, 1).R;
    Check(red is >= 1 and <= 3, $"Producer was not paced: frame {red}");
    await Until(() => receiver.Latest!.Pixel(0, 0, 2, 1).R == 9, 3000, "VOD frames did not advance");
    var old = receiver.Latest!;
    await Until(() => !old.IsFresh, 4000, "Stale frame did not expire for fallback");
    await Until(() => receiver.Latest!.Pixel(0, 0, 2, 1).R == 200, 5000, "EOF did not reconnect");
    await Task.Delay(400);
    Check(receiver.Latest!.Pixel(0, 0, 2, 1).R == 200, "Truncated frame was published");
    receiver.Dispose(); // Double dispose must be safe.
}
await server.WaitAsync(TimeSpan.FromSeconds(2));
listener.Stop();
Check(reports.Any(s => s.StartsWith("Feeder unavailable")), "Missing disconnect report");

// Cancellation must abort a pending HTTP response without blocking plugin unload.
var stalled = new TcpListener(IPAddress.Loopback, 0);
stalled.Start();
using (var receiver = new VideoStream(new Uri($"http://127.0.0.1:{((IPEndPoint)stalled.LocalEndpoint).Port}"), reports.Enqueue))
{
    using var peer = await stalled.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(2));
    var network = peer.GetStream();
    var buffer = new byte[4096];
    await network.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    receiver.Dispose();
    Check(await network.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(2)) == 0, "Dispose left HTTP request open");
}
stalled.Stop();
Console.WriteLine("PASS: fragmented/invalid headers, RGB sampling, HTTP pacing, stale-frame fallback, EOF reconnect, truncated frames, and cancellation.");

sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
        base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], token);
}
