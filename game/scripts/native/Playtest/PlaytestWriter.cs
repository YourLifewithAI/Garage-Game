using Godot;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace GarageGame.Native.Playtest;

/// <summary>
/// The record's own thread: it appends the events the main thread hands it to events.jsonl at least every FlushEveryMs (one write
/// to the operating system each time, nothing held in a buffer of ours, so a killed game loses only what is still queued, well under
/// a second), and it resizes, encodes and writes the stills (a JPEG beside its number, through a temporary file). The main thread
/// only grabs the picture and queues it. A failure to write stops the record quietly (the game plays on) and is reported once.
/// </summary>
internal sealed class PlaytestWriter
{
    /// <summary>Queued events reach the file at least this often.</summary>
    public const int FlushEveryMs = 250;
    /// <summary>The JPEG quality of a still (0 to 1).</summary>
    public const float JpegQuality = 0.8f;

    private readonly FileStream _events;
    private readonly ConcurrentQueue<byte[]> _lines = new();
    private readonly ConcurrentQueue<StillJob> _stills = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly MemoryStream _batch = new();
    private volatile bool _stopping;
    private volatile bool _abandoned;
    private int _pendingStills;
    private long _linesWritten;
    private long _stillsWritten;
    private string _failure = "";

    /// <summary>A still to finish: the grabbed picture, where it goes, and the size to write it at (its own when null).</summary>
    private sealed record StillJob(Image Image, string Path, Vector2I? Size);

    /// <summary>Opens events.jsonl (new, shared for reading so a session can read a play while it runs) and starts the thread.</summary>
    public PlaytestWriter(string eventsPath)
    {
        _events = new FileStream(eventsPath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = System.IO.FileAccess.Write, Share = FileShare.Read, BufferSize = 0,
        });
        _thread = new Thread(Run) { Name = "PlaytestWriter", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Stills queued and not yet written.</summary>
    public int PendingStills => Volatile.Read(ref _pendingStills);
    public long LinesWritten => Interlocked.Read(ref _linesWritten);
    public long StillsWritten => Interlocked.Read(ref _stillsWritten);
    /// <summary>Why the record stopped writing ("" while it writes).</summary>
    public string Failure => Volatile.Read(ref _failure);

    public void Enqueue(byte[] line)
    {
        if (_stopping) return;
        _lines.Enqueue(line);
    }

    /// <summary>Queues a grabbed picture; the writer owns it from here and frees it.</summary>
    public void EnqueueStill(Image image, string path, Vector2I? size)
    {
        if (_stopping)
        {
            image.Dispose();
            return;
        }
        Interlocked.Increment(ref _pendingStills);
        _stills.Enqueue(new StillJob(image, path, size));
        _wake.Set();
    }

    /// <summary>A clean stop: everything queued is written, then the file is closed. False when that took longer than the limit.</summary>
    public bool Stop(int limitMs)
    {
        _stopping = true;
        _wake.Set();
        var finished = _thread.Join(limitMs);
        if (finished) _events.Dispose();
        return finished;
    }

    /// <summary>
    /// What a killed game leaves (tests): the thread stops where it is and nothing queued is written. The file holds what the thread
    /// had already handed the operating system.
    /// </summary>
    public void Abandon()
    {
        _abandoned = true;
        _stopping = true;
        _wake.Set();
        _thread.Join(5000);
        _events.Dispose();
        while (_stills.TryDequeue(out var job)) job.Image.Dispose();
    }

    private void Run()
    {
        while (true)
        {
            _wake.WaitOne(FlushEveryMs);
            if (_abandoned) return;
            WriteLines();
            // A still at a time, the events between them, so a slow encoding never holds an event past the flush.
            while (!_abandoned && _stills.TryDequeue(out var job))
            {
                WriteStill(job);
                WriteLines();
            }
            if (_stopping && _lines.IsEmpty && _stills.IsEmpty) return;
        }
    }

    private void WriteLines()
    {
        if (_lines.IsEmpty) return;
        _batch.SetLength(0);
        var count = 0;
        while (_lines.TryDequeue(out var line))
        {
            _batch.Write(line);
            count++;
        }
        if (_failure.Length > 0) return;
        try
        {
            _events.Write(_batch.GetBuffer(), 0, (int)_batch.Length);
            Interlocked.Add(ref _linesWritten, count);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Fail("events.jsonl: " + error.GetType().Name);
        }
    }

    private void WriteStill(StillJob job)
    {
        var image = job.Image;
        try
        {
            if (_failure.Length > 0) return;
            if (job.Size is { } size && (size.X != image.GetWidth() || size.Y != image.GetHeight())) image.Resize(size.X, size.Y, Image.Interpolation.Bilinear);
            if (image.GetFormat() != Image.Format.Rgb8) image.Convert(Image.Format.Rgb8);
            var bytes = image.SaveJpgToBuffer(JpegQuality);
            var temporary = job.Path + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, job.Path, overwrite: false);
            Interlocked.Increment(ref _stillsWritten);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Fail("stills: " + error.GetType().Name);
        }
        finally
        {
            image.Dispose();
            Interlocked.Decrement(ref _pendingStills);
        }
    }

    private void Fail(string why)
    {
        if (Interlocked.CompareExchange(ref _failure, why, "") == "") GD.Print("PLAYTEST_RECORD stopped writing: " + why);
    }
}
