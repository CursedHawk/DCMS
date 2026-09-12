/**
 * A push-to-pull adapter, so a callback-driven loop can be consumed as an async iterable.
 *
 * <p>The model client reports progress through callbacks (`onText`, `onToolUse`) because that is
 * what an SSE reader naturally produces, while the runtime's consumers — a React panel, a test —
 * want `for await`. This is the join between them, and it exists so neither side has to be
 * written in the other's shape.</p>
 *
 * <p>It buffers. A producer that emits faster than the UI renders must not block or drop
 * events: token deltas arrive in bursts and a lost one is a hole in the middle of a sentence.</p>
 */
export class EventQueue<T> {
  private readonly buffer: T[] = [];
  private readonly waiting: ((result: IteratorResult<T>) => void)[] = [];
  private closed = false;

  push(item: T): void {
    if (this.closed) return;
    const next = this.waiting.shift();
    if (next) next({ value: item, done: false });
    else this.buffer.push(item);
  }

  /** No more items. Consumers still drain whatever is buffered before finishing. */
  close(): void {
    if (this.closed) return;
    this.closed = true;
    // Anyone already awaiting gets the end now; buffered items are handled by `next` below,
    // which checks the buffer before it checks `closed`.
    while (this.waiting.length > 0) {
      this.waiting.shift()!({ value: undefined as never, done: true });
    }
  }

  [Symbol.asyncIterator](): AsyncIterator<T> {
    return {
      next: (): Promise<IteratorResult<T>> => {
        // Buffer first, so closing does not discard what was already produced.
        if (this.buffer.length > 0) {
          return Promise.resolve({ value: this.buffer.shift()!, done: false });
        }
        if (this.closed) return Promise.resolve({ value: undefined as never, done: true });
        return new Promise((resolve) => this.waiting.push(resolve));
      },
      // Called when a consumer breaks out of `for await` early. Without it, a panel that stops
      // listening would leave the producer's pushes accumulating forever.
      return: (): Promise<IteratorResult<T>> => {
        this.close();
        return Promise.resolve({ value: undefined as never, done: true });
      },
    };
  }
}
