import type * as Monaco from 'monaco-editor';
import type { AppConfig, ExpressionFunction, FieldType, FlowDef } from '../../api';

/**
 * The flow expression language (Automation/Expressions.cs) as a Monaco language: colours,
 * completion, hovers and argument hints. It teaches only what the server parses — the functions
 * and variables come from GET /_automation/language, and the problems from POST
 * /_automation/check — so the editor can never offer something a save would refuse.
 *
 * Everything here but `registerExpressionLanguage` is pure, over the text before the cursor.
 */

export const EXPRESSION_LANGUAGE = 'dcms-expression';

/** A name `row.` or `previous.` offers: a field, a lookup or a system column. */
export interface MemberInfo {
  name: string;
  type: string;
  doc: string;
}

/** What one expression editor can see. Per document: two step conditions see different steps. */
export interface ExpressionContext {
  functions: readonly ExpressionFunction[];
  variables: readonly string[];
  /** The trigger table's members, or null when the trigger has no row. */
  row: readonly MemberInfo[] | null;
  /** Whether `previous` holds anything (row.updated only). */
  hasPrevious: boolean;
  /** Ids of the steps that run before this expression. */
  steps: readonly string[];
}

export interface Suggestion {
  label: string;
  kind: 'variable' | 'field' | 'function' | 'keyword' | 'property';
  detail: string;
  documentation?: string;
  insertText: string;
  /** Insert as a snippet (`len($1)`). */
  snippet?: boolean;
  /** Orders the list: a table's own fields before the columns every record has. */
  sortText?: string;
}

const VARIABLE_DOCS: Record<string, string> = {
  event: 'The event that started the run: eventName, occurredAt, entity, payload, correlationId…',
  row: 'The record the event is about (row.* triggers): its fields by api name, plus id, version, created_at, updated_at, created_by and updated_by. Null for other triggers.',
  previous: 'row.updated only: the changed fields’ values before the change.',
  changedFields: 'row.updated only: the api names of the fields whose value changed, e.g. "status" in changedFields.',
  input: 'manual and flow.invoke runs: the input the flow was started with.',
  steps: 'The outputs of the steps that ran before this one, by step id: steps.find_owner.email. A skipped step’s output is null.',
  run: 'This run: id, flow (its api name), revision and depth.',
};

const RUN_MEMBERS: MemberInfo[] = [
  { name: 'id', type: 'string', doc: 'The run id.' },
  { name: 'flow', type: 'string', doc: 'The flow’s api name.' },
  { name: 'revision', type: 'number', doc: 'The published revision the run executes.' },
  { name: 'depth', type: 'number', doc: 'How many flow runs deep this run is (0 for a person’s action).' },
];

// AppEvent (Dcms.Plugins.DynamicApps.Api/AppEvents.cs), as JsonSerializerOptions.Web names it.
const EVENT_MEMBERS: MemberInfo[] = [
  { name: 'eventName', type: 'string', doc: 'row.created, row.updated, row.deleted, relation.created, … or flow.event.{name}.' },
  { name: 'eventId', type: 'string', doc: 'The event’s id.' },
  { name: 'occurredAt', type: 'string', doc: 'When it happened (ISO 8601, UTC).' },
  { name: 'revision', type: 'number', doc: 'The published revision it happened under.' },
  { name: 'entity', type: 'object', doc: 'What it is about: { type, id } — a table’s api name and record id.' },
  { name: 'payload', type: 'object', doc: 'Row events: record (and previous); relation events: relationship, sourceId, targetId.' },
  { name: 'changedFields', type: 'string[]', doc: 'Updates: the api names of the changed fields.' },
  { name: 'correlationId', type: 'string', doc: 'Shared by everything one original action set off.' },
  { name: 'causationId', type: 'string | null', doc: 'The flow run whose write caused this event.' },
  { name: 'depth', type: 'number', doc: 'How many flow runs deep the event is.' },
  { name: 'sourcePlugin', type: 'string', doc: 'The plugin that raised it.' },
  { name: 'sourceInstanceId', type: 'string', doc: 'The plugin instance that raised it.' },
  { name: 'tenantId', type: 'string', doc: 'The workspace.' },
  { name: 'eventVersion', type: 'number', doc: 'The event’s schema version.' },
];

// RecordCodec.Read: every record carries these besides its fields.
const SYSTEM_MEMBERS: MemberInfo[] = [
  { name: 'id', type: 'string', doc: 'The record id.' },
  { name: 'version', type: 'number', doc: 'Bumped on every write.' },
  { name: 'created_at', type: 'string', doc: 'When the record was created (ISO 8601).' },
  { name: 'updated_at', type: 'string', doc: 'When the record was last written (ISO 8601).' },
  { name: 'created_by', type: 'string | null', doc: 'Who created it.' },
  { name: 'updated_by', type: 'string | null', doc: 'Who last wrote it.' },
];

const TYPE_NAMES: Record<FieldType, string> = {
  text: 'string', longText: 'string', email: 'string', url: 'string', choice: 'string', media: 'string',
  date: 'string (yyyy-MM-dd)', dateTime: 'string (ISO 8601)',
  integer: 'number', decimal: 'number', boolean: 'boolean', multiChoice: 'string[]', json: 'any',
};

/** What a flow's expressions can see, from the draft being edited — not the published config. */
export function contextFor(
  config: AppConfig,
  trigger: Pick<FlowDef['trigger'], 'event' | 'tableId'>,
  stepsBefore: readonly string[],
  language: { functions: readonly ExpressionFunction[]; variables: readonly string[] },
): ExpressionContext {
  const table = trigger.event.startsWith('row.') ? config.tables.find((t) => t.id === trigger.tableId) : undefined;
  const row = table
    ? [
        ...table.fields.map((f) => ({ name: f.apiName, type: `${TYPE_NAMES[f.type]} | null`, doc: f.displayName + (f.description ? ` — ${f.description}` : '') })),
        ...config.relationships
          .filter((r) => r.sourceTableId === table.id && r.kind !== 'manyToMany')
          .map((r) => ({
            name: r.apiName,
            type: 'string | null',
            doc: `${r.displayName ?? r.apiName} — the id of the ${config.tables.find((t) => t.id === r.targetTableId)?.displayName ?? 'related'} record`,
          })),
        ...SYSTEM_MEMBERS,
      ]
    : null;
  return { ...language, row, hasPrevious: trigger.event === 'row.updated', steps: stepsBefore };
}

/** Whether the cursor sits inside a string literal: nothing is offered there. */
function inString(text: string): boolean {
  let quote: string | null = null;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quote) {
      if (c === '\\') i++;
      else if (c === quote) quote = null;
    } else if (c === '"' || c === "'") {
      quote = c;
    }
  }
  return quote !== null;
}

function membersOf(root: string, ctx: ExpressionContext): readonly MemberInfo[] {
  switch (root) {
    case 'row':
      return ctx.row ?? [];
    case 'previous':
      return ctx.hasPrevious ? (ctx.row ?? []).filter((m) => !SYSTEM_MEMBERS.includes(m)) : [];
    case 'steps':
      return ctx.steps.map((id) => ({ name: id, type: 'step output', doc: `The output of step ${id}.` }));
    case 'run':
      return RUN_MEMBERS;
    case 'event':
      return EVENT_MEMBERS;
    default:
      return [];
  }
}

/**
 * What to offer at the end of `before`, and from which offset the typed word is replaced.
 * `row.` offers the trigger table's members; a bare word offers variables, functions and keywords.
 */
export function suggestionsAt(before: string, ctx: ExpressionContext): { from: number; items: Suggestion[] } {
  if (inString(before)) return { from: before.length, items: [] };
  const member = /([A-Za-z_]\w*)\s*\.\s*(\w*)$/.exec(before);
  if (member) {
    // Only a root variable's members are known: row.owner. offers nothing rather than guessing.
    const preceding = before.slice(0, member.index).trimEnd();
    if (preceding.endsWith('.') || preceding.endsWith(']')) return { from: before.length, items: [] };
    const items = membersOf(member[1]!, ctx).map<Suggestion>((m) => ({
      label: m.name, kind: member[1] === 'row' || member[1] === 'previous' ? 'field' : 'property', detail: m.type, documentation: m.doc, insertText: m.name,
      sortText: `${SYSTEM_MEMBERS.includes(m) ? 1 : 0}${m.name}`,
    }));
    return { from: before.length - member[2]!.length, items };
  }
  const word = /\w*$/.exec(before)![0];
  const items: Suggestion[] = [
    ...ctx.variables.map<Suggestion>((v) => ({ label: v, kind: 'variable', detail: 'variable', documentation: VARIABLE_DOCS[v], insertText: v })),
    ...ctx.functions.map<Suggestion>((f) => ({
      label: f.name, kind: 'function', detail: `${f.signature} → ${f.returns}`, documentation: f.description,
      insertText: f.maxArgs === 0 ? `${f.name}()` : `${f.name}($1)`, snippet: f.maxArgs > 0,
    })),
    ...['true', 'false', 'null', 'in'].map<Suggestion>((k) => ({ label: k, kind: 'keyword', detail: 'keyword', insertText: k })),
  ];
  return { from: before.length - word.length, items };
}

/** The innermost function call the cursor is inside, and which argument it is at. */
export function callAt(before: string): { name: string; argument: number } | null {
  let quote: string | null = null;
  // The brackets still open at the cursor, skipping string contents.
  const open: number[] = [];
  for (let i = 0; i < before.length; i++) {
    const c = before[i];
    if (quote) {
      if (c === '\\') i++;
      else if (c === quote) quote = null;
      continue;
    }
    if (c === '"' || c === "'") quote = c;
    else if (c === '(' || c === '[') open.push(i);
    else if (c === ')' || c === ']') open.pop();
  }
  if (quote) return null;
  const at = open.at(-1);
  if (at === undefined || before[at] !== '(') return null;
  const name = /([A-Za-z_]\w*)\s*$/.exec(before.slice(0, at))?.[1];
  if (!name) return null;
  let depth = 0;
  let argument = 0;
  quote = null;
  for (let i = at + 1; i < before.length; i++) {
    const c = before[i];
    if (quote) {
      if (c === '\\') i++;
      else if (c === quote) quote = null;
    } else if (c === '"' || c === "'") quote = c;
    else if (c === '(' || c === '[') depth++;
    else if (c === ')' || c === ']') depth--;
    else if (c === ',' && depth === 0) argument++;
  }
  return { name, argument };
}

/** The hover text for a word: a function's signature, a variable, or a member of row/previous/run/event. */
export function hoverFor(word: string, before: string, ctx: ExpressionContext): string | null {
  const root = /([A-Za-z_]\w*)\s*\.\s*$/.exec(before)?.[1];
  if (root) {
    const m = membersOf(root, ctx).find((x) => x.name === word);
    return m ? `**${root}.${m.name}**: \`${m.type}\`\n\n${m.doc}` : null;
  }
  const f = ctx.functions.find((x) => x.name === word);
  if (f) return `\`${f.signature}\` → \`${f.returns}\`\n\n${f.description}`;
  if (ctx.variables.includes(word)) return `**${word}**\n\n${VARIABLE_DOCS[word] ?? ''}`;
  return null;
}

// ------------------------------------------------------------------------------- Monaco wiring

/** By model uri: each expression editor's own view of the flow. */
const contexts = new Map<string, ExpressionContext>();

export function setExpressionContext(uri: string, ctx: ExpressionContext | null): void {
  if (ctx) contexts.set(uri, ctx);
  else contexts.delete(uri);
}

let registered = false;

/**
 * Registers the language and its providers, once per page: Monaco's providers are global to a
 * language, so registering per editor would stack duplicates. They read `contexts` per document.
 */
export function registerExpressionLanguage(monaco: typeof Monaco): void {
  if (registered) return;
  registered = true;
  monaco.languages.register({ id: EXPRESSION_LANGUAGE });
  monaco.languages.setLanguageConfiguration(EXPRESSION_LANGUAGE, {
    brackets: [['(', ')'], ['[', ']']],
    autoClosingPairs: [
      { open: '(', close: ')' },
      { open: '[', close: ']' },
      { open: '"', close: '"', notIn: ['string'] },
      { open: "'", close: "'", notIn: ['string'] },
    ],
    surroundingPairs: [{ open: '(', close: ')' }, { open: '"', close: '"' }, { open: "'", close: "'" }],
  });
  monaco.languages.setMonarchTokensProvider(EXPRESSION_LANGUAGE, {
    keywords: ['true', 'false', 'null', 'in'],
    tokenizer: {
      root: [
        [/[A-Za-z_]\w*(?=\s*\()/, 'support.function'],
        [/[A-Za-z_]\w*/, { cases: { '@keywords': 'keyword', '@default': 'identifier' } }],
        [/\d+(\.\d+)?/, 'number'],
        [/"([^"\\]|\\.)*"/, 'string'],
        [/'([^'\\]|\\.)*'/, 'string'],
        [/"([^"\\]|\\.)*$/, 'string.invalid'],
        [/'([^'\\]|\\.)*$/, 'string.invalid'],
        [/&&|\|\||==|!=|<=|>=|[<>!?:+\-*/%]/, 'operator'],
        [/[()[\],.]/, 'delimiter'],
      ],
    },
  });

  const KINDS = {
    variable: monaco.languages.CompletionItemKind.Variable,
    field: monaco.languages.CompletionItemKind.Field,
    property: monaco.languages.CompletionItemKind.Property,
    function: monaco.languages.CompletionItemKind.Function,
    keyword: monaco.languages.CompletionItemKind.Keyword,
  } as const;

  const before = (model: Monaco.editor.ITextModel, position: Monaco.IPosition) =>
    model.getValueInRange({ startLineNumber: 1, startColumn: 1, endLineNumber: position.lineNumber, endColumn: position.column });

  monaco.languages.registerCompletionItemProvider(EXPRESSION_LANGUAGE, {
    triggerCharacters: ['.'],
    provideCompletionItems(model, position) {
      const ctx = contexts.get(model.uri.toString());
      if (!ctx) return { suggestions: [] };
      const text = before(model, position);
      const { from, items } = suggestionsAt(text, ctx);
      const start = model.getPositionAt(from);
      const range = new monaco.Range(start.lineNumber, start.column, position.lineNumber, position.column);
      return {
        suggestions: items.map((s) => ({
          label: s.label,
          kind: KINDS[s.kind],
          detail: s.detail,
          documentation: s.documentation ? { value: s.documentation } : undefined,
          insertText: s.insertText,
          sortText: s.sortText,
          insertTextRules: s.snippet ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet : undefined,
          // A function's argument hint opens as soon as it is inserted.
          command: s.snippet ? { id: 'editor.action.triggerParameterHints', title: '' } : undefined,
          range,
        })),
      };
    },
  });

  monaco.languages.registerHoverProvider(EXPRESSION_LANGUAGE, {
    provideHover(model, position) {
      const ctx = contexts.get(model.uri.toString());
      const word = model.getWordAtPosition(position);
      if (!ctx || !word) return null;
      const text = before(model, { lineNumber: position.lineNumber, column: word.startColumn });
      const value = hoverFor(word.word, text, ctx);
      return value
        ? { contents: [{ value }], range: new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn) }
        : null;
    },
  });

  monaco.languages.registerSignatureHelpProvider(EXPRESSION_LANGUAGE, {
    signatureHelpTriggerCharacters: ['(', ','],
    signatureHelpRetriggerCharacters: [','],
    provideSignatureHelp(model, position) {
      const ctx = contexts.get(model.uri.toString());
      const call = ctx ? callAt(before(model, position)) : null;
      const f = call && ctx!.functions.find((x) => x.name === call.name);
      if (!call || !f) return null;
      const params = f.signature.slice(f.signature.indexOf('(') + 1, f.signature.lastIndexOf(')')).split(',').map((p) => p.trim()).filter(Boolean);
      return {
        value: {
          signatures: [{ label: f.signature, documentation: { value: f.description }, parameters: params.map((label) => ({ label })) }],
          activeSignature: 0,
          activeParameter: Math.min(call.argument, Math.max(params.length - 1, 0)),
        },
        dispose: () => {},
      };
    },
  });
}
