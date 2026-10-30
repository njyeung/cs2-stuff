using System.Buffers.Binary;
using System.Net.Http;
using System.Threading.Channels;

namespace TheatreScreen;

/// <summary>Reads TSM1 off-thread. Bounded queues propagate playback backpressure to HTTP.</summary>
public sealed class AvStream : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(500)
        { SingleReader = true, SingleWriter = true });
    private Channel<TimedVideoFrame>? _video;
    private readonly Task _receiver;
    private string? _error;
    private int _disposed;
    private int _fps;

    public string? Error => Volatile.Read(ref _error);
    public bool InputEnded => _receiver.IsCompleted;
    public int BufferedAudio => _audio.Reader.Count;
    public int BufferedVideo => Volatile.Read(ref _video)?.Reader.Count ?? 0;
    public bool Finished => InputEnded && BufferedAudio == 0;
    public bool Ready => BufferedVideo > 0 && (BufferedAudio >= 30 || InputEnded && BufferedAudio > 0);
    public int Fps => Volatile.Read(ref _fps);

    public AvStream(Uri endpoint, Action<string> report) =>
        _receiver = Task.Run(() => Receive(endpoint, report, _stop.Token));

    public byte[][] TakeAudio(int count)
    {
        var packets = new List<byte[]>(count);
        while (packets.Count < count && _audio.Reader.TryRead(out var packet)) packets.Add(packet);
        return [.. packets];
    }

    // Only the game thread consumes either queue. Future frames stay queued.
    public VideoFrame? VideoAt(double seconds)
    {
        VideoFrame? latest = null;
        var video = Volatile.Read(ref _video);
        while (video != null && video.Reader.TryPeek(out var next) && next.Index / (double)Fps <= seconds)
        {
            if (video.Reader.TryRead(out next)) latest = next.Frame;
        }
        return latest;
    }

    private async Task Receive(Uri endpoint, Action<string> report, CancellationToken stop)
    {
        try
        {
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(stop);
            opening.CancelAfter(TimeSpan.FromSeconds(60));
            using var response = await _http.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, opening.Token);
            if (!response.IsSuccessStatusCode)
            {
                using var reading = CancellationTokenSource.CreateLinkedTokenSource(stop);
                reading.CancelAfter(TimeSpan.FromSeconds(10));
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(stop));
                var chars = new char[1024];
                var count = await reader.ReadAsync(chars, reading.Token);
                throw new HttpRequestException($"Feeder HTTP {(int)response.StatusCode}: {new string(chars, 0, count).Replace('\r', ' ').Replace('\n', ' ').Trim()}");
            }
            await using var body = await response.Content.ReadAsStreamAsync(stop);
            var header = await AvStreamHeader.ReadAsync(body, stop);
            Volatile.Write(ref _fps, header.Fps);
            Volatile.Write(ref _video, Channel.CreateBounded<TimedVideoFrame>(new BoundedChannelOptions(header.Fps * 5)
                { SingleReader = true, SingleWriter = true }));
            report($"Connected AV: {header.Width}x{header.Height} at {header.Fps} FPS, 48 kHz mono Opus");
            await AvRecords.ReadAsync(body, header,
                (index, rgb) => _video.Writer.WriteAsync(new(index, new(header.Width, header.Height, rgb)), stop),
                (_, packet) => _audio.Writer.WriteAsync(packet, stop), stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        {
            Volatile.Write(ref _error, ex is OperationCanceledException ? "Feeder timed out." : ex.Message);
            report($"Playback failed: {Error}");
        }
        finally
        {
            _audio.Writer.TryComplete();
            Volatile.Read(ref _video)?.Writer.TryComplete();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _ = _receiver.ContinueWith(_ => { _http.Dispose(); _stop.Dispose(); }, TaskScheduler.Default);
    }

    private sealed record TimedVideoFrame(uint Index, VideoFrame Frame);
}

public sealed record AvStreamHeader(int Width, int Height, int Fps)
{
    public static async Task<AvStreamHeader> ReadAsync(Stream stream, CancellationToken stop)
    {
        var bytes = new byte[16];
        await VideoStream.ReadWithTimeout(stream, bytes, stop);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        var fps = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        if (magic != 0x314d5354 || width is < 1 or > 256 || height is < 1 or > 256 || fps is < 1 or > 30)
            throw new InvalidDataException("Invalid TSM1 stream header.");
        return new(width, height, fps);
    }
}

public static class AvRecords
{
    public static async Task ReadAsync(Stream stream, AvStreamHeader header,
        Func<uint, byte[], ValueTask> video, Func<uint, byte[], ValueTask> audio, CancellationToken stop)
    {
        uint nextVideo = 0, nextAudio = 0;
        var tag = new byte[1];
        var index = new byte[4];
        var length = new byte[2];
        while (true)
        {
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(stop);
            reading.CancelAfter(TimeSpan.FromSeconds(10));
            if (await stream.ReadAsync(tag, reading.Token) == 0)
            {
                if (nextVideo == 0 || nextAudio == 0) throw new InvalidDataException("Feeder ended without both video and audio.");
                return;
            }
            await stream.ReadExactlyAsync(index, reading.Token);
            var number = BinaryPrimitives.ReadUInt32LittleEndian(index);
            byte[] payload;
            switch (tag[0])
            {
                case (byte)'V':
                    if (number != nextVideo++) throw new InvalidDataException("Nonconsecutive video frame index.");
                    payload = new byte[header.Width * header.Height * 3];
                    await stream.ReadExactlyAsync(payload, reading.Token);
                    await video(number, payload);
                    break;
                case (byte)'A':
                    if (number != nextAudio++) throw new InvalidDataException("Nonconsecutive audio packet index.");
                    await stream.ReadExactlyAsync(length, reading.Token);
                    var size = BinaryPrimitives.ReadUInt16LittleEndian(length);
                    if (size == 0) throw new InvalidDataException("Empty Opus packet.");
                    payload = new byte[size];
                    await stream.ReadExactlyAsync(payload, reading.Token);
                    await audio(number, payload);
                    break;
                default: throw new InvalidDataException($"Unknown AV record tag {tag[0]}.");
            }
        }
    }
}
