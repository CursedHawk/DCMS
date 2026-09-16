import { Link, useParams } from 'react-router';
import { RichText } from '../components/RichText';
import { ErrorMessage, Loading } from '../components/Status';
import { ApiError } from '../api';
import { api } from '../lib/api';
import { bodyOf, collectionPath, dateOf, findCollection, formatDate, imageOf, otherFields, summaryOf, titleOf } from '../lib/content';
import { useApi } from '../lib/useApi';
import { NotFoundPage } from './NotFoundPage';

export function ItemPage() {
  const { instance, contentType, slug } = useParams();
  const collection = findCollection(instance, contentType);

  const result = useApi(
    () =>
      collection && slug
        ? api.content(collection.instance, collection.contentType).get(slug)
        : Promise.reject(new Error('Unknown collection')),
    [collection, slug],
  );

  if (!collection) return <NotFoundPage />;
  if (result.status === 'loading') return <Loading />;
  if (result.status === 'error') {
    // A slug nobody published is a missing page, not a failure worth retrying.
    return result.error instanceof ApiError && result.error.status === 404 ? (
      <NotFoundPage />
    ) : (
      <ErrorMessage error={result.error} onRetry={result.reload} />
    );
  }

  const item = result.data;
  const image = imageOf(collection, item, 1280);
  const summary = summaryOf(collection, item);
  const body = bodyOf(collection, item);
  const date = dateOf(collection, item);
  const rest = otherFields(collection, item);

  return (
    <article className="article">
      <p className="meta">
        <Link to={collectionPath(collection)}>{collection.label}</Link>
      </p>
      <h1>{titleOf(collection, item)}</h1>
      {date && (
        <time className="meta" dateTime={date.toISOString()}>
          {formatDate(date)}
        </time>
      )}
      {summary && <p className="lead">{summary}</p>}
      {image && <img className="article__image" src={image} alt="" />}
      {body && <RichText value={body} format={collection.bodyFormat} />}
      {rest.length > 0 && (
        <dl className="details">
          {rest.map(([label, value]) => (
            <div key={label}>
              <dt>{label}</dt>
              <dd>{value}</dd>
            </div>
          ))}
        </dl>
      )}
    </article>
  );
}
