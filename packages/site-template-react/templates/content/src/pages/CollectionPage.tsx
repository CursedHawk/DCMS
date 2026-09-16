import { Link, useParams, useSearchParams } from 'react-router';
import { ItemCard } from '../components/ItemCard';
import { Empty, ErrorMessage, Loading } from '../components/Status';
import { api } from '../lib/api';
import { findCollection } from '../lib/content';
import { useApi } from '../lib/useApi';
import { NotFoundPage } from './NotFoundPage';

const PAGE_SIZE = 12;

export function CollectionPage() {
  const { instance, contentType } = useParams();
  const [search] = useSearchParams();
  const collection = findCollection(instance, contentType);
  const page = Math.max(1, Number(search.get('page')) || 1);
  const tag = search.get('tag') ?? undefined;

  const result = useApi(
    () =>
      collection
        ? api.content(collection.instance, collection.contentType).list({ page, pageSize: PAGE_SIZE, tag })
        : Promise.reject(new Error('Unknown collection')),
    [collection, page, tag],
  );

  if (!collection) return <NotFoundPage />;

  const pages = result.status === 'success' ? Math.max(1, Math.ceil(result.data.totalCount / PAGE_SIZE)) : 1;
  const link = (n: number) => `?${new URLSearchParams({ ...(tag ? { tag } : {}), page: String(n) })}`;

  return (
    <>
      <header className="page-header">
        <h1>{collection.label}</h1>
        {collection.description && <p className="lead">{collection.description}</p>}
        {tag && (
          <p className="meta">
            Tagged “{tag}” · <Link to="?">show all</Link>
          </p>
        )}
      </header>

      {result.status === 'loading' && <Loading />}
      {result.status === 'error' && <ErrorMessage error={result.error} onRetry={result.reload} />}
      {result.status === 'success' &&
        (result.data.items.length === 0 ? (
          <Empty>Nothing here yet.</Empty>
        ) : (
          <>
            <div className="grid">
              {result.data.items.map((item) => (
                <ItemCard key={item.id} collection={collection} item={item} />
              ))}
            </div>
            {pages > 1 && (
              <nav className="pagination" aria-label="Pages">
                {page > 1 ? <Link to={link(page - 1)}>Newer</Link> : <span />}
                <span className="meta">
                  Page {page} of {pages}
                </span>
                {page < pages ? <Link to={link(page + 1)}>Older</Link> : <span />}
              </nav>
            )}
          </>
        ))}
    </>
  );
}
