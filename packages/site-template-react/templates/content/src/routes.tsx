import { createBrowserRouter } from 'react-router';
import { Layout } from './components/Layout';
import { CollectionPage } from './pages/CollectionPage';
import { HomePage } from './pages/HomePage';
import { ItemPage } from './pages/ItemPage';
import { NotFoundPage } from './pages/NotFoundPage';

/**
 * Every page of the site, in one place. Add a page by creating it in `src/pages/` and adding a
 * route here — static paths such as `/about` win over the `:instance` patterns below, so they
 * can be added in any order.
 */
export const router = createBrowserRouter([
  {
    element: <Layout />,
    children: [
      { index: true, element: <HomePage /> },
      { path: ':instance/:contentType', element: <CollectionPage /> },
      { path: ':instance/:contentType/:slug', element: <ItemPage /> },
      { path: '*', element: <NotFoundPage /> },
    ],
  },
]);
