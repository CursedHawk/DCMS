import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { attachPreview } from './preview/previewBridge';
import { PreviewPane } from './PreviewPane';

// Stable, as the real ones are: anything new on each render re-attaches on every render and
// hides the bug this file is about.
const t = (key: string) => key;
const auth = { base: null, identity: null, version: 0, signIn: () => {}, signOut: () => {} };
vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t }),
}));
vi.mock('../site-source/previewAuth', () => ({
  usePreviewAuth: () => auth,
  PreviewIdentityMenu: () => null,
  PreviewVisitorMenu: () => null,
}));
vi.mock('./preview/previewBridge', () => ({
  attachPreview: vi.fn(() => () => {}),
  previewGo: vi.fn(),
}));

describe('PreviewPane', () => {
  /*
   * Reload remounts the frame with the same document. The bridge must follow it to the new
   * frame: left on the removed one, it ignores the new page's API calls, and the site waits on
   * its access check and its signed-in user forever — a blank page, nobody signed in.
   */
  it('attaches the bridge to the new frame on reload', () => {
    const preview = { srcdoc: '<html><head></head><body></body></html>', error: null, building: false, problems: [], refresh: vi.fn() };
    render(<PreviewPane preview={preview} siteId="s1" />);
    const attach = vi.mocked(attachPreview);
    const first = screen.getByTitle('ide.preview') as HTMLIFrameElement;
    expect(attach).toHaveBeenLastCalledWith(first.contentWindow, expect.any(Function), expect.any(Function), expect.any(Function), expect.any(Function));

    fireEvent.click(screen.getByRole('button', { name: 'previewBar.reload' }));

    const second = screen.getByTitle('ide.preview') as HTMLIFrameElement;
    expect(second).not.toBe(first);
    expect(preview.refresh).toHaveBeenCalled();
    expect(attach).toHaveBeenLastCalledWith(second.contentWindow, expect.any(Function), expect.any(Function), expect.any(Function), expect.any(Function));
  });
});
