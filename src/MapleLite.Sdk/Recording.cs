using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using MapleLite.Core.Imaging;

namespace MapleLite.Sdk;

/// <summary>
/// A recorded play session: lossless PNGs of the game picture plus when each was taken, so it can be replayed
/// through the real readers (<c>--replay &lt;folder&gt;</c>) as often as needed.
/// <para>Layout: <c>session.json</c> (what was recorded), <c>frames.tsv</c> (one line per frame:
/// milliseconds since start, then the PNG name) and <c>frames/</c>. A frame identical to the one before it
/// is not saved again; its line names the earlier file.</para>
/// </summary>
public static class Recording
{
    public const string IndexFile = "frames.tsv", InfoFile = "session.json", FramesDir = "frames";

    public sealed record Info(DateTimeOffset Started, double Fps, string? WindowTitle, string? Mode, double Scale,
        int Frames = 0, int Saved = 0, int Dropped = 0, double Seconds = 0);

    public sealed record Entry(double Ms, string File);

    public static bool IsRecording(string path) => Directory.Exists(path) && File.Exists(Path.Combine(path, IndexFile));

    public static Info? ReadInfo(string folder)
    {
        try { return JsonSerializer.Deserialize<Info>(File.ReadAllText(Path.Combine(folder, InfoFile))); }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    public static List<Entry> ReadIndex(string folder)
    {
        var list = new List<Entry>();
        foreach (var line in File.ReadLines(Path.Combine(folder, IndexFile)))
        {
            var parts = line.Split('\t');
            if (parts.Length >= 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
                list.Add(new Entry(ms, parts[1]));
        }
        return list;
    }

    /// <summary>
    /// Writes a recording. <see cref="Add"/> is cheap and safe to call from the capture thread: PNG encoding runs
    /// on a background task. If the disk can't keep up, frames are dropped (and counted) rather than piling up in memory.
    /// </summary>
    public sealed class Writer : IAsyncDisposable
    {
        const int MaxQueued = 8;
        static readonly int Encoders = Math.Clamp(Environment.ProcessorCount / 2, 1, 3);
        readonly SemaphoreSlim _encoders = new(Encoders, Encoders);
        readonly BlockingCollection<(Frame Frame, double Ms)> _queue = new(MaxQueued);
        int _failed;
        readonly StreamWriter _index;
        readonly Task _worker;
        readonly DateTimeOffset _started;
        Info _info;
        int _frames, _saved, _dropped;
        long _bytes;
        double _lastMs;

        public string Folder { get; }
        public int Frames => _frames;
        public int Saved => _saved;
        /// <summary>Frames not recorded: skipped because encoding fell behind, or failed to write.</summary>
        public int Dropped => _dropped + _failed;
        public long Bytes => Interlocked.Read(ref _bytes);
        public TimeSpan Elapsed => DateTimeOffset.Now - _started;

        public Writer(string folder, Info info)
        {
            Folder = folder;
            Directory.CreateDirectory(Path.Combine(folder, FramesDir));
            _started = info.Started;
            _info = info;
            _index = new StreamWriter(Path.Combine(folder, IndexFile)) { AutoFlush = false };
            WriteInfo();
            _worker = Task.Factory.StartNew(Work, TaskCreationOptions.LongRunning);
        }

        /// <summary>Queues a frame taken at <paramref name="time"/>. Returns false if it was dropped.</summary>
        public bool Add(Frame frame, DateTimeOffset time)
        {
            if (_queue.IsAddingCompleted) return false;
            var ms = (time - _started).TotalMilliseconds;
            if (!_queue.TryAdd((frame, ms))) { Interlocked.Increment(ref _dropped); return false; }
            return true;
        }

        // This thread keeps the order (frames.tsv, file numbers, repeat checks); PNG encoding, the slow part,
        // runs on up to Encoders threads at once. When they're all busy the queue fills and Add starts dropping.
        void Work()
        {
            Frame? previous = null;
            string? previousFile = null;
            foreach (var (frame, ms) in _queue.GetConsumingEnumerable())
            {
                string file;
                if (previous != null && previousFile != null && Same(previous, frame)) file = previousFile;
                else
                {
                    file = $"{FramesDir}/{_saved:D6}.png";
                    _saved++;
                    previous = frame; previousFile = file;
                    _encoders.Wait();
                    var path = Path.Combine(Folder, file);
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var bytes = FrameFiles.EncodePng(frame, fast: true);
                            File.WriteAllBytes(path, bytes);
                            Interlocked.Add(ref _bytes, bytes.Length);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Interlocked.Increment(ref _failed); }
                        finally { _encoders.Release(); }
                    });
                }
                try
                {
                    _index.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{ms:0.0}\t{file}"));
                    _frames++;
                    _lastMs = ms;
                    if (_frames % 20 == 0) { _index.Flush(); WriteInfo(); }
                }
                catch (IOException) { Interlocked.Increment(ref _failed); }
            }
            for (int i = 0; i < Encoders; i++) _encoders.Wait(); // let the last pictures finish
        }

        static bool Same(Frame a, Frame b) => ReferenceEquals(a, b) || (a.W == b.W && a.H == b.H && a.Px.AsSpan().SequenceEqual(b.Px));

        void WriteInfo()
        {
            _info = _info with { Frames = _frames, Saved = _saved, Dropped = Dropped, Seconds = Math.Round(_lastMs / 1000, 1) };
            try { File.WriteAllText(Path.Combine(Folder, InfoFile), JsonSerializer.Serialize(_info, new JsonSerializerOptions { WriteIndented = true })); }
            catch (IOException) { }
        }

        /// <summary>Saves what's queued, then closes the files.</summary>
        public async ValueTask DisposeAsync()
        {
            if (_queue.IsAddingCompleted) return;
            _queue.CompleteAdding();
            await _worker.ConfigureAwait(false);
            _index.Flush();
            _index.Dispose();
            WriteInfo();
            _queue.Dispose();
            _encoders.Dispose();
        }
    }
}
