import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { user } from '../test/user';
import { DesktopRequired } from './DesktopRequired';

const labels = {
  title: 'This needs a wider screen',
  description: 'The editor is a desktop tool.',
  continueAnyway: 'Show it anyway',
};

function stubViewport(wide: boolean) {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: wide,
    media: query,
    addEventListener: () => {},
    removeEventListener: () => {},
  }));
}

afterEach(() => vi.unstubAllGlobals());

describe('DesktopRequired', () => {
  it('renders the editor on a wide screen', () => {
    stubViewport(true);
    render(
      <DesktopRequired labels={labels}>
        <p>editor</p>
      </DesktopRequired>,
    );

    expect(screen.getByText('editor')).toBeInTheDocument();
    expect(screen.queryByText(labels.title)).not.toBeInTheDocument();
  });

  it('explains itself instead of rendering a layout a phone cannot hold', () => {
    stubViewport(false);
    render(
      <DesktopRequired labels={labels}>
        <p>editor</p>
      </DesktopRequired>,
    );

    expect(screen.getByRole('heading', { name: labels.title })).toBeInTheDocument();
    expect(screen.queryByText('editor')).not.toBeInTheDocument();
  });

  /**
   * The person one pixel below the breakpoint knows their own situation better than this does.
   * Refusing outright is what makes a product look broken to the user best placed to judge.
   */
  it('lets someone through who insists', async () => {
    stubViewport(false);
    render(
      <DesktopRequired labels={labels}>
        <p>editor</p>
      </DesktopRequired>,
    );

    await user.click(screen.getByRole('button', { name: labels.continueAnyway }));

    expect(screen.getByText('editor')).toBeInTheDocument();
  });
});
