import { CookieConsent } from './dcms';
import { ContentList } from './pages/ContentList';

export function App() {
  return (
    <main style={{ fontFamily: 'system-ui, sans-serif', maxWidth: 720, margin: '2rem auto', padding: '0 1rem' }}>
      <h1>DCMS site starter</h1>
      <p>
        Edit <code>src/</code> to build your site. Content comes from your tenant API through the
        typed client in <code>src/api</code>.
      </p>
      <ContentList />

      {/*
        Renders nothing unless there is something to consent to: analytics on for
        this tenant, and this visitor has not answered yet. Point `policyUrl` at
        your own privacy page — a notice with nothing to read is not consent.
      */}
      <CookieConsent />
    </main>
  );
}
