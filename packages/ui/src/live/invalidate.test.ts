import { describe, expect, it, vi } from 'vitest';
import type { QueryClient } from '@tanstack/react-query';
import { createResourceInvalidator, type TagQueryMap } from './invalidate';
import { RESOURCE_TAGS, isResourceTag } from './tags';

const map: TagQueryMap = {
  media: [['media'], ['media-usage']],
  content: [['content']],
};

function client() {
  const invalidateQueries = vi.fn().mockResolvedValue(undefined);
  return { client: { invalidateQueries } as unknown as QueryClient, invalidateQueries };
}

describe('createResourceInvalidator', () => {
  it('invalidates every key a tag maps to', () => {
    const { client: qc, invalidateQueries } = client();
    createResourceInvalidator(qc, map)({ tag: 'media' });
    expect(invalidateQueries).toHaveBeenCalledTimes(2);
    expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: ['media'] });
    expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: ['media-usage'] });
  });

  it('ignores a tag it does not know, rather than refetching everything', () => {
    // A console talking to a newer server would otherwise dump its whole cache on every push
    // it did not understand — the opposite of what tagging is for.
    const { client: qc, invalidateQueries } = client();
    createResourceInvalidator(qc, map)({ tag: 'quantum-widgets' });
    expect(invalidateQueries).not.toHaveBeenCalled();
  });

  it('never invalidates without a key — that would be the whole cache', () => {
    const { client: qc, invalidateQueries } = client();
    const invalidate = createResourceInvalidator(qc, map);
    invalidate({ tag: 'content' });
    for (const call of invalidateQueries.mock.calls) {
      expect(call[0]).toHaveProperty('queryKey');
      expect((call[0] as { queryKey: unknown[] }).queryKey.length).toBeGreaterThan(0);
    }
  });

  it('passes a key prefix, so every variation of a list is covered', () => {
    // ['media'] has to match ['media', folderId, search] — that is the whole reason the server
    // names a class of data instead of a query.
    const { client: qc, invalidateQueries } = client();
    createResourceInvalidator(qc, map)({ tag: 'content' });
    expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: ['content'] });
  });

  it('does not care about the id, which is a hint rather than an instruction', () => {
    const { client: qc, invalidateQueries } = client();
    createResourceInvalidator(qc, map)({ tag: 'media', id: 'abc' });
    expect(invalidateQueries).toHaveBeenCalledTimes(2);
  });

  it('tolerates a tag mapped to nothing', () => {
    const { client: qc, invalidateQueries } = client();
    createResourceInvalidator(qc, { media: [] })({ tag: 'media' });
    expect(invalidateQueries).not.toHaveBeenCalled();
  });
});

describe('the tag vocabulary', () => {
  it('recognises its own tags', () => {
    for (const tag of RESOURCE_TAGS) expect(isResourceTag(tag)).toBe(true);
  });

  it('rejects anything else', () => {
    expect(isResourceTag('media ')).toBe(false);
    expect(isResourceTag('Media')).toBe(false);
    expect(isResourceTag('')).toBe(false);
  });
});
