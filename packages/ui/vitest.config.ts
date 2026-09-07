import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    // The components are Radix wrappers; every meaningful assertion is about rendered DOM,
    // focus and keyboard behaviour, so there is no useful node-environment subset to run.
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
  },
});
