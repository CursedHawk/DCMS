using System.Buffers;
using System.Text;

namespace Dcms.AiGateway.Providers;

/// <summary>
/// Reads token usage out of an Anthropic response as it streams past, without buffering it or
/// changing a byte of it.
///
/// <para>The IDE agent endpoint is a reverse proxy: it forwards the caller's request verbatim
/// and copies the response — SSE or plain JSON — straight to the browser. That design is what
/// keeps the API key server-side, and it is worth keeping. But it also means the one place that
/// knows what a tenant's agent session cost never looks at the answer. This scanner closes that
/// gap by watching the bytes on their way through.</para>
///
/// <para>It matches on the wire format rather than parsing JSON, because the stream is a
/// sequence of independent SSE events and there is no document to parse until it ends — by
/// which point the response has already been sent. Anthropic reports <c>input_tokens</c> once
/// in <c>message_start</c> and a running <c>output_tokens</c> in each <c>message_delta</c>, so
/// the highest value seen for each is the final one. Taking the maximum also makes the
/// carry-over overlap below harmless: re-reading the same number twice cannot inflate a
/// maximum, whereas it would inflate a sum.</para>
///
/// <para>Everything here is best-effort by construction. A count that cannot be found stays
/// zero, and <see cref="Feed"/> cannot throw — a metric is never a reason to break a response
/// the user is waiting on.</para>
/// </summary>
public sealed class AnthropicUsageScanner
{
    private const string InputKey = "\"input_tokens\":";
    private const string OutputKey = "\"output_tokens\":";

    // The longest key plus room for the number that follows it, so a count split across a chunk
    // boundary is still whole once the carry-over is prepended.
    private const int CarryOver = 48;

    private string _tail = string.Empty;

    public long PromptTokens { get; private set; }
    public long CompletionTokens { get; private set; }

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        try
        {
            // Anthropic's wire format is ASCII-safe for the fields we read; a multi-byte
            // sequence split across a chunk boundary can only corrupt text we do not look at.
            var text = _tail + Encoding.UTF8.GetString(chunk);

            PromptTokens = Math.Max(PromptTokens, LastValue(text, InputKey));
            CompletionTokens = Math.Max(CompletionTokens, LastValue(text, OutputKey));

            _tail = text.Length <= CarryOver ? text : text[^CarryOver..];
        }
        catch
        {
            // Deliberately swallowed: see the class remarks. The proxy keeps streaming.
        }
    }

    private static long LastValue(string text, string key)
    {
        long best = 0;
        var at = text.IndexOf(key, StringComparison.Ordinal);
        while (at >= 0)
        {
            var i = at + key.Length;
            while (i < text.Length && text[i] == ' ')
            {
                i++;
            }
            long value = 0;
            var digits = 0;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                value = (value * 10) + (text[i] - '0');
                i++;
                digits++;
            }
            // Zero digits means the number is still on the far side of the chunk boundary; the
            // carry-over will present the same key again next time, complete.
            if (digits > 0)
            {
                best = Math.Max(best, value);
            }
            at = text.IndexOf(key, at + key.Length, StringComparison.Ordinal);
        }
        return best;
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/>, feeding every chunk
    /// through the scanner on the way. Flushed per chunk so SSE still arrives incrementally —
    /// buffering here would turn a streaming agent into one that appears to hang.
    /// </summary>
    public async Task CopyAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                Feed(buffer.AsSpan(0, read));
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                await destination.FlushAsync(ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
