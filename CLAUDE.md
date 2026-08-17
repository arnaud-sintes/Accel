# Accel — Claude Code Guidance

This file is the entry point for Claude Code (and any coding agent) working in this repository. Read the file below that matches what you need before making changes:

| File | Read this when you need to know... |
|---|---|
| [README.md](README.md) | What Accel does functionally, and how to install/build/run it for the first time. Start here if you're new to the project. |
| [CLAUDE_ARCHITECTURE.md](CLAUDE_ARCHITECTURE.md) | How the system is put together: process model, component responsibilities (Cli, Server, Orchestration, Settings, Metrics, Versioning, App), key data flows (hook event → UI, PTY session lifecycle), and the abstractions/design decisions behind them. Read before touching cross-component behavior. |
| [CLAUDE_DESIGN.md](CLAUDE_DESIGN.md) | Code style and conventions actually used in this codebase: C# naming/nullable/async/error-handling patterns, comment style, unit-test vs. smoke-test conventions, and the WPF/XAML design system (Theme.xaml palette, spacing grid, MVVM boundaries). Read before writing or reviewing code, so new code matches existing conventions. |
| [CLAUDE_ENV.md](CLAUDE_ENV.md) | Where things live on disk, prerequisites (pinned SDK version), and exact build/test/publish commands. Read before running a build, running tests, or navigating the folder layout for the first time. |

## Quick orientation

Accel is a native Windows C#/.NET 8 tool that monitors Claude Code local session activity: it installs itself into Claude Code's hooks, runs a local HTTP server to receive events, and shows a WPF monitor window of sessions, sub-agents, and PTY terminals — all in one process.

For anything beyond a one-line lookup, prefer the dedicated file above over guessing from file names.
