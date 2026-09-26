using System.Text;

namespace Cinomni.Playback.Encoding;

/// <summary>
/// The last few lines a child process wrote, and nothing more. FFmpeg's stderr has to be read for as
/// long as the process lives — a pipe nobody drains fills up and blocks the encoder mid-stream — but
/// only its end is worth keeping: it is where the reason for a failure is.
/// <para>
/// Bounded twice, because the text is untrusted output from a process fed a hostile file: at most
/// <see cref="MaxLines"/> lines are kept, and a line longer than <see cref="MaxLineLength"/> is cut,
/// so a stream with no line breaks at all cannot grow the buffer either. Both <c>\n</c> and <c>\r</c>
/// end a line, since FFmpeg redraws its status line with a bare carriage return.
/// </para>
/// Thread-safe: the drain appends while a sweep reads.
/// </summary>
internal sealed class BoundedLineTail
{
    internal const int MaxLines = 20;

    internal const int MaxLineLength = 400;

    private readonly Queue<string> _lines = new();
    private readonly StringBuilder _current = new();
    private readonly object _gate = new();

    public void Append(ReadOnlySpan<char> text)
    {
        lock (_gate)
        {
            foreach (var character in text)
            {
                if (character is '\n' or '\r')
                {
                    EndLine();
                }
                else if (_current.Length < MaxLineLength)
                {
                    _current.Append(character);
                }
            }
        }
    }

    /// <summary>The kept lines, oldest first, including a final line that has not ended yet.</summary>
    public override string ToString()
    {
        lock (_gate)
        {
            var lines = _lines.ToList();
            if (_current.Length > 0)
            {
                lines.Add(_current.ToString());
            }

            return string.Join('\n', lines);
        }
    }

    private void EndLine()
    {
        if (_current.Length == 0)
        {
            return; // "\r\n" and blank lines carry nothing worth a slot
        }

        _lines.Enqueue(_current.ToString());
        _current.Clear();
        while (_lines.Count > MaxLines)
        {
            _lines.Dequeue();
        }
    }
}
