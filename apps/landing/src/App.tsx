import { Home } from './pages/Home';
import { NotFound } from './pages/NotFound';
import { Privacy } from './pages/Privacy';
import { Terms } from './pages/Terms';

const pages: Record<string, () => React.JSX.Element> = {
  '/': Home,
  '/privacy-policy': Privacy,
  '/terms-of-service': Terms,
};

export function App({ path }: { path: string }) {
  const Page = pages[path.replace(/\/+$/, '') || '/'] ?? NotFound;
  return <Page />;
}
