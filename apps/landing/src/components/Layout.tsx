import { Moon, Sun } from 'lucide-react';
import { CONSOLE_URL, CONTACT_EMAIL } from '../site';

export function Logo({ className = 'size-8' }: { className?: string }) {
  return (
    <svg viewBox="0 0 32 32" className={className} aria-hidden="true">
      <rect width="32" height="32" rx="8" className="fill-primary" />
      <path
        d="M10 8h6.2c4.9 0 8.3 3.2 8.3 8s-3.4 8-8.3 8H10V8Zm4.4 3.6v8.8h1.6c2.6 0 4.2-1.7 4.2-4.4s-1.6-4.4-4.2-4.4h-1.6Z"
        className="fill-primary-foreground"
      />
    </svg>
  );
}

/*
 * No React state: both icons render and the `.dark` class on <html> (set before paint by
 * index.html) picks one. So the server's HTML and the first client render always agree.
 */
function ThemeToggle() {
  const toggle = () => {
    const dark = document.documentElement.classList.toggle('dark');
    try {
      localStorage.setItem('dcms.theme', dark ? 'dark' : 'light');
    } catch {
      /* private mode: the choice just isn't remembered */
    }
  };
  return (
    <button
      type="button"
      onClick={toggle}
      aria-label="Switch between light and dark theme"
      className="grid size-10 place-items-center rounded-full text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
    >
      <Moon className="size-[18px] dark:hidden" aria-hidden="true" />
      <Sun className="hidden size-[18px] dark:block" aria-hidden="true" />
    </button>
  );
}

const nav = [
  ['/#build', 'How you build'],
  ['/#plugins', 'Plugins'],
  ['/#hosting', 'Hosting'],
  ['/#faq', 'FAQ'],
] as const;

export function Header() {
  return (
    <header className="sticky top-0 z-40 border-b border-transparent bg-background/80 backdrop-blur-md supports-[backdrop-filter]:bg-background/65">
      <div className="mx-auto flex h-16 max-w-6xl items-center gap-6 px-4 sm:px-6">
        <a
          href="/"
          className="flex items-center gap-2.5 font-semibold tracking-tight"
          aria-label="DCMS home"
        >
          <Logo />
          <span className="text-lg">DCMS</span>
        </a>
        <nav aria-label="Main" className="hidden flex-1 items-center gap-1 md:flex">
          {nav.map(([href, label]) => (
            <a
              key={href}
              href={href}
              className="rounded-md px-3 py-2 text-[0.9375rem] text-muted-foreground transition-colors hover:text-foreground"
            >
              {label}
            </a>
          ))}
        </nav>
        <div className="ml-auto flex items-center gap-2 md:ml-0">
          <ThemeToggle />
          <a
            href={CONSOLE_URL}
            className="rounded-full bg-foreground px-4 py-2 text-[0.9375rem] font-medium text-background transition-opacity hover:opacity-85"
          >
            Sign in
          </a>
        </div>
      </div>
    </header>
  );
}

export function Footer() {
  return (
    <footer className="border-t">
      <div className="mx-auto grid max-w-6xl gap-8 px-4 py-12 text-[0.9375rem] sm:px-6 md:grid-cols-[1fr_auto_auto] md:gap-16">
        <div className="max-w-sm">
          <a href="/" className="flex items-center gap-2.5 font-semibold" aria-label="DCMS home">
            <Logo className="size-7" />
            DCMS
          </a>
          <p className="mt-3 text-muted-foreground">
            Websites and content, built, versioned and hosted in one place. Run by HighGeek in the
            European Union.
          </p>
        </div>
        <nav aria-label="Product">
          <h2 className="font-medium">Product</h2>
          <ul className="mt-3 space-y-2 text-muted-foreground">
            {nav.map(([href, label]) => (
              <li key={href}>
                <a href={href} className="hover:text-foreground">
                  {label}
                </a>
              </li>
            ))}
            <li>
              <a href={CONSOLE_URL} className="hover:text-foreground">
                Admin console
              </a>
            </li>
          </ul>
        </nav>
        <nav aria-label="Legal">
          <h2 className="font-medium">Company</h2>
          <ul className="mt-3 space-y-2 text-muted-foreground">
            <li>
              <a href="/privacy-policy" className="hover:text-foreground">
                Privacy policy
              </a>
            </li>
            <li>
              <a href="/terms-of-service" className="hover:text-foreground">
                Terms of service
              </a>
            </li>
            <li>
              <a href={`mailto:${CONTACT_EMAIL}`} className="hover:text-foreground">
                {CONTACT_EMAIL}
              </a>
            </li>
          </ul>
        </nav>
      </div>
      <div className="mx-auto max-w-6xl px-4 pb-10 text-sm text-muted-foreground sm:px-6">
        © 2026 HighGeek
      </div>
    </footer>
  );
}
