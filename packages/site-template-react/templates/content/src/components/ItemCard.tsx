import { Link } from 'react-router';
import type { CollectionInfo } from '../api';
import { dateOf, formatDate, imageOf, itemPath, summaryOf, titleOf, type Item } from '../lib/content';

export function ItemCard({ collection, item }: { collection: CollectionInfo; item: Item }) {
  const image = imageOf(collection, item);
  const summary = summaryOf(collection, item);
  const date = dateOf(collection, item);

  return (
    <article className="card">
      {image && <img className="card__image" src={image} alt="" loading="lazy" />}
      <div className="card__body">
        <h3 className="card__title">
          {/* The whole card is clickable through this link's ::after, but only one link is announced. */}
          <Link to={itemPath(collection, item)}>{titleOf(collection, item)}</Link>
        </h3>
        {summary && <p className="card__summary">{summary}</p>}
        {date && (
          <time className="meta" dateTime={date.toISOString()}>
            {formatDate(date)}
          </time>
        )}
      </div>
    </article>
  );
}
