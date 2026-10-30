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
var requests = 0;
var server = Task.Run(async () =>
{
    for (var connection = 0; connection < 3; connection++)
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
        if (connection == 1)
            body.Write(Encoding.UTF8.GetBytes("ERROR: This video is unavailable"));
        else
        {
            body.Write(Header());
            if (connection == 0)
                for (var i = 0; i < 10; i++) body.Write([(byte)i, 0, 0, (byte)i, 0, 0]);
            else
                body.Write([200, 0, 0, 200, 0, 0, 255, 255, 255]); // One complete frame, one truncated.
        }
        Interlocked.Increment(ref requests);
        var status = connection == 1 ? "502 Bad Gateway" : "200 OK";
        // No Content-Length: match the real feeder's stream ending at connection close.
        await network.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nConnection: close\r\n\r\n"));
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
    await Until(() => receiver.Finished, 2000, "Clean EOF did not finish playback");
    Check(receiver.Error == null, "Clean EOF was reported as an error");
    await Task.Delay(300);
    Check(requests == 1, "Video restarted automatically after EOF");
    receiver.Dispose(); // Double dispose must be safe.
}
using (var failed = new VideoStream(endpoint, reports.Enqueue))
{
    await Until(() => failed.Finished, 2000, "Feeder HTTP error did not finish playback");
    Check(failed.Error?.Contains("502") == true && failed.Error.Contains("video is unavailable"), "Feeder error body was not preserved");
    Check(failed.Latest == null, "HTTP error published a frame");
}
using (var truncated = new VideoStream(endpoint, reports.Enqueue))
{
    await Until(() => truncated.Finished, 2000, "Truncated frame did not stop playback");
    Check(truncated.Error != null, "Truncated frame was not reported");
    Check(truncated.Latest?.Pixel(0, 0, 2, 1).R == 200, "Truncated frame was published");
}
await server.WaitAsync(TimeSpan.FromSeconds(2));
listener.Stop();
Check(reports.Any(s => s.StartsWith("Playback failed")), "Missing failure report");
await Until(() => !pixel.IsFresh, 4000, "Stale frame did not expire");

// The command accepts YouTube/Twitch URLs and escapes playlist query parameters.
foreach (var url in new[] { "https://www.youtube.com/watch?v=aRiVUVNUhls", "https://youtu.be/aRiVUVNUhls?si=wuUOB65R6J6Viwl", "https://www.youtube.com/watch?v=abc&list=xyz", "https://www.twitch.tv/example" })
{
    foreach (var arguments in new[] { url, $"  {url}  ", $"\"{url}\"", $"“{url}”" })
    {
        Check(PlaybackRequest.TryReadArgument(arguments, out var parsed) && parsed == url,
            "Pasted URL punctuation/query/quotes were not preserved");
        Check(PlaybackRequest.TryCreate("http://localhost:8787", parsed, 64, 36, 15, out var built, out _),
            "Pasted chat URL was rejected");
        Check(Uri.UnescapeDataString(built!.Query.Split('&')[0][5..]) == url, "Pasted URL changed before reaching feeder");
    }
}
foreach (var arguments in new[] { "", "   ", "\"\"", "https://youtu.be/abc extra words" })
    Check(!PlaybackRequest.TryReadArgument(arguments, out _), "Missing or extra command arguments accepted");
Check(PlaybackRequest.TryCreate("http://172.30.0.1:8787", "https://www.youtube.com/watch?v=abc&list=xyz", 64, 36, 15,
    out var play, out _), "Valid YouTube URL rejected");
Check(play!.AbsolutePath == "/av", "Command did not use combined AV endpoint");
Check(play.Query.Contains("%26list%3Dxyz") && play.Query.EndsWith("&w=64&h=36&fps=15"), "Video URL or screen dimensions encoded incorrectly");
foreach (var source in new[] { "file:///tmp/video", "https://youtube.com.attacker.test/watch", "https://youtube.com@attacker.test/", "hello" })
    Check(!PlaybackRequest.TryCreate("http://localhost:8787", source, 64, 36, 15, out _, out _), "Invalid source accepted");
Check(!PlaybackRequest.TryCreate("invalid", "https://youtu.be/abc", 64, 36, 15, out _, out _), "Invalid feeder accepted");
Check(!PlaybackRequest.TryCreate("http://localhost:8787", "https://youtu.be/abc", 0, 36, 15, out _, out _), "Invalid dimensions accepted");

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
Console.WriteLine("PASS: stream pacing, clean EOF without looping, feeder error messages, truncated frames, cancellation, headers, RGB sampling, and YouTube command URLs.");

sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
        base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], token);
}
