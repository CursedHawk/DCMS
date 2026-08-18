import { handleRequest, type ParseWorkerRequest } from '@dcms/gjs-parse';

// Worker shell. Everything expensive lives in @dcms/gjs-parse so it can be unit
// tested in node; this file only moves messages across the thread boundary.
//
// What runs here: HTML tokenizing (htmlparser2), CSS parsing (css-tree),
// HTML/CSS formatting (js-beautify) and the project-wide class/custom-property
// index. Those are the operations that scale with document size, so keeping them
// off the main thread is what stops a large page from freezing the canvas on
// load, on every code-view edit and on every save.

self.onmessage = (event: MessageEvent<ParseWorkerRequest>) => {
  self.postMessage(handleRequest(event.data));
};
