import { Link } from 'react-router';
import { collections, type CollectionInfo } from '../api';
import { ItemCard } from '../components/ItemCard';
import { Empty, ErrorMessage, Loading } from '../components/Status';
import { api } from '../lib/api';
import { collectionPath } from '../lib/content';
import { useApi } from '../lib/useApi';
import { site } from '../site';

export function HomePage() {
  return (
    <>
      <section className="hero">
        <h1>{site.name}</h1>
        <p className="lead">{site.tagline}</p>
      </section>

      {collections.length === 0 ? (
        <Empty>
          Nothing is published yet. Add a content plugin in DCMS, and its collections appear here.
        </Empty>
      ) : (
        collections.map((c) => <Latest key={collectionPath(c)} collection={c} />)
      )}
    </>
  );
}

function Latest({ collection }: { collection: CollectionInfo }) {
  const latest = useApi(() => api.content(collection.instance, collection.contentType).list({ pageSize: 3 }), [
    collection.instance,
    collection.contentType,
  ]);

  return (
    <section className="section" aria-labelledby={`latest-${collection.instance}-${collection.contentType}`}>
      <div className="section__header">
        <h2 id={`latest-${collection.instance}-${collection.contentType}`}>{collection.label}</h2>
        <Link to={collectionPath(collection)}>View all</Link>
      </div>
      {latest.status === 'loading' && <Loading />}
      {latest.status === 'error' && <ErrorMessage error={latest.error} onRetry={latest.reload} />}
      {latest.status === 'success' &&
        (latest.data.items.length === 0 ? (
          <Empty>Nothing published in {collection.label} yet.</Empty>
        ) : (
          <div className="grid">
            {latest.data.items.map((item) => (
              <ItemCard key={item.id} collection={collection} item={item} />
            ))}
          </div>
        ))}
    </section>
  );
}
