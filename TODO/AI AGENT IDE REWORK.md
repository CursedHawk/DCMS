# DCMS AI dev rework 
## Main lead: 
```
Rework AI and VFS in Mode B site builder so i AI in IDE behaves more like Claude Code. 
Goal is to save tokens, AI API requests, generally faster and easier reponses, better tooling, chat history (implement it so it works nicely with current tenant and user AI history design).
Rework current autosave and conflict detection mechanics so they are better, no false possitives and play nicely with AI assitent. User has his draws stored per branch and when there is newer change saved on server it syncs to old openned tab. Or refresh solves it. 
Agent should work on same level as user so prepare WorkSpace abstraction with all the tools on top of existing VFS. 
There should be more agent modes (ask for permission, full auto, with access to full tenant config like content and plugins management, etc.). 
Add much more tooling so AI can do everything like debugging, writing code, patching code/insertion/deletion at lines, file reads with range support, look into console for errors, lookup git diffs, preview check, etc.
Dont use "GET loops" (like in builder section) and remove them whereever they appears, use SignalR for all of server state sync (AI response streaming, active builds, push to git notifications etc.)
For frontend use frontend design skill. IDE should be more "VS Code"-like.
Agent should be able to try frontend / backend operations / integrations in sandbox mode using new tools. 
Here is some inspiration to implement rework: 
```

# DCMS AI Coding Agent — Architecture & Token-Efficient Design

> **Goal:** Build an AI coding agent that feels fast, capable, and autonomous like Claude Code, while fitting naturally into the existing DCMS workspace, source-control, and draft architecture.

---

## Executive Summary

The agent should **not** work by repeatedly sending the entire project to an LLM.

Instead, treat the LLM as a **reasoning engine sitting on top of a highly capable workspace API**.

The core architecture is:

```text
Excellent tools
        +
Incremental context
        +
Persistent workspace state
        +
Patch-based editing
        +
Deterministic validation
        +
Model routing
        ↓
Fast, reliable AI coding agent
```

The most important principle is:

> **Don't optimize tokens first. Optimize the agent's ability to find and operate on exactly the information it needs.**

A single excellent agent with excellent tools will likely outperform a complicated multi-agent swarm for this product.

---

# 1. Architecture

Think of the agent as five layers:

```text
                         ┌──────────────────────┐
                         │     User prompt      │
                         └──────────┬───────────┘
                                    │
                                    ▼
                         ┌──────────────────────┐
                         │   Agent controller   │
                         │                      │
                         │ intent → plan → exec │
                         └──────────┬───────────┘
                                    │
             ┌──────────────────────┼──────────────────────┐
             ▼                      ▼                      ▼
      ┌─────────────┐       ┌──────────────┐       ┌──────────────┐
      │ Context     │       │ Tool router  │       │ Model router │
      │ manager     │       │              │       │              │
      │             │       │ read/edit/   │       │ cheap/strong │
      │ files/index │       │ search/test  │       │ model choice │
      └──────┬──────┘       └──────┬───────┘       └──────────────┘
             │                     │
             └─────────────┬───────┘
                           ▼
                  ┌──────────────────┐
                  │  Site workspace  │
                  │                  │
                  │ React source     │
                  │ package.json     │
                  │ git/draft state  │
                  │ diagnostics     │
                  └──────────────────┘
```

The **context manager** is the biggest opportunity for token savings.

---

# 2. Don't Send the Project to the Model

This is probably the single most important architectural change.

A naïve implementation looks like:

```text
User:
"Make the hero section blue"

LLM context:
  package.json
  src/App.tsx
  src/components/*
  src/pages/*
  CSS/*
  ...
```

Every request becomes expensive.

Instead, give the agent a filesystem-like tool API:

```text
list_files()
read_file(path)
read_range(path, start, end)
search(query)
find_symbol(symbol)
get_diagnostics()
get_git_diff()
apply_patch(...)
write_file(...)
run_check(...)
```

Then the model decides what it actually needs.

### Example

```text
User:
"Make the hero section blue"

Agent:

search("hero")
    ↓
src/components/Hero.tsx
src/styles/hero.css
    ↓
read_file("src/styles/hero.css")
    ↓
edit
    ↓
run targeted validation
```

The model might consume **2–5k tokens instead of 30–100k**.

This is exactly the direction to take if the goal is to reproduce the feel of Claude Code.

---

# 3. Make Search Extremely Good

The agent will live or die by its ability to locate relevant code.

Don't make the model repeatedly request entire files.

Give it something closer to:

```typescript
search({
  query: "hero",
  paths?: ["src"],
  maxResults: 20
})
```

Return compact results:

```text
src/components/Hero.tsx:12
src/components/Hero.tsx:38
src/pages/Home.tsx:21
src/styles/hero.css:4
```

Then the model chooses what to read.

### Symbol-aware search

Even better:

```text
search("hero", mode="symbol")
search("Hero", mode="references")
search("background", path="src/styles")
```

### Build a lightweight project index

When the workspace opens, build an index containing:

```text
files
imports
exports
components
routes
CSS classes
CSS variables
package dependencies
```

The agent gets structural context without reading source files.

---

# 4. Use Persistent Workspace Context

This is where DCMS can outperform a simplistic agent implementation.

Maintain server-side state such as:

```typescript
WorkspaceState {
  siteId
  branch
  revision

  files
  fileHashes

  projectGraph
  diagnostics

  recentlyReadFiles
  recentlyChangedFiles

  currentTask
  taskPlan
}
```

Critically:

> **Do not make all of this part of the LLM context.**

It is server-side state.

The model receives only the relevant projection:

```text
Workspace:
  framework: React + Vite
  package manager: pnpm
  entry: src/main.tsx
  route: /
  changed files:
    src/components/Hero.tsx
    src/styles/hero.css

Relevant symbols:
  Hero
  HomePage
```

This is dramatically cheaper than repeatedly explaining the project.

---

# 5. Use Hashes Aggressively

The existing DCMS source-control architecture has an important advantage: granular deltas and per-file conflict detection.

Exploit it.

Every file should have something like:

```json
{
  "path": "src/components/Hero.tsx",
  "revision": "...",
  "hash": "...",
  "size": 4213
}
```

The agent should be able to request:

```text
read_file(path, if_hash != cached_hash)
```

Or internally:

```text
File unchanged
      ↓
Don't send it again
```

Tool results can explicitly report:

```json
{
  "path": "src/components/Hero.tsx",
  "unchanged": true
}
```

This prevents the classic agent loop:

```text
read file
  ↓
edit
  ↓
read entire file again
  ↓
edit
  ↓
read entire file again
```

---

# 6. Make Edits Patch-Based

Do **not** make the model regenerate entire files unless necessary.

Prefer:

```typescript
apply_patch({
  path,
  expectedHash,
  patch
})
```

over:

```typescript
write_file({
  path,
  content: "entire 600-line file..."
})
```

### Benefits

* Fewer output tokens
* Fewer conflicts
* Less bandwidth
* Easier rollback
* Easier diff UI
* Safer concurrent editing
* Easier validation

Because DCMS already has per-file conflict detection, this fits the architecture particularly well.

---

# 7. Batch Agent Edits

Don't have the LLM "save" every tiny edit.

Suppose the agent changes:

```text
Hero.tsx
hero.css
Navbar.tsx
```

Don't do:

```text
LLM
 ↓
write Hero
 ↓
save
 ↓
LLM
 ↓
write CSS
 ↓
save
 ↓
LLM
 ↓
write Navbar
 ↓
save
```

Instead maintain an agent transaction:

```text
Task
 ├── edit Hero.tsx
 ├── edit hero.css
 └── edit Navbar.tsx
        ↓
     validate
        ↓
  commit draft delta
```

The IDE can still show changes immediately because the working tree is updated in memory/server-side.

Persistence can then be batched.

---

# 8. Separate Agent State from User Draft State

Use a unified workspace model:

```text
User working draft
        │
        ├── human edits
        │
        └── agent edits
                │
                ▼
          unified workspace
```

The agent should **not** create commits for every operation.

Instead:

```text
workspace revision 1837
        ↓
agent task
        ↓
workspace revision 1842
        ↓
user reviews diff
        ↓
save / commit / publish
```

This provides the Claude Code feeling while preserving the DCMS source-control model.

---

# 9. Use a Model Router

Don't use the most expensive model for everything.

Use three classes.

| Model                | Use for                                                                                                    |
| -------------------- | ---------------------------------------------------------------------------------------------------------- |
| **Fast**             | Intent classification, trivial edits, file selection, summarization, simple CSS changes, extracting errors |
| **Normal coding**    | Ordinary React changes, multi-file modifications, debugging, refactoring                                   |
| **Strong reasoning** | Architecture, difficult debugging, large refactors, repeated failures, ambiguous requirements              |

Conceptually:

```text
                  task
                   │
                   ▼
             classify difficulty
             /       |        \
          trivial   normal    complex
             │        │          │
          cheap     normal      strong
```

Don't ask an extremely expensive model:

> "Change the button text to Buy now."

---

# 10. Don't Make Every Request a Planning Request

Another common agent mistake:

```text
User:
Change button color.

Agent:
I'll first formulate a comprehensive implementation plan...
```

That's wasted latency and tokens.

Instead:

```text
simple task
    ↓
execute directly
```

Only enter planning mode when complexity crosses a threshold.

| Complexity                            | Behavior         |
| ------------------------------------- | ---------------- |
| 1 file / obvious change               | Direct execution |
| 2–4 files                             | Lightweight plan |
| Architecture / 5+ files / uncertainty | Explicit plan    |

Claude Code feels fast partly because it doesn't make every trivial task ceremonious.

---

# 11. Keep the System Prompt Tiny

Don't put the entire DCMS documentation into every request.

Use something like:

```text
You are the DCMS site coding agent.

You edit the current tenant's React site.

Rules:
- inspect before modifying
- prefer minimal patches
- never overwrite unrelated changes
- validate after meaningful changes
- don't invent APIs
- preserve existing architecture
- use available tools instead of guessing
```

Then load domain knowledge on demand.

For example:

```text
agent needs deployment information
        ↓
read_skill("deployment")
```

rather than:

```text
SYSTEM PROMPT = 25,000 tokens of DCMS documentation
```

---

# 12. Build Skills Around DCMS Concepts

DCMS is already a strongly structured platform.

Exploit that.

A possible structure:

```text
skills/
  react-site/
    structure.md
    editing.md
    validation.md

  dcms/
    source-control.md
    publish.md
    media.md
    routes.md

  frontend/
    accessibility.md
    performance.md
```

Don't concatenate all of them into the prompt.

Instead expose:

```text
available knowledge:
  react-site
  dcms-source-control
  dcms-publish
```

and load a skill only when necessary.

---

# 13. Give the Model Real Coding Tools

The agent API should look roughly like:

```text
workspace.list
workspace.read
workspace.search
workspace.symbol
workspace.diff

edit.patch
edit.create
edit.delete
edit.rename

project.diagnostics
project.test
project.build

git.status
git.diff
git.history

preview.open
preview.console
preview.screenshot
```

Notice what's missing:

```text
execute arbitrary shell command
```

Initially, avoid giving the model an unrestricted shell.

A controlled tool surface makes the agent:

* Safer
* More predictable
* Easier to cache
* Easier to audit
* Easier to meter
* Easier to optimize

A sandboxed terminal can be added later.

---

# 14. Browser Preview Is Extremely Valuable

For DCMS, this is an area where the agent can go beyond a normal coding agent.

The pipeline is:

```text
React source
      ↓
DCMS build
      ↓
hosted website
      ↓
browser preview
```

Give the agent structured browser feedback:

```text
preview.get_console_errors()
preview.get_network_errors()
preview.get_dom_snapshot(selector?)
preview.get_screenshot()
```

Then:

```text
User:
"Make the landing page look better."

Agent:
  inspect source
      ↓
  modify
      ↓
  build
      ↓
  open preview
      ↓
  inspect
      ↓
  notice overflow
      ↓
  modify
      ↓
  inspect
      ↓
  finish
```

This is much closer to a genuinely autonomous web-development agent.

### Don't send screenshots every iteration

Prefer:

```text
DOM
 ↓
console
 ↓
computed state
 ↓
screenshot only when visual judgment is necessary
```

This saves a significant amount of multimodal context.

---

# 15. Add a Context Budget

This should be implemented explicitly.

For example:

```typescript
ContextBudget {
  maxInputTokens: 40_000
  maxToolResultTokens: 8_000
  maxFilesPerRead: 5
}
```

Then the context manager decides:

```text
Need another file?
        │
        ▼
   Does it fit?
    /       \
  yes        no
   │          │
 read     summarize /
           drop old
           context
```

The desired agent behavior is:

> "I know enough. I don't need the whole repository."

Not:

> "I'll just read everything."

---

# 16. Compress Old Conversation Aggressively

Don't retain the entire raw conversation forever:

```text
user message 1
assistant reasoning 1
tool call 1
tool output 1
user message 2
assistant reasoning 2
tool call 2
...
```

Instead maintain three layers.

### Recent Context

Full.

### Working Memory

Structured:

```json
{
  "goal": "Redesign landing page hero",
  "decisions": [
    "keep existing routing",
    "use existing Button component",
    "don't add dependencies"
  ],
  "files_changed": [
    "src/components/Hero.tsx",
    "src/styles/hero.css"
  ],
  "remaining": [
    "mobile layout"
  ]
}
```

### Historical Context

Summarized.

This is enormously cheaper than replaying the entire conversation.

---

# 17. Cache Aggressively

There are multiple caching opportunities.

## Project Metadata

Cache:

```text
package.json
tsconfig
vite config
file tree
dependency graph
```

## File Reads

Cache by:

```text
path + content hash
```

## Search

Cache by:

```text
query + revision
```

## Diagnostics

Cache by:

```text
file hashes + compiler config
```

## Model Context

Where the model/provider supports prompt caching, structure requests so the stable prefix is reusable:

```text
SYSTEM
DCMS rules
TOOL definitions
workspace metadata
        ↓
----------------------------
current user request
recent changes
tool results
```

This is particularly important for providers supporting prompt/context caching.

---

# 18. Don't Call the AI Gateway for Server-Known Facts

This is a major architectural distinction.

### Bad

```text
User:
"What files changed?"

LLM:
...
```

The server already knows.

Instead:

```text
GET workspace/diff
```

Likewise, these should be zero-token operations:

```text
What branch am I on?
What is the current revision?
Is the build running?
What files changed?
What's the current route?
Is this file modified?
```

These should be ordinary DCMS operations.

> **The agent should only consume tokens where reasoning is actually required.**

---

# 19. Keep Tool Results Tiny

### Bad

```json
{
  "path": "...",
  "content": "entire 800-line file",
  "metadata": "...",
  "history": "...",
  "permissions": "...",
  "everything": "..."
}
```

### Good

```json
{
  "path": "src/components/Hero.tsx",
  "lines": "32-67",
  "content": "...",
  "hash": "abc123"
}
```

Tool APIs are effectively part of your prompt engineering.

> **Bad tools cause expensive agents.**

---

# 20. Use Line Ranges

This deserves special emphasis for an IDE.

Give the agent:

```text
read_file(path, startLine, endLine)
```

Then:

```text
search("Hero")
    ↓
Hero.tsx:42
    ↓
read 35–75
```

instead of:

```text
read 400 lines
```

You can automatically expand ranges when necessary:

```text
read 40–60

if model requests more context:
  30–70

if still needed:
  1–100
```

This is substantially more token efficient.

---

# 21. Semantic Patches Can Come Later

For React, a future optimization could support operations like:

```text
insert_import()
replace_component_prop()
replace_jsx_attribute()
rename_symbol()
add_class()
```

rather than relying entirely on textual patches.

But this doesn't need to exist on day one.

Start with:

```text
search
+
range read
+
patch
```

That's enough.

---

# 22. Use Deterministic Checks Before Another LLM Call

This is a major reliability and cost optimization.

After editing:

```text
patch
 ↓
TypeScript
 ↓
ESLint
 ↓
build
 ↓
browser console
```

Only call the model if something fails.

Don't ask:

> "Does this compile?"

when the compiler can answer for free.

Likewise, don't ask the model to inspect an import graph that your parser already knows.

---

# 23. Build a Failure Loop With a Hard Limit

The agent should follow something like:

```text
edit
 ↓
validate
 ↓
success ──────────────→ done
 ↓
failure
 ↓
can diagnose automatically?
 ├── yes → patch
 └── no  → model
             ↓
          validate
             ↓
             ...
```

Enforce:

```text
max autonomous repair attempts = 3
```

After three failures:

```text
"I couldn't safely resolve the build error.

Here's what failed:
..."
```

Otherwise you risk:

```text
LLM edits
→ breaks
→ LLM edits
→ breaks
→ LLM edits
→ makes it worse
→ burns $4
```

---

# 24. Use Speculative Execution Carefully

For independent operations, parallelize.

For example:

```text
search("Hero")
search("button")
get_diagnostics()
git_diff()
```

These don't depend on each other.

Run them concurrently.

But don't parallelize dependent operations:

```text
read
 ↓
edit
 ↓
validate
```

A simple dependency graph gives you lower latency without sacrificing correctness.

---

# 25. Streaming Matters for Perceived Speed

Even if total generation time is identical:

### Slow-feeling

```text
request
    ↓
4 seconds
    ↓
complete response
```

### Fast-feeling

```text
request
    ↓
thinking
    ↓
tool call
    ↓
"Reading Hero.tsx..."
    ↓
patch
    ↓
"Running checks..."
    ↓
done
```

For an IDE, stream **agent events**, not just LLM tokens:

```text
agent.started
agent.thinking
tool.started
tool.completed
file.changed
validation.started
validation.failed
agent.retrying
agent.completed
```

This will make the IDE feel substantially faster.

---

# 26. Ideal Request Lifecycle

The target lifecycle should look like this:

```text
USER
│
│ "Make the homepage hero more modern"
▼
CLASSIFIER
│
├── task complexity: medium
├── likely files: unknown
└── needs visual verification: yes
│
▼
PLANNER
│
└── lightweight plan
    1. inspect homepage
    2. inspect hero styles
    3. modify
    4. build
    5. visually inspect
│
▼
SEARCH
│
├── Home
├── Hero
└── relevant CSS
│
▼
READ
│
├── Hero.tsx
└── hero.css
│
▼
MODEL
│
└── produce patches
│
▼
APPLY
│
├── Hero.tsx
└── hero.css
│
▼
VALIDATE
│
├── TS
├── lint
└── build
│
▼
PREVIEW
│
└── visual check
│
▼
MODEL only if necessary
│
▼
DONE
```

This is much closer to the desired architecture than a conventional chatbot.

---

# 27. Optimization Priorities

The most important optimization is **not token optimization itself**.

Prioritize the systems in this order:

| Priority | System                   | Effect                  |
| -------: | ------------------------ | ----------------------- |
|        1 | Good search / index      | Massive token reduction |
|        2 | Incremental file reads   | Massive token reduction |
|        3 | Patch-based edits        | Cost + reliability      |
|        4 | Deterministic validation | Cost + reliability      |
|        5 | Model routing            | Cost                    |
|        6 | Context compression      | Cost                    |
|        7 | Prompt caching           | Cost + latency          |
|        8 | Parallel tools           | Latency                 |
|        9 | Streaming events         | Perceived latency       |
|       10 | Fancy multi-agent system | Usually unnecessary     |

### Strong recommendation

Resist the temptation to build a swarm of agents.

For this product:

> **One very good agent with excellent tools beats five mediocre agents arguing with each other.**

---

# 28. What to Build Into DCMS

Given the existing architecture, introduce an `AgentWorkspace` abstraction.

```typescript
interface AgentWorkspace {
  revision: number

  files: FileIndex
  graph: ProjectGraph

  search(query: SearchQuery): Promise<SearchResult[]>

  readFile(
    path: string,
    range?: LineRange
  ): Promise<FileSlice>

  applyPatch(
    path: string,
    expectedHash: string,
    patch: Patch
  ): Promise<EditResult>

  diagnostics(): Promise<Diagnostic[]>

  diff(): Promise<Diff>

  validate(
    scope?: ValidationScope
  ): Promise<ValidationResult>

  preview(): Promise<PreviewState>
}
```

Above that:

```typescript
interface AgentRuntime {
  run(task: AgentTask): AsyncIterable<AgentEvent>
}
```

The model then sees:

```text
workspace.search
workspace.read
workspace.patch
workspace.diff
workspace.validate
preview.inspect
```

It does **not** need to know about:

```text
Forgejo
HTTP
Redis
NATS
...
```

That is a much cleaner agent boundary.

---

# 29. Don't Let the AI Bypass the DCMS Workspace

The existing Mode B architecture appears to have a strong source-of-truth model:

* Per-site Git repository
* Working draft
* Per-user / per-branch state
* Granular deltas
* Conflict detection

**Do not make the AI agent bypass that model.**

That would be a mistake.

Instead:

```text
                DCMS Workspace
                     │
          ┌──────────┴──────────┐
          │                     │
       Human IDE            AI Agent
          │                     │
          └──────────┬──────────┘
                     ▼
              Working Draft
                     │
                conflict
                 detection
                     │
                   diff
                     │
                  release
```

Human and AI should be **two clients of the same workspace abstraction**.

This gives you something powerful:

> **The AI doesn't have special powers. It operates on exactly the same source state as the IDE.**

That is excellent for reliability.

---

# 30. Make the Agent Revision-Aware

Every tool call should implicitly operate against:

```text
workspaceRevision = 1842
```

Suppose the user edits `Hero.tsx` while the agent is thinking.

The agent attempts:

```typescript
applyPatch(
  "Hero.tsx",
  expectedHash = "abc"
)
```

DCMS sees:

```text
actualHash   = xyz
expectedHash = abc
```

Therefore:

```text
→ 409 conflict
```

The agent then rereads the changed region and rebases its patch.

This is exactly the kind of robustness required in a collaborative web IDE.

---

# 31. Target Production Numbers

A reasonable production target would be:

### Simple edit

```text
1 LLM call
1–3 tool calls
<5–10k input tokens
```

### Normal feature

```text
1–2 LLM calls
5–15 tool calls
<20–40k input tokens
```

### Complex feature

```text
explicit plan
2–5 LLM calls
deterministic validation between calls
```

And ideally:

### Zero LLM calls for

```text
file tree
git status
diff
revision
diagnostics
project metadata
known symbols
known routes
```

Those should all be ordinary DCMS operations.

---

# 32. The Real Key to the Claude Code Feeling

If the goal is a Claude Code-like experience, **the key isn't the prompt**.

It is this combination:

```text
Excellent tools
        +
Incremental context
        +
Persistent workspace state
        +
Patch-based editing
        +
Deterministic validation
        +
Model routing
        +
Good streaming UX
```

The LLM is only the reasoning engine sitting on top.

---

# 33. Final Recommendation

If the current implementation is essentially:

```text
chat
 ↓
send project/context
 ↓
LLM
 ↓
return files
 ↓
save
```

then yes — **that is the wrong long-term architecture** for this product.

It can work for a demo, but it will become slow and expensive as sites get larger.

The existing DCMS source-control design gives you a much better foundation.

The agent should instead be:

```text
                    ┌─────────────────┐
                    │   User request  │
                    └────────┬────────┘
                             │
                             ▼
                    ┌─────────────────┐
                    │ Agent Runtime   │
                    │                 │
                    │ classify/plan  │
                    │ reason/execute  │
                    └────────┬────────┘
                             │
              ┌──────────────┼──────────────┐
              ▼              ▼              ▼
         Context          Tools          Models
         Manager          Router         Router
              │              │              │
              └──────────────┼──────────────┘
                             ▼
                    ┌─────────────────┐
                    │ Agent Workspace │
                    │                 │
                    │ search          │
                    │ read            │
                    │ patch           │
                    │ validate        │
                    │ preview         │
                    │ diff            │
                    └────────┬────────┘
                             │
                             ▼
                    ┌─────────────────┐
                    │ Working Draft  │
                    │ + Revision     │
                    │ + Conflicts    │
                    └────────┬────────┘
                             │
                             ▼
                       User reviews
                             │
                             ▼
                         Publish
```

The strongest architectural principle is:

> **The AI should be another client of the DCMS workspace, not a parallel source-control system.**

That gives you the combination of **speed, low token usage, deterministic behavior, conflict safety, auditability, and a genuinely autonomous coding experience** without turning DCMS into an unnecessarily complicated multi-agent system.
