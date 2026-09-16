import { CookieConsent } from './dcms';
import { site } from './site';

/**
 * Start here. The typed API client is ready at `src/lib/api.ts`; `src/api/API.md` lists what it
 * can fetch, and `useApi` in `src/lib/useApi.ts` handles loading and errors. AGENTS.md describes
 * how this project is laid out.
 */
export function App() {
  return (
    <>
      <main id="main" className="container blank">
        <h1>{site.name}</h1>
        <p className="lead">
          Edit <code>src/App.tsx</code> and the preview updates as you type.
        </p>
      </main>
      <CookieConsent policyUrl={site.privacyUrl} />
    </>
  );
}
