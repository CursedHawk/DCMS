import { CookieConsent } from './dcms';
import { Contact } from './sections/Contact';
import { Features } from './sections/Features';
import { Hero } from './sections/Hero';
import { Latest } from './sections/Latest';
import { site } from './site';

/**
 * A one-page site: each section is a component in `src/sections/`, in the order they appear.
 * Add a section by creating a component there and placing it here.
 */
export function App() {
  return (
    <>
      <a className="skip-link" href="#main">
        Skip to content
      </a>
      <main id="main">
        <Hero />
        <Features />
        <Latest />
        <Contact />
      </main>
      <footer className="site-footer">
        <div className="container">
          © {new Date().getFullYear()} {site.name}
        </div>
      </footer>
      <CookieConsent policyUrl={site.privacyUrl} />
    </>
  );
}
