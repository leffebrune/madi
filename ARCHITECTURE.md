# Madi architecture

Source code is split into seven projects. Folders and namespaces separate responsibilities inside each project without creating additional assemblies.

## Projects

- `Madi`: the compatibility console executable and `IUserInterface` adapter.
- `Madi.TUI`: the Terminal.Gui full-screen executable. Its views, colors, layouts, and key bindings are defined in code for NativeAOT compatibility.
- `Madi.App`: shared application composition, utility installation, immutable state snapshots, structured application events, command catalog, authentication, session flow, and successful-turn persistence.
- `Madi.Core`: agent orchestration and stable contracts such as `IAgent`, `IModelProvider`, `ITool`, and `IWorkSessionStore`.
- `Madi.Networking`: provider-neutral HTTP/SSE transport, including configurable endpoints, headers, compression, error handling, and SSE framing.
- `Madi.Runtime`: concrete application integrations grouped under `Providers`, `Tools`, and `Authentication`. Providers build model requests and consume structured networking events.
- `Madi.Services`: reusable application services, including downloading and extracting files from GitHub releases.

## Dependency direction

```text
Madi ─────┐
          ├──> Madi.App ──> Madi.Core
Madi.TUI ─┘       ├───────> Madi.Networking
                  ├───────> Madi.Services
                  └───────> Madi.Runtime ──> Madi.Core
                                      └────> Madi.Networking
```

`Madi.Core`, `Madi.Networking`, and `Madi.Services` have no project dependencies. `Madi.Runtime` implements Core contracts and owns provider-specific request semantics and networking configuration, filesystem/process access, OAuth, credential persistence, and Work/Session persistence. `Madi.Networking` remains provider-neutral and owns wire-level HTTP and SSE behavior. `Madi.Services` owns general-purpose integrations that are independent of agent runtime contracts. Concrete implementations are selected by the shared `Madi.App` composition factory so both executable front ends use identical runtime behavior.

At application startup, Windows x64 builds ensure `fd.exe` and `rg.exe` exist under `MADI_HOME/bin`, or under the `bin` directory in the default local application data root. Missing utilities are installed from the matching ZIP asset in each repository's latest GitHub release. Installation failures are warnings and do not prevent the requested command from running. This shared utility directory is placed first on the Bash tool's `PATH`.

Subagent definitions are read from `MADI_HOME/subagents`, or the `subagents` directory under the default local application data root. The directory is optional and read-only from Madi's perspective: no directory or definition files are created automatically. Valid definitions are exposed to the main agent through a parallel-capable delegation tool. Subagent sessions are ephemeral, use only the tools allowed by their definition, and are not persisted independently.
Their model setting uses the `provider/name:effort` format and is resolved against both the provider and model name in the model catalog.

Work and Session metadata are stored independently. Session conversations use an append-only JSONL stream so Work remains a small logical grouping and unscoped sessions can use a null `WorkId`. The active session pointer is stored separately and determines the Work, if any, during restoration.

## Namespaces

```text
Madi
Madi.UI
Madi.App
Madi.TUI
Madi.Core
Madi.Networking
Madi.Services
Madi.Runtime.Providers.OpenAI
Madi.Runtime.Providers.OpenRouter
Madi.Runtime.Tools
Madi.Runtime.Authentication
```
