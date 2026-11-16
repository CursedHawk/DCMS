import { createBrowserRouter } from 'react-router';
import { siteAccessLoader, siteAccessRevalidate } from './dcms';
import { Layout } from './components/Layout';
import { CollectionPage } from './pages/CollectionPage';
import { HomePage } from './pages/HomePage';
import { ItemPage } from './pages/ItemPage';
import { NotFoundPage } from './pages/NotFoundPage';

/**
 * Every page of the site, in one place. Add a page by creating it in `src/pages/` and adding a
 * route here — static paths such as `/about` win over the `:instance` patterns below, so they
 * can be added in any order.
 *
 * The root loader asks the site's access rules (User Authentication) before each page is drawn:
 * moving between pages inside the app never reaches them otherwise. Without rules it lets
 * everything through.
 */
export const router = createBrowserRouter([
  {
    element: <Layout />,
    loader: siteAccessLoader,
    shouldRevalidate: siteAccessRevalidate,
    HydrateFallback: () => null,
    children: [
      { index: true, element: <HomePage /> },
      { path: ':instance/:contentType', element: <CollectionPage /> },
      { path: ':instance/:contentType/:slug', element: <ItemPage /> },
      { path: '*', element: <NotFoundPage /> },
    ],
  },
]);
