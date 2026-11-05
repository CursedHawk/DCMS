// What a TypeScript function takes and returns, as JSON Schema (ADR 0029): the site's OpenAPI
// document describes each HTTP function with it, and admin-api checks calls against the input.
//
// Trusted code: the TypeScript compiler (a pinned copy, ScriptRunner:TypeScriptPath) reading the
// repository, never running it. Started by FunctionBuilder after `deno check` passed, confined
// like it, with read access to the build directory and the compiler only.
//
//   deno run --allow-read=<root>,<typescript> type-schemas.js <typescript> <root> <name>=<file>...
//
// Prints {"<name>": {"input": schema, "output": schema}}. A type it cannot say (a function,
// a class instance, the SDK's Json) is {} -- anything -- never a guess.

const [tsDir, root, ...entries] = Deno.args;
// The compiler takes a Node-like `process` for Node and reaches for its file system; it is given
// its own host below instead.
delete globalThis.process;
(0, eval)(Deno.readTextFileSync(`${tsDir}/lib/typescript.js`));
const ts = globalThis.ts;

const SDK = `${root}/.dcms/functions.ts`;
const options = {
  strict: true,
  target: ts.ScriptTarget.ES2022,
  module: ts.ModuleKind.ESNext,
  moduleResolution: ts.ModuleResolutionKind.Bundler,
  allowImportingTsExtensions: true,
  noEmit: true,
  skipLibCheck: true,
  types: [],
  lib: ["lib.es2022.d.ts"],
  baseUrl: root,
  paths: { "@dcms/functions": [".dcms/functions.ts"] },
};

const exists = (path) => {
  try {
    return Deno.statSync(path).isFile;
  } catch {
    return false;
  }
};
const host = {
  getSourceFile: (fileName, languageVersion) =>
    exists(fileName) ? ts.createSourceFile(fileName, Deno.readTextFileSync(fileName), languageVersion, true) : undefined,
  getDefaultLibFileName: () => `${tsDir}/lib/lib.d.ts`,
  getDefaultLibLocation: () => `${tsDir}/lib`,
  writeFile: () => {},
  getCurrentDirectory: () => root,
  getCanonicalFileName: (f) => f,
  useCaseSensitiveFileNames: () => true,
  getNewLine: () => "\n",
  fileExists: exists,
  readFile: (f) => (exists(f) ? Deno.readTextFileSync(f) : undefined),
  directoryExists: (d) => {
    try {
      return Deno.statSync(d).isDirectory;
    } catch {
      return false;
    }
  },
  getDirectories: () => [],
};

const files = Object.fromEntries(entries.map((e) => e.split("=")).map(([name, file]) => [name, `${root}/${file}`]));
const program = ts.createProgram(Object.values(files), options, host);
const checker = program.getTypeChecker();

const MAX_DEPTH = 8;
const MAX_PROPERTIES = 200;
/** Schema nodes one input or output may have: wide types nest into a lot, and past this it says "anything". */
const MAX_NODES = 1_000;
const MAX_CHARS = 32_000;
let nodes = 0;

/** The SDK's own Json, which says "any JSON": no schema narrows it. */
const isSdk = (symbol) => symbol?.declarations?.some((d) => d.getSourceFile().fileName === SDK);

function schemaOf(type, depth, seen) {
  if (depth > MAX_DEPTH || seen.has(type) || ++nodes > MAX_NODES) return {};
  if (type.aliasSymbol && isSdk(type.aliasSymbol)) return {};
  const f = type.flags;
  if (f & (ts.TypeFlags.Any | ts.TypeFlags.Unknown | ts.TypeFlags.Never)) return {};
  if (f & ts.TypeFlags.StringLiteral) return { type: "string", enum: [type.value] };
  if (f & ts.TypeFlags.NumberLiteral) return { type: "number", enum: [type.value] };
  if (f & ts.TypeFlags.BooleanLiteral) return { type: "boolean", enum: [type.intrinsicName === "true"] };
  if (f & ts.TypeFlags.String) return { type: "string" };
  if (f & ts.TypeFlags.Number) return { type: "number" };
  if (f & ts.TypeFlags.Boolean) return { type: "boolean" };
  if (f & ts.TypeFlags.Null) return { type: "null" };
  if (f & (ts.TypeFlags.Undefined | ts.TypeFlags.Void)) return { type: "null" };
  if (type.isUnion()) return unionOf(type.types, depth, seen);
  if (checker.isArrayType(type) || checker.isTupleType(type)) {
    const [item] = checker.getTypeArguments(type);
    return { type: "array", items: checker.isTupleType(type) || !item ? {} : schemaOf(item, depth + 1, seen) };
  }
  if (!(f & ts.TypeFlags.Object) && !type.isIntersection()) return {};
  if (type.getCallSignatures().length > 0) return {};
  const name = type.getSymbol()?.getName();
  if (name === "Date") return { type: "string", format: "date-time" };
  if (isSdk(type.getSymbol())) return {};

  const inner = new Set(seen).add(type);
  const schema = { type: "object", properties: {} };
  const required = [];
  for (const property of checker.getPropertiesOfType(type).slice(0, MAX_PROPERTIES)) {
    const declaration = property.valueDeclaration ?? property.declarations?.[0];
    const propertyType = declaration ? checker.getTypeOfSymbolAtLocation(property, declaration) : checker.getTypeOfSymbol(property);
    if (propertyType.getCallSignatures().length > 0) continue;
    const optional = (property.flags & ts.SymbolFlags.Optional) !== 0;
    const parts = propertyType.isUnion() ? propertyType.types : [propertyType];
    const defined = parts.filter((t) => !(t.flags & ts.TypeFlags.Undefined));
    const own = defined.length === parts.length ? schemaOf(propertyType, depth + 1, inner) : unionOf(defined, depth + 1, inner);
    const doc = ts.displayPartsToString(property.getDocumentationComment(checker)).trim();
    if (doc) own.description = doc.slice(0, 500);
    schema.properties[property.getName()] = own;
    if (!optional && defined.length === parts.length) required.push(property.getName());
  }
  if (required.length > 0) schema.required = required;
  const index = checker.getIndexInfoOfType(type, ts.IndexKind.String);
  if (index) {
    schema.additionalProperties = schemaOf(index.type, depth + 1, inner);
    // A record: what it holds, not an object of no properties.
    if (Object.keys(schema.properties).length === 0) delete schema.properties;
  }
  return schema;
}

function unionOf(types, depth, seen) {
  const defined = types.filter((t) => !(t.flags & (ts.TypeFlags.Undefined | ts.TypeFlags.Void)));
  const nullable = defined.length !== types.length || defined.some((t) => t.flags & ts.TypeFlags.Null);
  const rest = defined.filter((t) => !(t.flags & ts.TypeFlags.Null));
  // true | false is how TypeScript spells boolean.
  const booleans = rest.filter((t) => t.flags & ts.TypeFlags.BooleanLiteral);
  const others = rest.filter((t) => !(t.flags & ts.TypeFlags.BooleanLiteral));
  const members = [...others.map((t) => schemaOf(t, depth, seen)), ...(booleans.length === 2 ? [{ type: "boolean" }] : booleans.map((t) => schemaOf(t, depth, seen)))];
  if (members.some((m) => Object.keys(m).length === 0)) return {};
  let schema;
  const literal = members.every((m) => m.enum && m.type === members[0].type);
  if (members.length === 1) schema = members[0];
  else if (literal) schema = { type: members[0].type, enum: members.flatMap((m) => m.enum) };
  else schema = { anyOf: members };
  if (!nullable) return schema;
  if (typeof schema.type === "string") {
    const out = { ...schema, type: [schema.type, "null"] };
    if (out.enum) out.enum = [...out.enum, null];
    return out;
  }
  return { anyOf: [...(schema.anyOf ?? [schema]), { type: "null" }] };
}

/** `export default defineFunction({...})`: FunctionSpec<I, O>, whose I and O the handler fixed. */
function describe(file) {
  const source = program.getSourceFile(file);
  const exported = source?.statements.find((s) => ts.isExportAssignment(s) && !s.isExportEquals);
  if (!exported) return null;
  const type = checker.getTypeAtLocation(exported.expression);
  const [input, output] = type.aliasTypeArguments ?? (type.flags & ts.TypeFlags.Object ? checker.getTypeArguments(type) : []);
  if (!input || !output) return null;
  const bounded = (type) => {
    nodes = 0;
    const schema = schemaOf(type, 0, new Set());
    return nodes > MAX_NODES || JSON.stringify(schema).length > MAX_CHARS ? {} : schema;
  };
  return { input: bounded(input), output: bounded(output) };
}

const result = {};
for (const [name, file] of Object.entries(files)) {
  try {
    const described = describe(file);
    if (described) result[name] = described;
  } catch {
    // A type the walk does not know: that function goes undescribed, the rest still are.
  }
}
console.log(JSON.stringify(result));
