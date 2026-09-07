import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { TFunction } from 'i18next';
import { ApiError } from '@dcms/core';

const toast = vi.hoisted(() => {
  const fn = vi.fn() as ReturnType<typeof vi.fn> & Record<string, ReturnType<typeof vi.fn>>;
  fn.error = vi.fn();
  fn.success = vi.fn();
  fn.message = vi.fn();
  return fn;
});
vi.mock('sonner', () => ({ toast }));

const { toastApiError } = await import('./errors');

/** i18next stand-in that returns the defaultValue, which is what the host app does for a missing key. */
const t = ((key: string, o?: Record<string, unknown>) =>
  (o?.defaultValue as string | undefined)?.replace('{{id}}', String(o?.id ?? '')) ?? key) as unknown as TFunction;

beforeEach(() => {
  toast.error.mockReset();
  toast.success.mockReset();
  toast.message.mockReset();
});
afterEach(() => vi.unstubAllGlobals());

const optionsOf = () => toast.error.mock.calls[0][1] as {
  description: string;
  action: { label: string; onClick: () => void };
};

describe('toastApiError', () => {
  it('shows the server’s own message', () => {
    toastApiError(new ApiError(400, 'Slug already taken'), t);
    expect(toast.error).toHaveBeenCalledWith('Slug already taken');
  });

  it('falls back to a generic message for a non-ApiError', () => {
    toastApiError(new TypeError('undefined is not a function'), t);
    expect(toast.error).toHaveBeenCalledWith('Something went wrong.');
  });

  it('prefixes when one toast has to say WHICH of several things failed', () => {
    toastApiError(new ApiError(500, 'Boom'), t, 'photo.jpg');
    expect(toast.error).toHaveBeenCalledWith('photo.jpg: Boom');
  });

  it('offers no id action when there is no id to offer', () => {
    toastApiError(new ApiError(500, 'Boom'), t);
    expect(toast.error).toHaveBeenCalledWith('Boom');
    expect(toast.error.mock.calls[0]).toHaveLength(1);
  });

  it('surfaces the trace id, which is the only handle support has', () => {
    toastApiError(new ApiError(500, 'Boom', undefined, 'trace-1', 'req-1'), t);
    expect(optionsOf().description).toBe('Trace trace-1');
  });

  it('prefers the trace id over the request id', () => {
    toastApiError(new ApiError(500, 'Boom', undefined, 'trace-1', 'req-1'), t);
    expect(optionsOf().description).toContain('trace-1');
  });

  it('falls back to the request id when the request was never traced', () => {
    // A proxy error never reaches a service that starts spans, so there is no trace.
    toastApiError(new ApiError(502, 'Bad gateway', undefined, undefined, 'req-1'), t);
    expect(optionsOf().description).toBe('Trace req-1');
  });

  it('copies the id and confirms it', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    toastApiError(new ApiError(500, 'Boom', undefined, 'trace-1'), t);
    optionsOf().action.onClick();
    await vi.waitFor(() => expect(toast.success).toHaveBeenCalledWith('Copied'));
    expect(writeText).toHaveBeenCalledWith('trace-1');
  });

  it('shows the id to select by hand when there is no clipboard at all', () => {
    // navigator.clipboard is undefined on an insecure origin — i.e. local development.
    vi.stubGlobal('navigator', {});
    toastApiError(new ApiError(500, 'Boom', undefined, 'trace-1'), t);
    optionsOf().action.onClick();
    expect(toast.message).toHaveBeenCalledWith('trace-1');
  });

  it('shows the id when the clipboard exists but refuses', async () => {
    const writeText = vi.fn().mockRejectedValue(new DOMException('denied'));
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    toastApiError(new ApiError(500, 'Boom', undefined, 'trace-1'), t);
    optionsOf().action.onClick();
    await vi.waitFor(() => expect(toast.message).toHaveBeenCalledWith('trace-1'));
  });
});
