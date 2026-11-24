# The dcms command line

`dcms` is your DCMS workspace from the desktop: for you, and for your own AI agents (Claude Code,
Cursor, Codex). It talks to the console it was downloaded from, as you, and never does more than
you could in the console. The design is ADR 0032.

## Install and sign in

```sh
curl -fsSL https://<your console>/downloads/install.sh | sh        # Linux, macOS
irm https://<your console>/downloads/install.ps1 | iex             # Windows (PowerShell)
dcms login --server https://<your console>
```

`dcms login` shows a code and a link. Open the link in a browser where you're signed in, check
that the code matches, and approve. `dcms version` says whether your copy matches the console's.
`dcms update` replaces it with the console's build. `dcms logout` forgets the sign-in on this machine.

## Every operation, by name

The console tells `dcms` what you may call there: sites, content, media, plugins, Dynamic Apps,
environments, functions and more, each with its parameters and its risk (Read, Safe, or Dangerous
for things that delete, publish or deploy).

```sh
dcms ops --workspace acme                          # what you may call; dcms ops sites, to filter
dcms op describe sites.commit -w acme              # its path, parameters, body schema, risk
dcms op call sites.list -w acme
dcms op call media.upload -w acme --file ./logo.png --form folderId=<id>
dcms op call plugins.dynamic-apps.records@1.Query -w acme --instance crm --body '{"table":"orders"}'
```

`--workspace` (`-w`) and `--env` choose where. In a linked or pulled folder they come from it.

## Sites and apps on your disk

```sh
dcms sites create "Spring sale" --mode react-builder --template landing --dir ./spring -w acme
dcms pull "Spring sale" --dir ./spring -w acme       # an existing site, or a Functions app by its slug
# … edit the files with anything …
dcms status --dir ./spring                           # what you changed
dcms diff --dir ./spring                             # line by line (needs git)
dcms commit -m "New hero" --dir ./spring             # push your changes, then commit
dcms publish --dir ./spring                          # merge into release: it builds and goes live
dcms builds --watch --dir ./spring
```

- Your folder is your draft of the branch on the server. `push` writes your changes into it.
  Pulling again takes others' changes in; a file you both changed gets conflict markers.
- If the branch moved, `commit` writes markers into the files changed on both sides. Fix them,
  then `dcms commit -m "…" --resolved`. Or settle every file at once with `--ours` or `--theirs`.
- Hidden files and folders (`.env`, `.git`, …) never leave your machine.

## Your own AI agent

```sh
dcms link-agent --claude-code -w acme --env staging          # in your project's folder
dcms link-agent --cursor -w acme --read-only
dcms link-agent --codex -w acme --days 30
dcms agents                                                  # your agents' tokens; which are linked here
dcms unlink-agent <id>                                       # revoke it and remove its configuration
```

`link-agent` gives the agent its own token, made with your sign-in. The token is for one
workspace, the environments you name (all of yours if none), and only reads with `--read-only`.
It lasts 90 days unless `--days` says otherwise. Then `link-agent`:
- points the agent's MCP configuration at `dcms mcp --agent <id>`;
- installs the DCMS skill: what DCMS is and how to work in it.

Restart the agent to pick them up.

What the agent can do:
- **The server holds it to its token.** Only its workspace and environments; reads only if it is
  read-only; never more than your own current permissions.
- **It never touches people or AI settings:** members, roles, invitations, ownership, AI settings
  and agent tokens. Those stay yours, in the console or with your own `dcms`.
- **It reaches a plugin's operations only on apps whose AI tools are on.**
- **It sees each tool's risk.** Your agent's own approval settings decide when it asks you first.
- **On your machine, it reaches only files in the project folder it runs in,** outside hidden folders.
- **What it does is in the audit log** as you, "via agent: <its name>". Account → Devices lists
  your agents, and revoking one there stops it at once.
- **Each token is rate-limited** (`Agents:RequestsPerMinute`, 300 by default).

## Functions apps

For a Functions repository cloned with git, `dcms link`, `sdk`, `check`, `run`, `runs`, `logs`,
`secrets` and `deploy` work as described in [functions-local-development.md](functions-local-development.md).
