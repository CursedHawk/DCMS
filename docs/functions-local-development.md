# Developing functions on your own machine

A Functions app keeps its code in a git repository, one per app and environment. You can work on it in the web IDE, or clone it and use your own editor (VS Code, Rider, Visual Studio). The `dcms` command line connects the clone to the server. Decisions: [ADR 0031](adr/0031-functions-packages-and-local-development.md).

**The server is authoritative.** `dcms check` and `dcms run` send your working tree to the same build the web IDE uses. A local type check or `dotnet build` is a fast first pass, and does not replace them.

## 1. Clone

Open the app's code in the console. The clone panel (Source control) shows the HTTPS and SSH URLs, once you hold the app's *edit* or *read* permission.

To clone over HTTPS, use your email and git password. Set the password, or add an SSH key, under **Account → Git access**.

```sh
git clone https://git.example.com/tenant-acme/functions-…git orders-fn
cd orders-fn
```

You may push to `main` and to any other branch. You may not push to `release`: it is what builds, and only the platform writes to it, when someone with the *deploy* permission releases (step 6).

## 2. Install and sign in

```sh
curl -fsSL https://admin.example.com/downloads/install.sh | sh    # Linux, macOS
irm https://admin.example.com/downloads/install.ps1 | iex         # Windows (PowerShell)
dcms login --server https://admin.example.com
```

`dcms login` prints a code and a link. Open the link in a browser where you are signed in to the console, check that the code matches, and approve. The command line then acts as you, with your permissions.

- The sign-in is stored in `credentials.json` in dcms's folder (`~/.config/dcms` on Linux, `~/Library/Application Support/dcms` on macOS, `%APPDATA%\dcms` on Windows), readable only by you.
- `dcms version` says whether your copy matches the console's; `dcms update` replaces it with the console's build.
- **Account → Devices** lists every machine you signed in this way, and signs any of them out.
- `dcms logout` forgets the sign-in on this machine.

## 3. Link the clone and write the SDK

```sh
dcms link    # from the git remote: which workspace, environment and app this is
dcms sdk     # .dcms/ with the environment's SDK, and the editor config
```

`dcms sdk` writes these files:

| File | For |
| --- | --- |
| `.dcms/functions.ts` | `@dcms/functions` as this environment's build sees it: your tables, contracts and secrets, typed |
| `.dcms/Sdk.g.cs` | The same for C# |
| `deno.json` | Deno (the VS Code Deno extension): the import map, and `lib: ["esnext", "webworker"]`, which is what a function has at run time |
| `tsconfig.json` | Plain TypeScript tooling: the same `paths` and `lib` |
| `Functions.csproj`, `nuget.config`, `.dcms/packages/` | C#, only when the repository has `.cs` files. The project references **Dcms.Functions.Sdk**, the package the server makes from what it runs. It compiles against the curated BCL only, so code that does not compile on the server does not compile locally either. |

`.dcms/`, `bin/` and `obj/` are added to `.gitignore`. Run `dcms sdk` again when tables or contracts change. `Functions.csproj` and `nuget.config` are written only once, so your later changes to them are kept.

## 4. Check and run

```sh
dcms check                                     # builds the working tree; problems as file:line:column
dcms run orders/total --input '{"lines":[1,2]}'  # runs your working tree, as the IDE's Run does
dcms run orders/total --input @in.json --live   # runs what is deployed
dcms runs orders/total                          # the newest runs
dcms logs <runId>                               # one run: input, result, logs
```

A run of the working tree acts on real data, with your permissions, as the IDE's Run does.

## 5. Secrets

```sh
dcms secrets                        # names only
echo -n "sk_live_…" | dcms secrets set STRIPE_KEY
dcms secrets rm STRIPE_KEY
```

## 6. Release and deploy

Push your work to `main`, then:

```sh
dcms deploy
```

`dcms deploy` does three things:

1. It merges `main` into `release` on the server, which needs the *deploy* permission.
2. It waits for that commit's build.
3. It deploys the build: deploying vouches for what the functions use, as in the console.

If `main` does not merge cleanly into `release`, resolve the conflict in the web IDE and run it again.

The web IDE hears of your pushes as they land: anyone with the app's code open sees "main moved"
and can reload. Their uncommitted edits stay theirs. A file you both changed is resolved file by
file when they commit.

## What a function can use

The Functions view's **Runtime** page lists what a function can use, and so does `src/Plugins/Dcms.Plugins.Functions/Docs/functions-runtime.md`:

- the web globals, including `fetch` to declared hosts;
- the SDK;
- for C#, the BCL assemblies;
- the limits.
