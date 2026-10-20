// @vitest-environment jsdom
import { act } from 'react';
import { createRoot } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { configureSiteEdge, resetSiteAccess } from './access';
import { builtinRegistry } from './components';
import { DataClientContext, DataError, createFetchDataClient, type DataClient } from './data';
import type { Node } from './document';
import { RenderNode } from './render';
import { RenderModeContext } from './renderMode';

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

/** A JWT whose payload expires `seconds` from now — only `exp` is read. */
const jwt = (seconds: number) => `h.${btoa(JSON.stringify({ exp: Math.floor(Date.now() / 1000) + seconds }))}.s`;

function visitors(): DataClient & { posts: { path: string; body: unknown }[] } {
  const posts: { path: string; body: unknown }[] = [];
  return {
    posts,
    async get(path) {
      if (path === '/api/members/me') {
        if (!localStorage.getItem('dcms.visitor')) throw new DataError(401, path);
        return { id: 'v1', email: 'ann@example.com', displayName: 'Ann' };
      }
      throw new DataError(404, path);
    },
    async post(path, body) {
      posts.push({ path, body });
      if ((body as { password?: string }).password !== 'right') throw new DataError(401, path);
      return { accessToken: jwt(900), refreshToken: 'r1' };
    },
  };
}

async function render(nodes: Node[], data: DataClient, mode: 'live' | 'edit' = 'live'): Promise<HTMLElement> {
  const el = document.createElement('div');
  document.body.append(el);
  await act(async () => {
    createRoot(el).render(
      <RenderModeContext.Provider value={mode}>
        <DataClientContext.Provider value={data}>
          {nodes.map((n) => (
            <RenderNode key={n.id} node={n} registry={builtinRegistry} />
          ))}
        </DataClientContext.Provider>
      </RenderModeContext.Provider>,
    );
  });
  await settle();
  return el;
}

const settle = () =>
  act(async () => {
    await new Promise((r) => setTimeout(r, 0));
  });

const section = (accounts: string, show: string, text: string): Node => ({
  id: `s-${show}`,
  type: 'dcms.signed-in',
  props: { accounts, show },
  slots: { default: [{ id: `t-${show}`, type: 'dcms.text', props: { text } }] },
});

beforeEach(() => {
  localStorage.clear();
  resetSiteAccess();
});
afterEach(() => {
  vi.unstubAllGlobals();
  document.body.innerHTML = '';
});

describe('visitor accounts', () => {
  it('signs in with the form, then shows who it is and the signed-in section, and signs out', async () => {
    const data = visitors();
    const el = await render(
      [
        { id: 'in', type: 'dcms.sign-in', props: { accounts: 'visitors', instance: 'members' } },
        section('visitors', 'signed-in', 'Members area'),
        section('visitors', 'signed-out', 'Please sign in'),
        { id: 'who', type: 'dcms.account', props: { accounts: 'visitors' } },
      ],
      data,
    );
    expect(el.textContent).toContain('Please sign in');
    expect(el.textContent).not.toContain('Members area');

    (el.querySelector('input[name="email"]') as HTMLInputElement).value = 'ann@example.com';
    (el.querySelector('input[name="password"]') as HTMLInputElement).value = 'right';
    await act(async () => el.querySelector('form')!.requestSubmit());
    await settle();

    expect(data.posts).toEqual([{ path: '/api/members/login', body: { email: 'ann@example.com', password: 'right' } }]);
    expect(JSON.parse(localStorage.getItem('dcms.visitor')!)).toMatchObject({ instance: 'members', refreshToken: 'r1' });
    expect(el.textContent).toContain('Members area');
    expect(el.textContent).not.toContain('Please sign in');
    expect(el.textContent).toContain('Signed in as Ann');
    expect(el.querySelector('form')).toBeNull();

    await act(async () => [...el.querySelectorAll('button')].find((b) => b.textContent === 'Sign out')!.click());
    expect(localStorage.getItem('dcms.visitor')).toBeNull();
    expect(el.textContent).toContain('Please sign in');
    expect(el.textContent).not.toContain('Signed in as');
  });

  it('says so when the password is wrong, and registers with a name', async () => {
    const data = visitors();
    const el = await render([{ id: 'in', type: 'dcms.sign-in', props: { accounts: 'visitors', instance: 'members' } }], data);
    (el.querySelector('input[name="email"]') as HTMLInputElement).value = 'ann@example.com';
    (el.querySelector('input[name="password"]') as HTMLInputElement).value = 'wrong';
    await act(async () => el.querySelector('form')!.requestSubmit());
    await settle();
    expect(el.querySelector('[role="alert"]')?.textContent).toBe('That email and password do not match an account.');

    const reg = await render([{ id: 'up', type: 'dcms.sign-in', props: { accounts: 'visitors', instance: 'members', mode: 'register' } }], data);
    expect(reg.querySelector('input[name="password"]')!.getAttribute('autocomplete')).toBe('new-password');
    (reg.querySelector('input[name="displayName"]') as HTMLInputElement).value = 'Ann';
    (reg.querySelector('input[name="email"]') as HTMLInputElement).value = 'ann@example.com';
    (reg.querySelector('input[name="password"]') as HTMLInputElement).value = 'right';
    await act(async () => reg.querySelector('form')!.requestSubmit());
    expect(data.posts.at(-1)).toEqual({ path: '/api/members/register', body: { email: 'ann@example.com', password: 'right', displayName: 'Ann' } });
  });

  it('the site’s data client sends the token, and renews it once expired', async () => {
    const calls: { url: string; auth?: string; body?: string }[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init?: RequestInit) => {
        calls.push({ url, auth: (init?.headers as Record<string, string>)?.Authorization, body: init?.body as string });
        return new Response(JSON.stringify(url.endsWith('/refresh') ? { accessToken: 'fresh.e30.s', refreshToken: 'r2' } : { items: [] }), { status: 200 });
      }),
    );
    const client = createFetchDataClient();
    await client.get('/api/blog/posts');
    expect(calls[0]!.auth).toBeUndefined();

    localStorage.setItem('dcms.visitor', JSON.stringify({ instance: 'members', accessToken: jwt(900), refreshToken: 'r1' }));
    await client.get('/api/crm/data/deals');
    expect(calls[1]!.auth).toBe(`Bearer ${jwt(900)}`);

    localStorage.setItem('dcms.visitor', JSON.stringify({ instance: 'members', accessToken: jwt(-60), refreshToken: 'r1' }));
    await client.post('/api/crm/data/deals', { name: 'x' });
    expect(calls[2]).toMatchObject({ url: '/api/members/refresh', auth: undefined, body: JSON.stringify({ refreshToken: 'r1' }) });
    expect(calls[3]!.auth).toBe('Bearer fresh.e30.s');
    expect(JSON.parse(localStorage.getItem('dcms.visitor')!)).toMatchObject({ accessToken: 'fresh.e30.s', refreshToken: 'r2' });
  });

  it('a refused renewal signs the visitor out', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('{}', { status: 401 })));
    localStorage.setItem('dcms.visitor', JSON.stringify({ instance: 'members', accessToken: jwt(-60), refreshToken: 'gone' }));
    await expect(createFetchDataClient().get('/api/crm/data/deals')).rejects.toThrow();
    expect(localStorage.getItem('dcms.visitor')).toBeNull();
  });
});

describe('site users', () => {
  it('signs in at the edge, back to the page, and asks the edge who is signed in', async () => {
    const signIn = vi.fn();
    configureSiteEdge({ base: '/.edge/preview/acme/s1', signIn });
    let me = { signedIn: false, name: null as string | null, email: null as string | null };
    const asked: string[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        asked.push(url);
        return new Response(JSON.stringify(me), { status: 200 });
      }),
    );
    const el = await render(
      [
        { id: 'in', type: 'dcms.sign-in', props: { afterSignIn: '/portal' } },
        section('users', 'signed-in', 'Staff only'),
        { id: 'who', type: 'dcms.account', props: {} },
      ],
      createFetchDataClient(),
    );
    expect(asked).toEqual(['/.edge/preview/acme/s1/me']);
    expect(el.textContent).not.toContain('Staff only');
    await act(async () => el.querySelector('button')!.click());
    expect(signIn).toHaveBeenCalledWith('/portal');

    // The preview's popup finished: it forgets the answers, and the page asks again.
    me = { signedIn: true, name: 'Pat', email: 'pat@corp.test' };
    await act(async () => resetSiteAccess());
    await settle();
    expect(el.textContent).toContain('Staff only');
    expect(el.textContent).toContain('Signed in as Pat');
    configureSiteEdge({ base: '/.edge/site', signIn: (url) => window.location.assign(url) });
  });

  it('on the canvas shows every state and asks nobody', async () => {
    const fetch = vi.fn();
    vi.stubGlobal('fetch', fetch);
    const el = await render([section('users', 'signed-in', 'Staff only'), { id: 'who', type: 'dcms.account', props: {} }], visitors(), 'edit');
    expect(el.textContent).toContain('Shown only to signed-in users');
    expect(el.textContent).toContain('Staff only');
    expect(el.textContent).toContain('Signed in as Alex Example');
    expect(fetch).not.toHaveBeenCalled();
  });
});
