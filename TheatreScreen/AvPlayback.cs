namespace TheatreScreen;

/// <summary>Game-thread clock: three 10 ms packets per message, no catch-up after starvation.</summary>
public sealed class AvPlayback
{
    private double _credit;
    private bool _started;
    public long SentPackets { get; private set; }
    public int SentBytes { get; private set; }
    public double Seconds => SentPackets * 0.01;

    public void Tick(double elapsedSeconds, bool ready, bool inputEnded, Func<int> buffered,
        Func<int, byte[][]> take, Action<byte[][], int, uint> send)
    {
        if (!_started)
        {
            if (!ready) return;
            _started = true;
            _credit = 0.03; // One confirmed three-packet message of lead.
        }
        else _credit += Math.Clamp(elapsedSeconds, 0, 0.1);
        // Never burst a backlog after a game-thread hitch.
        _credit = Math.Min(_credit, 0.06);
        while (_credit + 1e-9 >= 0.03 || inputEnded && _credit + 1e-9 >= 0.01)
        {
            var count = inputEnded ? Math.Min(3, buffered()) : 3;
            if (count == 0 || buffered() < count)
            {
                _credit = 0;
                return;
            }
            if (_credit + 1e-9 < count * 0.01) return;
            var packets = take(count);
            if (packets.Length != count) throw new InvalidOperationException("Audio queue changed outside playback tick.");
            send(packets, SentBytes, unchecked((uint)(SentPackets * 480)));
            SentPackets += packets.Length;
            SentBytes = unchecked(SentBytes + packets.Sum(p => p.Length));
            _credit -= packets.Length * 0.01;
        }
    }
}
