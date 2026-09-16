import { collections } from '../api';
import { api } from '../lib/api';
import { useApi } from '../lib/useApi';

/**
 * The newest items of the tenant's first collection, if it has one. Renders nothing otherwise,
 * so the page is complete for a tenant with no content plugins at all.
 */
export function Latest() {
  const collection = collections[0];
  const latest = useApi(
    () => (collection ? api.content(collection.instance, collection.contentType).list({ pageSize: 3 }) : Promise.resolve(null)),
    [collection],
  );

  if (!collection || latest.status !== 'success' || !latest.data || latest.data.items.length === 0) return null;

  return (
    <section className="band band--surface" aria-labelledby="latest-title">
      <div className="container">
        <h2 id="latest-title">Latest from {collection.label}</h2>
        <ul className="latest">
          {latest.data.items.map((item) => {
            const title = collection.titleField ? item.data[collection.titleField] : undefined;
            const summary = collection.summaryField ? item.data[collection.summaryField] : undefined;
            return (
              <li key={item.id}>
                <h3>{typeof title === 'string' ? title : item.slug}</h3>
                {typeof summary === 'string' && <p>{summary}</p>}
              </li>
            );
          })}
        </ul>
      </div>
    </section>
  );
}
