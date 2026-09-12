using System.Text;

namespace Ardopcall.Tests;

/// <summary>
/// A stdin stand-in that hands out prepared lines and then holds the reader open
/// until the test says the input has ended.
/// </summary>
/// <remarks>
/// The gate is what makes the relay test deterministic without a single sleep: a
/// plain StringReader would reach end of input the instant the relay read its
/// last line, and the disconnect that follows would race the peer's reply.
/// </remarks>
internal sealed class ScriptedReader(params string[] lines) : TextReader
{
    private readonly Queue<string> pending = new(lines);
    private readonly TaskCompletionSource endOfInput = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Lets the reader report end of input, the way closing a pipe would.</summary>
    internal void EndInput() => endOfInput.TrySetResult();

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (pending.Count > 0)
        {
            return pending.Dequeue();
        }

        await endOfInput.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }
}

/// <summary>
/// A stdout stand-in that a test can await: it completes a task as soon as the
/// text it is watching for has been written.
/// </summary>
internal sealed class SignalWriter : TextWriter
{
    private readonly StringBuilder buffer = new();
    private readonly Lock gate = new();
    private string? wanted;
    private TaskCompletionSource<string>? completion;

    public override Encoding Encoding => Encoding.UTF8;

    /// <summary>Everything written so far.</summary>
    internal string Text
    {
        get
        {
            lock (gate)
            {
                return buffer.ToString();
            }
        }
    }

    /// <summary>Completes once the given text has been written.</summary>
    internal Task<string> ExpectAsync(string substring)
    {
        lock (gate)
        {
            wanted = substring;
            completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            CheckLocked();
            return completion.Task;
        }
    }

    public override void Write(char value)
    {
        lock (gate)
        {
            buffer.Append(value);
            CheckLocked();
        }
    }

    public override void Write(string? value)
    {
        lock (gate)
        {
            buffer.Append(value);
            CheckLocked();
        }
    }

    private void CheckLocked()
    {
        if (wanted is not null && completion is not null && buffer.ToString().Contains(wanted, StringComparison.Ordinal))
        {
            completion.TrySetResult(buffer.ToString());
        }
    }
}
