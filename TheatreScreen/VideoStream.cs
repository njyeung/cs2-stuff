using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;

namespace TheatreScreen;

/// <summary>Receives and paces the feeder off-thread; publishes only complete immutable frames.</summary>
public sealed class VideoStream : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _receiver;
    private VideoFrame? _latest;
    private int _disposed;
    private string? _error;

    public VideoFrame? Latest => Volatile.Read(ref _latest);
    public bool Finished => _receiver.IsCompleted;
    public string? Error => Volatile.Read(ref _error);

    public VideoStream(Uri endpoint, Action<string> report)
    {
        _receiver = Task.Run(() => ReceiveLoop(endpoint, report, _stop.Token));
    }

    private async Task ReceiveLoop(Uri endpoint, Action<string> report, CancellationToken stop)
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
                var text = new char[1024];
                var count = await reader.ReadAsync(text, reading.Token);
                var message = new string(text, 0, count).Replace('\r', ' ').Replace('\n', ' ').Trim();
                throw new HttpRequestException($"Feeder HTTP {(int)response.StatusCode}: {message}");
            }
            await using var body = await response.Content.ReadAsStreamAsync(stop);
            var header = await VideoStreamHeader.ReadAsync(body, stop);
            report($"Connected: {header.Width}x{header.Height} at {header.Fps} FPS");
            var clock = Stopwatch.StartNew();
            var due = 0d;
            for (var index = 0; header.FrameCount == 0 || index < header.FrameCount; index++)
            {
                var delay = due - clock.Elapsed.TotalSeconds;
                if (delay > 0)
                    await Task.Delay(TimeSpan.FromSeconds(delay), stop);
                var rgb = new byte[header.Width * header.Height * 3];
                using var reading = CancellationTokenSource.CreateLinkedTokenSource(stop);
                reading.CancelAfter(TimeSpan.FromSeconds(10));
                var first = await body.ReadAsync(rgb, reading.Token);
                if (first == 0)
                {
                    if (header.FrameCount > 0)
                        throw new EndOfStreamException("Feeder ended before the advertised frame count.");
                    break; // Clean EOF at a frame boundary means the video finished.
                }
                await body.ReadExactlyAsync(rgb.AsMemory(first), reading.Token);
                Volatile.Write(ref _latest, new VideoFrame(header.Width, header.Height, rgb));
                // Client pacing prevents downloading a whole VOD at decode speed.
                due = Math.Max(due + 1d / header.Fps, clock.Elapsed.TotalSeconds);
            }
            report("Video finished; returning to black.");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        {
            Volatile.Write(ref _error, ex is OperationCanceledException ? "Feeder timed out." : ex.Message);
            report($"Playback failed: {Error}");
        }
    }

    internal static async Task ReadWithTimeout(Stream stream, Memory<byte> buffer, CancellationToken stop)
    {
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(stop);
        reading.CancelAfter(TimeSpan.FromSeconds(10));
        await stream.ReadExactlyAsync(buffer, reading.Token);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        // Abort HTTP reads and pacing without waiting on the game thread.
        _ = _receiver.ContinueWith(_ =>
        {
            _http.Dispose();
            _stop.Dispose();
        }, TaskScheduler.Default);
    }
}

public sealed record VideoStreamHeader(int Width, int Height, int Fps, int FrameCount)
{
    public static async Task<VideoStreamHeader> ReadAsync(Stream stream, CancellationToken stop)
    {
        var bytes = new byte[20];
        await VideoStream.ReadWithTimeout(stream, bytes, stop);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        var width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4));
        var fps = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12, 4));
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16, 4));
        if (magic != 0x31565354 || width is < 1 or > 256 || height is < 1 or > 256 || fps is < 1 or > 30 || count < 0)
            throw new InvalidDataException("Invalid TSV1 stream header.");
        return new(width, height, fps, count);
    }
}

public sealed class VideoFrame(int width, int height, byte[] rgb)
{
    private readonly long _received = Stopwatch.GetTimestamp();
    public bool IsFresh => Stopwatch.GetElapsedTime(_received) < TimeSpan.FromSeconds(3);

    public Color Pixel(int col, int row, int columns, int rows)
    {
        var offset = ((row * height / rows) * width + col * width / columns) * 3;
        return Color.FromArgb(rgb[offset], rgb[offset + 1], rgb[offset + 2]);
    }
}
