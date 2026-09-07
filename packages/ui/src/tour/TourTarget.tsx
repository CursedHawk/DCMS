import { useEffect, useRef } from 'react';
import { useTour } from './TourProvider';

/**
 * Marks the element a tour step points at.
 *
 * <p>A wrapper rather than a ref threaded through, because the things worth explaining are
 * often components from this package — a `DataTable`, a `FilterBar` — and adding a ref
 * parameter to each of them to support a feature they know nothing about is the wrong trade.</p>
 *
 * <p><b>The wrapper is `display: contents`, and what gets registered is its first element
 * child.</b> That combination is load-bearing in both halves. `contents` keeps the wrapper out
 * of layout, so dropping one around a grid item or a flex child does not change the page. But a
 * `contents` element has no box of its own — `getBoundingClientRect` on it returns zeros — so
 * measuring the wrapper would put the tour's cut-out in the top-left corner of the screen,
 * every time, which is exactly the sort of bug that looks like a positioning subtlety and is
 * not.</p>
 *
 * <p>Pass a `className` to opt into a real wrapper element instead, for the rare case where the
 * thing being pointed at is several siblings rather than one.</p>
 */
export function TourTarget({
  id,
  children,
  className,
}: {
  id: string;
  children: React.ReactNode;
  className?: string;
}) {
  const { register } = useTour();
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const wrapper = ref.current;
    if (!wrapper) {
      register(id, null);
      return;
    }
    // A styled wrapper has its own box and is the target; a `contents` one does not, so the
    // child is.
    const measurable = className ? wrapper : (wrapper.firstElementChild as HTMLElement | null);
    register(id, measurable ?? wrapper);
    return () => register(id, null);
  }, [id, register, className, children]);

  return (
    <div ref={ref} className={className ?? 'contents'}>
      {children}
    </div>
  );
}
