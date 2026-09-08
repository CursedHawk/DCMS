import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { useState } from 'react';
import { AiProvider, useAi, useAiPageContext } from './context';

/**
 * The page-context hook, and the loop it used to cause.
 *
 * <p>`useAiPageContext` writes to state on the provider above the page that calls it. That makes
 * an unstable argument a re-render loop rather than a wasted comparison: effect → provider state
 * → page re-render → new object → effect. React ends it with "Maximum update depth exceeded"
 * (minified: #185) and the page is replaced by an error boundary — which is exactly what
 * happened to the media library, whose `useMemo` dependency was a filtered array rebuilt on
 * every render.</p>
 */

function Probe() {
  const { page } = useAi();
  return <span data-testid="page">{page ? `${page.area}:${page.summary}` : 'none'}</span>;
}

/** Rebuilds its context object on every render, the way a page with an unmemoised list does. */
function UnstableCaller({ area, summary }: { area: string; summary: string }) {
  useAiPageContext({ area, summary, selection: [] });
  return null;
}

function Renders({ area, summary }: { area: string; summary: string }) {
  const [, force] = useState(0);
  return (
    <>
      <UnstableCaller area={area} summary={summary} />
      <button type="button" onClick={() => force((n) => n + 1)}>
        rerender
      </button>
    </>
  );
}

describe('useAiPageContext', () => {
  it('publishes what the page is showing', () => {
    render(
      <AiProvider>
        <UnstableCaller area="media" summary="the media library" />
        <Probe />
      </AiProvider>,
    );
    expect(screen.getByTestId('page')).toHaveTextContent('media:the media library');
  });

  it('survives a caller that builds a new object every render', () => {
    // Before the hook compared by value this render never settled: it threw
    // "Maximum update depth exceeded" instead of producing a tree.
    expect(() =>
      render(
        <AiProvider>
          <Renders area="media" summary="the media library" />
          <Probe />
        </AiProvider>,
      ),
    ).not.toThrow();

    expect(screen.getByTestId('page')).toHaveTextContent('media:the media library');
  });

  it('republishes when the description actually changes', () => {
    const { rerender } = render(
      <AiProvider>
        <UnstableCaller area="media" summary="the media library" />
        <Probe />
      </AiProvider>,
    );

    rerender(
      <AiProvider>
        <UnstableCaller area="media" summary="the media library, in the Brand folder" />
        <Probe />
      </AiProvider>,
    );

    expect(screen.getByTestId('page')).toHaveTextContent('the media library, in the Brand folder');
  });

  it('clears the context when the page unmounts', () => {
    const { rerender } = render(
      <AiProvider>
        <UnstableCaller area="media" summary="the media library" />
        <Probe />
      </AiProvider>,
    );
    expect(screen.getByTestId('page')).toHaveTextContent('media:');

    rerender(
      <AiProvider>
        <Probe />
      </AiProvider>,
    );
    expect(screen.getByTestId('page')).toHaveTextContent('none');
  });
});
