import { Link } from 'react-router';

export function NotFoundPage() {
  return (
    <section className="page-header">
      <h1>Page not found</h1>
      <p className="lead">The page may have moved, or it was never published.</p>
      <p>
        <Link className="button" to="/">
          Go to the home page
        </Link>
      </p>
    </section>
  );
}
