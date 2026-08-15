# Madi architecture

Source code is split into four projects. Folders and namespaces separate responsibilities inside each project without creating additional assemblies.

## Projects

- `Madi`: executable composition root, application flow, and console UI.
- `Madi.Core`: agent orchestration and stable contracts such as `IAgent`, `IModelProvider`, `ITool`, and `IWorkSessionStore`.
- `Madi.Networking`: provider-neutral HTTP/SSE transport, including configurable endpoints, headers, compression, error handling, and SSE framing.
- `Madi.Runtime`: concrete application integrations grouped under `Providers`, `Tools`, and `Authentication`. Providers build model requests and consume structured networking events.

## Dependency direction

```text
Madi ───────────────> Madi.Core
  ├──> Madi.Networking
  └──> Madi.Runtime ───> Madi.Core
              └───────> Madi.Networking
```

`Madi.Core` and `Madi.Networking` have no project dependencies. `Madi.Runtime` implements Core contracts and owns provider-specific request semantics and networking configuration, filesystem/process access, OAuth, credential persistence, and Work/Session persistence. `Madi.Networking` remains provider-neutral and owns wire-level HTTP and SSE behavior. Concrete implementations are selected only in the `Madi` composition root.

Work and Session metadata are stored independently. Session conversations use an append-only JSONL stream so Work remains a small logical grouping and unscoped sessions can use a null `WorkId`. The active session pointer is stored separately and determines the Work, if any, during restoration.

## Namespaces

```text
Madi
Madi.UI
Madi.Core
Madi.Networking
Madi.Runtime.Providers.OpenAI
Madi.Runtime.Providers.OpenRouter
Madi.Runtime.Tools
Madi.Runtime.Authentication
```
