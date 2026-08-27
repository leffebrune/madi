# Madi architecture

Source code is split into nine projects. Folders and namespaces separate responsibilities inside each project without creating additional assemblies.

## Projects

- `Madi`: the compatibility console executable and `IUserInterface` adapter.
- `Madi.TUI`: the Terminal.Gui full-screen executable. Its views, colors, layouts, and key bindings are defined in code for NativeAOT compatibility.
- `Madi.GUI`: the Windows 10 1809+ WinUI 3 executable. It is an unpackaged, self-contained x64 application published as a single extracting EXE.
- `Madi.GUI.Presentation`: platform-neutral GUI view models, timeline models, dispatch abstraction, and Markdown document parsing.
- `Madi.App`: shared application composition, utility installation, immutable state snapshots, structured application events, command catalog, authentication, session flow, and successful-turn persistence.
- `Madi.Core`: agent orchestration and stable contracts such as `IAgent`, `IModelProvider`, `ITool`, and `IWorkSessionStore`.
- `Madi.Networking`: provider-neutral HTTP/SSE transport, including configurable endpoints, headers, compression, error handling, and SSE framing.
- `Madi.Runtime`: concrete application integrations grouped under `Providers`, `Tools`, and `Authentication`. Providers build model requests and consume structured networking events.
- `Madi.Services`: reusable application services, including downloading and extracting files from GitHub releases.

## Dependency direction

```text
Madi ───────────┐
Madi.TUI ───────┼──> Madi.App ──> Madi.Core
Madi.GUI ───────┤       ├───────> Madi.Networking
       └────────┼──> Madi.GUI.Presentation
                │       └───────> Madi.App
                └──────> Madi.App ──> Madi.Services
                              └─────> Madi.Runtime ──> Madi.Core
                                               └────> Madi.Networking
```

`Madi.Core`, `Madi.Networking`, and `Madi.Services` have no project dependencies. `Madi.Runtime` implements Core contracts and owns provider-specific request semantics and networking configuration, filesystem/process access, OAuth, credential persistence, and Work/Session persistence. `Madi.Networking` remains provider-neutral and owns wire-level HTTP and SSE behavior. `Madi.Services` owns general-purpose integrations that are independent of agent runtime contracts. Concrete implementations are selected by the shared `Madi.App` composition factory so both executable front ends use identical runtime behavior.

At application startup, Windows x64 builds ensure `fd.exe` and `rg.exe` exist under `MADI_HOME/bin`, or under the `bin` directory in the default local application data root. Missing utilities are installed from the matching ZIP asset in each repository's latest GitHub release. Installation failures are warnings and do not prevent the requested command from running. This shared utility directory is placed first on the Bash tool's `PATH` and is preferred by the dedicated `grep` (ripgrep) and `find` (fd) tools. Agents are instructed to use those structured tools instead of Bash for content and file searches.

Subagent definitions are read from `MADI_HOME/subagents`, or the `subagents` directory under the default local application data root. If the `subagents` directory does not exist, it is created automatically along with the default subagent definitions (`reviewer.md` and `scanner.md`). Valid definitions are exposed to the main agent through a parallel-capable delegation tool. Subagent sessions are ephemeral, use only the tools allowed by their definition, and are not persisted independently.
Their model setting uses the `provider/name:effort` format and is resolved against both the provider and model name in the model catalog.

Work and Session metadata are stored independently. Session conversations use an append-only JSONL stream so Work remains a small logical grouping and unscoped sessions can use a null `WorkId`. The active session pointer is stored separately and determines the Work, if any, during restoration.

Work and Session metadata also carry a normalized working directory and UTC creation/update timestamps. Front ends only restore conversations that belong to their active working directory. Schema-v1 documents without a working directory remain readable and are upgraded to schema v2 on their next save; the GUI offers an explicit import action instead of guessing their workspace.

The GUI is published with `WindowsPackageType=None`, a self-contained Windows App SDK runtime, and .NET single-file bundling. The resulting EXE extracts its bundled native/runtime content to a temporary directory at launch; it is not a NativeAOT or zero-extraction binary.

## Namespaces

```text
Madi
Madi.UI
Madi.App
Madi.TUI
Madi.GUI
Madi.GUI.Presentation
Madi.Core
Madi.Networking
Madi.Services
Madi.Runtime.Providers.OpenAI
Madi.Runtime.Providers.OpenRouter
Madi.Runtime.Tools
Madi.Runtime.Authentication
```
