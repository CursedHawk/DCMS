import type { ParseHtmlOptions } from './html';
import type { LintContext, LintMarker } from './lint';
import type { ParsedCssRule, ParsedDocument } from './types';

/**
 * Message protocol between the builder's main thread and its parse/serialize
 * workers. Declared here, next to the functions that implement it, so the worker
 * shell in the admin app stays a few lines of `postMessage` plumbing and every
 * request/response pair is type-checked on both sides.
 */

export interface ParseHtmlRequest {
  kind: 'parse-html';
  id: number;
  /** The page this belongs to, echoed back so late replies can be dropped. */
  key: string;
  input: string;
  options?: ParseHtmlOptions;
}

export interface ParseCssRequest {
  kind: 'parse-css';
  id: number;
  key: string;
  input: string;
}

export interface FormatRequest {
  kind: 'format';
  id: number;
  key: string;
  html?: string;
  css?: string;
}

export interface IndexRequest {
  kind: 'index';
  id: number;
  key: string;
  /** Every stylesheet in the project, keyed by path. */
  stylesheets: Record<string, string>;
  /** Every page's markup, keyed by path, for class usage lookup. */
  pages?: Record<string, string>;
}

export interface LintRequest {
  kind: 'lint';
  id: number;
  /** The page path being linted, so a stale reply for another file is dropped. */
  key: string;
  source: string;
  context: LintContext;
}

export type ParseWorkerRequest =
  | ParseHtmlRequest
  | ParseCssRequest
  | FormatRequest
  | IndexRequest
  | LintRequest;

export interface ParseHtmlResponse {
  kind: 'parse-html';
  id: number;
  key: string;
  result: ParsedDocument;
}

export interface ParseCssResponse {
  kind: 'parse-css';
  id: number;
  key: string;
  result: ParsedCssRule[];
}

export interface FormatResponse {
  kind: 'format';
  id: number;
  key: string;
  html?: string;
  css?: string;
}

export interface IndexResponse {
  kind: 'index';
  id: number;
  key: string;
  /**
   * Class names *defined* somewhere in the project's CSS. Deliberately not the
   * ones merely used in markup: the "undefined class" diagnostic reads this list.
   */
  classNames: string[];
  /** Custom properties (`--x`) defined anywhere in the project's CSS. */
  customProperties: string[];
  /** Where each class is defined, for go-to-definition from `class="…"`. */
  classSources: Record<string, SymbolSource>;
  /** Where each custom property is defined, for go-to-definition from `var()`. */
  propertySources: Record<string, SymbolSource>;
  /** Every place markup uses each class, for "find all references" on a rule. */
  classUsages: Record<string, SymbolSource[]>;
}

export interface SymbolSource {
  path: string;
  line: number;
  column: number;
}

export interface LintResponse {
  kind: 'lint';
  id: number;
  key: string;
  diagnostics: LintMarker[];
}

export interface ErrorResponse {
  kind: 'error';
  id: number;
  key: string;
  message: string;
}

export type ParseWorkerResponse =
  | ParseHtmlResponse
  | ParseCssResponse
  | FormatResponse
  | IndexResponse
  | LintResponse
  | ErrorResponse;
