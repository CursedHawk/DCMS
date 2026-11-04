/// <reference lib="webworker" />

// The C# language service's Web Worker (ADR 0030): Roslyn on .NET's WebAssembly runtime
// (src/Functions/Dcms.Functions.LanguageWorker, published under /csharp/). Requests are answered
// one at a time in the order they came, so a file's text always reaches Roslyn before a question
// about it.

type Exports = Record<string, (...args: unknown[]) => unknown>;
interface Ask {
  id: number;
  base: string;
  method: string;
  args: unknown[];
}

let roslyn: Promise<Exports> | undefined;

// .NET takes any Web Worker for one of its own pthreads, which waits on a main runtime that never
// comes: boot hangs after onRuntimeInitialized. Declaring the worker a "sidecar" (.NET's word for
// a runtime that lives in a dedicated worker) makes it start one of its own.
(self as unknown as { dotnetSidecar: boolean }).dotnetSidecar = true;

function boot(base: string): Promise<Exports> {
  return (roslyn ??= (async () => {
    // Served beside the console, not bundled: it is .NET's own loader and assemblies.
    const { dotnet } = await import(/* @vite-ignore */ `${base}csharp/_framework/dotnet.js`);
    const runtime = await dotnet.create();
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    return exports.Worker as Exports;
  })());
}

let queue: Promise<void> = Promise.resolve();

self.onmessage = (event: MessageEvent<Ask>) => {
  const { id, base, method, args } = event.data;
  queue = queue.then(async () => {
    try {
      const worker = await boot(base);
      const result = await worker[method](...args);
      self.postMessage({ id, result });
    } catch (error) {
      self.postMessage({ id, error: error instanceof Error ? error.message : String(error) });
    }
  });
};
