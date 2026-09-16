/** The three states every page that loads something has to draw. */

export function Loading({ label = 'Loading…' }: { label?: string }) {
  return (
    <p className="status" role="status">
      {label}
    </p>
  );
}

export function ErrorMessage({ error, onRetry }: { error: Error; onRetry?: () => void }) {
  return (
    <div className="status status--error" role="alert">
      <p>This could not be loaded. {error.message}</p>
      {onRetry && (
        <button type="button" className="button" onClick={onRetry}>
          Try again
        </button>
      )}
    </div>
  );
}

export function Empty({ children }: { children: React.ReactNode }) {
  return <p className="status">{children}</p>;
}
