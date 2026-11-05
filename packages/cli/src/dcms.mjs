#!/usr/bin/env node
import { main } from './cli.mjs';

main(process.argv.slice(2)).then(
  (code) => process.exit(code ?? 0),
  (e) => {
    console.error(`dcms: ${e instanceof Error ? e.message : e}`);
    process.exit(1);
  },
);
