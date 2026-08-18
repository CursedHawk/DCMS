import type {
  LintContext,
  LintMarker,
  ParseWorkerRequest,
  ParseWorkerResponse,
  ParsedCssRule,
  ParsedDocument,
  ParseHtmlOptions,
} from '@dcms/gjs-parse';
import ParseWorker from './parse.worker?worker';

/**
 * A request minus its id. `Omit` over a union collapses to the shared keys, so
 * the omit has to distribute across the members to keep each variant's payload.
 */
type PendingRequest = ParseWorkerRequest extends infer R
  ? R extends { id: number }
    ? Omit<R, 'id'>
    : never
  : never;

/**
 * Promise-per-request client for the parse worker.
 *
 * One worker is shared by the whole builder: requests are cheap to queue and a
 * worker-per-page would multiply the (non-trivial) cost of loading htmlparser2
 * and css-tree into each one.
 *
 * Every request carries a `key` (usually the file path). `latest()` uses it to
 * drop stale replies: while typing in the code view a slow parse of an earlier
 * keystroke must never overwrite the canvas with older markup.
 */
export class ParseClient {
  private worker: Worker | null = null;
  private nextId = 1;
  private readonly pending = new Map<number, { resolve: (r: ParseWorkerResponse) => void; reject: (e: Error) => void }>();
  /** Most recent in-flight request id per key, for `latest()`. */
  private readonly newest = new Map<string, number>();

  private ensureWorker(): Worker {
    if (this.worker) return this.worker;
    const worker = new ParseWorker();
    worker.onmessage = (event: MessageEvent<ParseWorkerResponse>) => {
      const response = event.data;
      const entry = this.pending.get(response.id);
      if (!entry) return;
      this.pending.delete(response.id);
      if (response.kind === 'error') entry.reject(new Error(response.message));
      else entry.resolve(response);
    };
    worker.onerror = (event) => {
      // A worker-level failure rejects everything queued; leaving them pending
      // would hang the save loop silently.
      const error = new Error(event.message || 'Parse worker failed.');
      for (const [, entry] of this.pending) entry.reject(error);
      this.pending.clear();
    };
    this.worker = worker;
    return worker;
  }

  private dispatch<T extends ParseWorkerResponse>(
    request: PendingRequest,
  ): { id: number; promise: Promise<T> } {
    const id = this.nextId++;
    this.newest.set(request.key, id);
    const promise = new Promise<T>((resolve, reject) => {
      this.pending.set(id, { resolve: resolve as (r: ParseWorkerResponse) => void, reject });
      this.ensureWorker().postMessage({ ...request, id } as ParseWorkerRequest);
    });
    return { id, promise };
  }

  private send<T extends ParseWorkerResponse>(request: PendingRequest): Promise<T> {
    return this.dispatch<T>(request).promise;
  }

  /** True when `id` is still the most recent request for its key. */
  private isLatest(key: string, id: number): boolean {
    return this.newest.get(key) === id;
  }

  async parseHtml(key: string, input: string, options?: ParseHtmlOptions): Promise<ParsedDocument> {
    const res = await this.send<Extract<ParseWorkerResponse, { kind: 'parse-html' }>>({
      kind: 'parse-html',
      key,
      input,
      options,
    });
    return res.result;
  }

  /**
   * Like `parseHtml`, but resolves to null when a newer request for the same key
   * has since been made — the caller should then simply do nothing.
   */
  async parseHtmlLatest(key: string, input: string, options?: ParseHtmlOptions): Promise<ParsedDocument | null> {
    const { id, promise } = this.dispatch<Extract<ParseWorkerResponse, { kind: 'parse-html' }>>({
      kind: 'parse-html',
      key,
      input,
      options,
    });
    const res = await promise;
    return this.isLatest(key, id) ? res.result : null;
  }

  async parseCss(key: string, input: string): Promise<ParsedCssRule[]> {
    const res = await this.send<Extract<ParseWorkerResponse, { kind: 'parse-css' }>>({
      kind: 'parse-css',
      key,
      input,
    });
    return res.result;
  }

  async format(key: string, input: { html?: string; css?: string }): Promise<{ html?: string; css?: string }> {
    const res = await this.send<Extract<ParseWorkerResponse, { kind: 'format' }>>({
      kind: 'format',
      key,
      html: input.html,
      css: input.css,
    });
    return { html: res.html, css: res.css };
  }

  /**
   * Cross-file diagnostics for one page. Resolves to null when the author has
   * typed again since — publishing markers for text that is already gone would
   * leave squiggles sitting on the wrong words.
   */
  async lintLatest(path: string, source: string, context: LintContext): Promise<LintMarker[] | null> {
    // Namespaced: staleness is tracked per key, and a lint of a page must not
    // make a concurrent parse of the same page look superseded.
    const key = `lint:${path}`;
    const { id, promise } = this.dispatch<Extract<ParseWorkerResponse, { kind: 'lint' }>>({
      kind: 'lint',
      key,
      source,
      context,
    });
    const res = await promise;
    return this.isLatest(key, id) ? res.diagnostics : null;
  }

  async index(stylesheets: Record<string, string>, pages?: Record<string, string>) {
    const res = await this.send<Extract<ParseWorkerResponse, { kind: 'index' }>>({
      kind: 'index',
      key: 'project',
      stylesheets,
      pages,
    });
    return {
      classNames: res.classNames,
      customProperties: res.customProperties,
      classSources: res.classSources,
      propertySources: res.propertySources,
      classUsages: res.classUsages,
    };
  }

  dispose(): void {
    this.worker?.terminate();
    this.worker = null;
    this.pending.clear();
    this.newest.clear();
  }
}

/** The builder's shared client. Lazily spawns its worker on first use. */
export const parseClient = new ParseClient();
