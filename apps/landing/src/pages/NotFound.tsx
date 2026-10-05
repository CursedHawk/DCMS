import { Footer, Header } from '../components/Layout';

export function NotFound() {
  return (
    <>
      <Header />
      <main className="mx-auto max-w-6xl px-4 py-28 sm:px-6 lg:py-40">
        <p className="font-mono text-muted-foreground">404</p>
        <h1 className="mt-3 text-4xl font-semibold tracking-tight sm:text-5xl">
          This page does not exist
        </h1>
        <p className="mt-4 max-w-prose text-lg text-muted-foreground">
          The address may be mistyped, or the page may have moved.
        </p>
        <a
          href="/"
          className="mt-8 inline-block rounded-full bg-foreground px-6 py-3 font-medium text-background hover:opacity-85"
        >
          Go to the home page
        </a>
      </main>
      <Footer />
    </>
  );
}
