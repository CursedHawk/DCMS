import type * as Monaco from 'monaco-editor';

/**
 * The code editors' colour themes. Every Monaco on the page shares one theme, so the choice is
 * made once (the IDE's status bar) and kept in this browser. "Match the console" follows the
 * console's light/dark switch and its colours; the others are the familiar editor themes.
 *
 * Each theme is a palette of roles. The rules cover Monaco's own grammars (TypeScript, C#, HTML,
 * CSS, JSON, Markdown) and the semantic tokens the TypeScript and C# language services send
 * (semanticTokens.ts), so a class, a method and a constant look the same in both languages.
 */

interface Palette {
  label: string;
  base: 'vs' | 'vs-dark' | 'hc-black' | 'hc-light';
  fg: string;
  bg: string;
  comment: string;
  keyword: string;
  /** if, return, await, import…: VS Code's "keyword.control". */
  control: string;
  string: string;
  escape: string;
  number: string;
  regexp: string;
  type: string;
  func: string;
  variable: string;
  parameter?: string;
  property?: string;
  constant: string;
  tag: string;
  attribute: string;
  punctuation?: string;
  invalid?: string;
  italic?: { comment?: boolean; keyword?: boolean; parameter?: boolean };
  ui?: {
    lineHighlight?: string;
    selection?: string;
    lineNumber?: string;
    activeLineNumber?: string;
    cursor?: string;
    widget?: string;
    widgetBorder?: string;
    indent?: string;
    hover?: string;
  };
}

const LIGHT_PLUS: Palette = {
  label: 'Light+',
  base: 'vs',
  fg: '000000',
  bg: 'FFFFFF',
  comment: '008000',
  keyword: '0000FF',
  control: 'AF00DB',
  string: 'A31515',
  escape: 'EE0000',
  number: '098658',
  regexp: '811F3F',
  type: '267F99',
  func: '795E26',
  variable: '001080',
  constant: '0070C1',
  tag: '800000',
  attribute: 'E50000',
  punctuation: '800000',
};

const DARK_PLUS: Palette = {
  label: 'Dark+',
  base: 'vs-dark',
  fg: 'D4D4D4',
  bg: '1E1E1E',
  comment: '6A9955',
  keyword: '569CD6',
  control: 'C586C0',
  string: 'CE9178',
  escape: 'D7BA7D',
  number: 'B5CEA8',
  regexp: 'D16969',
  type: '4EC9B0',
  func: 'DCDCAA',
  variable: '9CDCFE',
  constant: '4FC1FF',
  tag: '569CD6',
  attribute: '9CDCFE',
  punctuation: '808080',
};

/** The console's own colours (apps/admin/src/index.css), with Light+/Dark+ syntax on top. */
const CONSOLE_LIGHT: Palette = {
  ...LIGHT_PLUS,
  label: 'Console light',
  fg: '0F1729',
  ui: {
    lineHighlight: 'F3F4F6',
    selection: '5048E533',
    lineNumber: '8A909C',
    activeLineNumber: '0F1729',
    cursor: '5048E5',
    widget: 'FFFFFF',
    widgetBorder: 'DADCE2',
    indent: 'E5E7EB',
  },
};

const CONSOLE_DARK: Palette = {
  ...DARK_PLUS,
  label: 'Console dark',
  bg: '090E1A',
  fg: 'E2E6EC',
  ui: {
    lineHighlight: '1A233380',
    selection: '6E67E94D',
    lineNumber: '5B6577',
    activeLineNumber: 'AEB5C2',
    cursor: '6E67E9',
    widget: '0F1524',
    widgetBorder: '243147',
    indent: '1A2333',
  },
};

const PALETTES: Record<string, Palette> = {
  'light-plus': LIGHT_PLUS,
  'dark-plus': DARK_PLUS,
  'github-light': {
    label: 'GitHub Light',
    base: 'vs',
    fg: '1F2328',
    bg: 'FFFFFF',
    comment: '6E7781',
    keyword: 'CF222E',
    control: 'CF222E',
    string: '0A3069',
    escape: '116329',
    number: '0550AE',
    regexp: '116329',
    type: '953800',
    func: '8250DF',
    variable: '1F2328',
    property: '0550AE',
    constant: '0550AE',
    tag: '116329',
    attribute: '0550AE',
    ui: { lineHighlight: 'EAEEF280', selection: '0969DA33', lineNumber: '8C959F', activeLineNumber: '1F2328', cursor: '0969DA' },
  },
  'github-dark': {
    label: 'GitHub Dark',
    base: 'vs-dark',
    fg: 'E6EDF3',
    bg: '0D1117',
    comment: '8B949E',
    keyword: 'FF7B72',
    control: 'FF7B72',
    string: 'A5D6FF',
    escape: '7EE787',
    number: '79C0FF',
    regexp: '7EE787',
    type: 'FFA657',
    func: 'D2A8FF',
    variable: 'E6EDF3',
    property: '79C0FF',
    constant: '79C0FF',
    tag: '7EE787',
    attribute: '79C0FF',
    ui: { lineHighlight: '6E76811A', selection: '264F78', lineNumber: '6E7681', activeLineNumber: 'E6EDF3', cursor: '2F81F7', widget: '161B22', widgetBorder: '30363D' },
  },
  'one-dark': {
    label: 'One Dark',
    base: 'vs-dark',
    fg: 'ABB2BF',
    bg: '282C34',
    comment: '7F848E',
    keyword: 'C678DD',
    control: 'C678DD',
    string: '98C379',
    escape: '56B6C2',
    number: 'D19A66',
    regexp: '56B6C2',
    type: 'E5C07B',
    func: '61AFEF',
    variable: 'E06C75',
    parameter: 'D19A66',
    constant: 'D19A66',
    tag: 'E06C75',
    attribute: 'D19A66',
    italic: { comment: true },
    ui: { lineHighlight: '2C313C', selection: '3E4451', lineNumber: '495162', activeLineNumber: 'ABB2BF', cursor: '528BFF', widget: '21252B', widgetBorder: '181A1F' },
  },
  monokai: {
    label: 'Monokai',
    base: 'vs-dark',
    fg: 'F8F8F2',
    bg: '272822',
    comment: '88846F',
    keyword: '66D9EF',
    control: 'F92672',
    string: 'E6DB74',
    escape: 'AE81FF',
    number: 'AE81FF',
    regexp: 'E6DB74',
    type: 'A6E22E',
    func: 'A6E22E',
    variable: 'F8F8F2',
    parameter: 'FD971F',
    constant: 'AE81FF',
    tag: 'F92672',
    attribute: 'A6E22E',
    italic: { keyword: true, parameter: true },
    ui: { lineHighlight: '3E3D32', selection: '49483E', lineNumber: '90908A', activeLineNumber: 'C2C2BF', cursor: 'F8F8F0', widget: '1E1F1C', widgetBorder: '414339' },
  },
  'solarized-light': {
    label: 'Solarized Light',
    base: 'vs',
    fg: '657B83',
    bg: 'FDF6E3',
    comment: '93A1A1',
    keyword: '859900',
    control: '859900',
    string: '2AA198',
    escape: 'DC322F',
    number: 'D33682',
    regexp: 'DC322F',
    type: 'CB4B16',
    func: '268BD2',
    variable: '268BD2',
    constant: 'CB4B16',
    tag: '268BD2',
    attribute: '93A1A1',
    italic: { comment: true },
    ui: { lineHighlight: 'EEE8D5', selection: 'EEE8D5', lineNumber: '93A1A1', activeLineNumber: '586E75', cursor: '657B83', widget: 'EEE8D5', widgetBorder: 'D3AF86' },
  },
  'solarized-dark': {
    label: 'Solarized Dark',
    base: 'vs-dark',
    fg: '839496',
    bg: '002B36',
    comment: '586E75',
    keyword: '859900',
    control: '859900',
    string: '2AA198',
    escape: 'DC322F',
    number: 'D33682',
    regexp: 'DC322F',
    type: 'CB4B16',
    func: '268BD2',
    variable: '268BD2',
    constant: 'CB4B16',
    tag: '268BD2',
    attribute: '93A1A1',
    italic: { comment: true },
    ui: { lineHighlight: '073642', selection: '274642', lineNumber: '566C74', activeLineNumber: '93A1A1', cursor: 'D30102', widget: '00212B', widgetBorder: '2B2B4A' },
  },
  'hc-dark': {
    label: 'High contrast dark',
    base: 'hc-black',
    fg: 'FFFFFF',
    bg: '000000',
    comment: '7CA668',
    keyword: '569CD6',
    control: 'C586C0',
    string: 'CE9178',
    escape: 'D7BA7D',
    number: 'B5CEA8',
    regexp: 'D16969',
    type: '4EC9B0',
    func: 'DCDCAA',
    variable: '9CDCFE',
    constant: '4FC1FF',
    tag: '569CD6',
    attribute: '9CDCFE',
  },
  'hc-light': {
    label: 'High contrast light',
    base: 'hc-light',
    fg: '292929',
    bg: 'FFFFFF',
    comment: '515151',
    keyword: '0F4A85',
    control: 'B5200D',
    string: '0F4A85',
    escape: 'EE0000',
    number: '096D48',
    regexp: '811F3F',
    type: '185E73',
    func: '5E2CBC',
    variable: '001080',
    constant: '02715D',
    tag: '0F4A85',
    attribute: '264F78',
  },
};

/** What the picker offers: "auto" (match the console) first, then each theme. */
export const EDITOR_THEMES: { id: string; label: string }[] = Object.entries(PALETTES).map(([id, p]) => ({ id, label: p.label }));

/** Keywords VS Code colours as control flow. Monaco's C# grammar, and ours for TypeScript, emit `keyword.<word>`. */
const CONTROL = [
  'if', 'else', 'switch', 'case', 'default', 'for', 'foreach', 'while', 'do', 'break', 'continue', 'return',
  'throw', 'try', 'catch', 'finally', 'goto', 'yield', 'await', 'import', 'export', 'from',
];
/** TypeScript's primitive types are types, not keywords, to VS Code. */
const TS_PRIMITIVES = ['any', 'boolean', 'number', 'string', 'symbol', 'bigint', 'object', 'unknown', 'never', 'void', 'undefined'];

function rules(p: Palette): Monaco.editor.ITokenThemeRule[] {
  const parameter = p.parameter ?? p.variable;
  const property = p.property ?? p.variable;
  const punctuation = p.punctuation ?? p.fg;
  const it = (on?: boolean) => (on ? 'italic' : '');
  return [
    { token: '', foreground: p.fg, background: p.bg },
    { token: 'invalid', foreground: p.invalid ?? 'F44747' },
    { token: 'emphasis', fontStyle: 'italic' },
    { token: 'strong', fontStyle: 'bold' },
    { token: 'comment', foreground: p.comment, fontStyle: it(p.italic?.comment) },
    { token: 'keyword', foreground: p.keyword, fontStyle: it(p.italic?.keyword) },
    { token: 'keyword.flow', foreground: p.control },
    ...CONTROL.map((w) => ({ token: `keyword.${w}`, foreground: p.control, fontStyle: '' })),
    ...TS_PRIMITIVES.map((w) => ({ token: `keyword.${w}.ts`, foreground: p.type, fontStyle: '' })),
    { token: 'string', foreground: p.string },
    { token: 'string.escape', foreground: p.escape },
    { token: 'number', foreground: p.number },
    { token: 'regexp', foreground: p.regexp },
    { token: 'type', foreground: p.type },
    { token: 'namespace', foreground: p.type },
    { token: 'identifier', foreground: p.variable },
    { token: 'delimiter', foreground: p.fg },
    { token: 'annotation', foreground: p.type },
    { token: 'constant', foreground: p.constant },
    { token: 'predefined', foreground: p.func },
    // HTML, XML
    { token: 'tag', foreground: p.tag },
    { token: 'metatag', foreground: p.tag },
    { token: 'delimiter.html', foreground: punctuation },
    { token: 'delimiter.xml', foreground: punctuation },
    { token: 'attribute.name', foreground: p.attribute },
    { token: 'attribute.value', foreground: p.string },
    { token: 'attribute.value.number', foreground: p.number },
    { token: 'attribute.value.unit', foreground: p.number },
    { token: 'attribute.value.hex', foreground: p.fg },
    // JSON, YAML, Markdown
    { token: 'string.key.json', foreground: property },
    { token: 'string.value.json', foreground: p.string },
    { token: 'keyword.json', foreground: p.keyword },
    { token: 'key', foreground: property },
    { token: 'variable', foreground: p.variable },
    { token: 'variable.predefined', foreground: p.constant },
    { token: 'string.link', foreground: p.string },
    // Semantic tokens (semanticTokens.ts). Every type the legend has needs a rule: a token type
    // without one is painted the plain foreground over the grammar's colour.
    { token: 'class', foreground: p.type },
    { token: 'struct', foreground: p.type },
    { token: 'interface', foreground: p.type },
    { token: 'enum', foreground: p.type },
    { token: 'typeParameter', foreground: p.type },
    { token: 'function', foreground: p.func },
    { token: 'method', foreground: p.func },
    { token: 'variable.readonly', foreground: p.constant },
    { token: 'parameter', foreground: parameter, fontStyle: it(p.italic?.parameter) },
    { token: 'property', foreground: property },
    { token: 'property.readonly', foreground: p.constant },
    { token: 'event', foreground: property },
    { token: 'enumMember', foreground: p.constant },
    { token: 'label', foreground: p.fg },
  ];
}

function colors(p: Palette): Monaco.editor.IColors {
  const ui = p.ui ?? {};
  const out: Record<string, string> = { 'editor.background': `#${p.bg}`, 'editor.foreground': `#${p.fg}` };
  const set = (keys: string[], value?: string) => {
    if (value) for (const key of keys) out[key] = `#${value}`;
  };
  set(['editor.lineHighlightBackground'], ui.lineHighlight);
  set(['editor.lineHighlightBorder'], ui.lineHighlight && '00000000');
  set(['editor.selectionBackground'], ui.selection);
  set(['editorLineNumber.foreground'], ui.lineNumber);
  set(['editorLineNumber.activeForeground'], ui.activeLineNumber);
  set(['editorCursor.foreground'], ui.cursor);
  set(['editorIndentGuide.background1', 'editorWhitespace.foreground'], ui.indent);
  set(
    ['editorWidget.background', 'editorSuggestWidget.background', 'editorHoverWidget.background', 'quickInput.background', 'peekViewResult.background'],
    ui.widget,
  );
  set(['editorWidget.border', 'editorSuggestWidget.border', 'editorHoverWidget.border'], ui.widgetBorder);
  set(['editorGutter.background', 'minimap.background', 'peekViewEditor.background'], p.bg);
  return out;
}

const themeId = (id: string) => `dcms-${id}`;

export function defineEditorThemes(monaco: typeof Monaco): void {
  const all: Record<string, Palette> = { ...PALETTES, 'console-light': CONSOLE_LIGHT, 'console-dark': CONSOLE_DARK };
  for (const [id, p] of Object.entries(all)) {
    monaco.editor.defineTheme(themeId(id), { base: p.base, inherit: true, rules: rules(p), colors: colors(p) });
  }
}

const STORAGE_KEY = 'dcms.editorTheme';

/** The chosen theme: "auto" or a key of {@link EDITOR_THEMES}. */
export function editorThemeChoice(): string {
  const stored = localStorage.getItem(STORAGE_KEY);
  return stored && stored in PALETTES ? stored : 'auto';
}

let consoleTheme: 'light' | 'dark' = 'light';
let monacoRef: typeof Monaco | undefined;

/** The Monaco theme for a choice, with the console's light/dark for "auto". */
export function resolveEditorTheme(choice: string, console: 'light' | 'dark'): string {
  return themeId(choice in PALETTES ? choice : `console-${console}`);
}

export function applyTheme(monaco: typeof Monaco, console: 'light' | 'dark'): void {
  monacoRef = monaco;
  consoleTheme = console;
  monaco.editor.setTheme(resolveEditorTheme(editorThemeChoice(), console));
}

/** Keeps the choice and repaints every editor on the page. */
export function setEditorThemeChoice(choice: string): void {
  if (choice === 'auto') localStorage.removeItem(STORAGE_KEY);
  else localStorage.setItem(STORAGE_KEY, choice);
  if (monacoRef) applyTheme(monacoRef, consoleTheme);
}
