import { useEffect, useState } from 'react';
import type { ContentItem } from '../api';
import { config } from '../config';
import { api } from '../lib/client';

/**
 * Example page: lists items from a configured plugin instance. Uses the generic
 * `api.content(slug, type)` accessor so it works before you wire up the typed,
 * per-instance calls (e.g. `api.news.post.list()`) available in the downloaded client.
 */
export function ContentList() {
  const [items, setItems] = useState<ContentItem[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    if (!config.demoSlug || !config.demoContentType) {
      setError('Set VITE_DEMO_SLUG and VITE_DEMO_CONTENT_TYPE in .env to see content here.');
      setLoading(false);
      return;
    }
    let active = true;
    api
      .content(config.demoSlug, config.demoContentType)
      .list({ pageSize: 20 })
      .then((result) => {
        if (active) setItems(result.items);
      })
      .catch((e: unknown) => {
        if (active) setError(e instanceof Error ? e.message : String(e));
      })
      .finally(() => {
        if (active) setLoading(false);
      });
    return () => {
      active = false;
    };
  }, []);

  if (loading) return <p>Loading…</p>;
  if (error) return <p style={{ color: 'crimson' }}>{error}</p>;
  if (items.length === 0) return <p>No published items yet.</p>;

  return (
    <ul style={{ listStyle: 'none', padding: 0, display: 'grid', gap: '1rem' }}>
      {items.map((item) => (
        <li key={item.id} style={{ border: '1px solid #e2e8f0', borderRadius: '.5rem', padding: '1rem' }}>
          <strong>{item.slug}</strong>
          <pre style={{ overflowX: 'auto' }}>{JSON.stringify(item.data, null, 2)}</pre>
        </li>
      ))}
    </ul>
  );
}
