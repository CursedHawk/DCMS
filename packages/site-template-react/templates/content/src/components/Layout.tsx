import { NavLink, Outlet, ScrollRestoration } from 'react-router';
import { collections } from '../api';
import { CookieConsent } from '../dcms';
import { collectionPath } from '../lib/content';
import { site } from '../site';

/** The frame around every page: header with navigation, the page, and the footer. */
export function Layout() {
  return (
    <div className="shell">
      <a className="skip-link" href="#main">
        Skip to content
      </a>
      <header className="site-header">
        <div className="container site-header__inner">
          <NavLink to="/" className="brand" end>
            {site.name}
          </NavLink>
          {collections.length > 0 && (
            <nav aria-label="Main">
              <ul className="nav">
                {collections.map((c) => (
                  <li key={collectionPath(c)}>
                    <NavLink to={collectionPath(c)}>{c.label}</NavLink>
                  </li>
                ))}
              </ul>
            </nav>
          )}
        </div>
      </header>

      <main id="main" className="container page">
        <Outlet />
      </main>

      <footer className="site-footer">
        <div className="container">
          © {new Date().getFullYear()} {site.name}
        </div>
      </footer>

      <ScrollRestoration />
      <CookieConsent policyUrl={site.privacyUrl} />
    </div>
  );
}
