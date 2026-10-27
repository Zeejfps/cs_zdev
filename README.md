# Zdev

The libraries GitBench and Zultree share: git, terminals and coding agents, with no UI. They were
extracted from GitBench with their history, and are consumed as a git submodule, the same way both
apps consume the ZGF framework.

```
git submodule add <url> zdev
```

## Projects

| | |
|---|---|
| `Zdev.Common` | Dependency-free helpers the others share: outcomes, path keys, directory deletion, the login shell's environment, shell quoting |
| `Zdev.Git` | Git through `git.exe`: repositories, worktrees, branches, status, diffs, history, submodules |
| `Zdev.Pty` | Cross-platform PTY: ConPTY on Windows (with the published OpenConsole host), forkpty on Unix |
| `Zdev.Terminal.Vt` | VT engine behind a terminal view, built on the vendored XtermSharp fork |
| `Zdev.Agents` | Coding agents through their native CLIs: the Agent Client Protocol client, agent presets, and the command line a terminal agent starts with |

`Zdev.Git` and `Zdev.Agents` reference only `Zdev.Common`. `Zdev.Pty` and `Zdev.Terminal.Vt`
reference nothing here.

## Vendored code

`vendor/XtermSharp/` is a fork of upstream XtermSharp (MIT, see its `LICENSE`), patched in place.
Every divergence from the original clone is listed in
[`vendor/XtermSharp/PATCHES.md`](vendor/XtermSharp/PATCHES.md), and the gaps those patches close
are in [`docs/xtermsharp-known-gaps.md`](docs/xtermsharp-known-gaps.md).
`vendor/XtermSharp.Vendored/` is the project that builds it, and has to stay `netstandard2.0`; its
csproj explains why.

## Building

```
dotnet build Zdev.slnx
dotnet test Zdev.slnx
```

The git tests run the real `git` binary against temporary repositories, so git has to be on `PATH`.

Coding rules for this repo are in [`docs/coding_rules.md`](docs/coding_rules.md).
