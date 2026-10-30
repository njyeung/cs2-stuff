namespace TheatreScreen;

public static class PlaybackRequest
{
    public static bool TryReadArgument(string arguments, out string sourceUrl)
    {
        sourceUrl = arguments.Trim().Trim('"', '\'', '“', '”').Trim();
        return sourceUrl.Length > 0 && !sourceUrl.Any(char.IsWhiteSpace);
    }

    public static bool TryCreate(string feederUrl, string sourceUrl, int width, int height, int fps,
        out Uri? endpoint, out string error)
    {
        endpoint = null;
        error = "Provide an HTTP(S) YouTube URL.";
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source) || source.Scheme is not ("http" or "https"))
            return false;
        var host = source.Host;
        if (!(host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
              host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
              host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
              host.EndsWith(".youtu.be", StringComparison.OrdinalIgnoreCase)))
            return false;
        error = "FeederUrl must be an absolute HTTP(S) base URL without a query or fragment.";
        if (!Uri.TryCreate(feederUrl, UriKind.Absolute, out var feeder) ||
            feeder.Scheme is not ("http" or "https") || feeder.Query.Length != 0 || feeder.Fragment.Length != 0)
            return false;
        error = "Screen dimensions must be 1–256 and FramesPerSecond must be 1–30.";
        if (width is < 1 or > 256 || height is < 1 or > 256 || fps is < 1 or > 30)
            return false;
        endpoint = new Uri($"{feeder.AbsoluteUri.TrimEnd('/')}/stream?url={Uri.EscapeDataString(sourceUrl)}&w={width}&h={height}&fps={fps}");
        error = "";
        return true;
    }
}
