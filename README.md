# GX Works3 MCP Bridge

> A [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server that lets AI assistants such as Claude read, write, and compile **Structured Text (ST)** code inside Mitsubishi Electric's **GX Works3** PLC IDE — driving the IDE through Windows UI Automation, with a preview → confirm → verify write-safety layer.

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![Language](https://img.shields.io/badge/language-C%23-239120)
![Platform](https://img.shields.io/badge/platform-Windows-0078D6)
![MCP](https://img.shields.io/badge/protocol-MCP%20stdio-000000)
![License](https://img.shields.io/badge/license-MIT-green)

## Table of contents

- [Why this exists](#why-this-exists)
- [How it works](#how-it-works)
- [Features](#features)
- [Available MCP tools](#available-mcp-tools)
- [Write-safety flow](#write-safety-flow)
- [Requirements](#requirements)
- [Build](#build)
- [Register with an MCP client](#register-with-an-mcp-client)
- [Usage example](#usage-example)
- [Project structure](#project-structure)
- [Troubleshooting](#troubleshooting)
- [Disclaimer](#disclaimer)
- [License](#license)

## Why this exists

GX Works3 has no public scripting API for its ST (Structured Text) editor. Engineers who want to
automate repetitive work — generating code, running the build, reading errors, and fixing them in a
loop — are stuck doing it by hand.

This server exposes GX Works3 as a set of MCP **tools** an AI assistant can call. The assistant can
generate ST code, write it into a POU, trigger *Rebuild All*, read the resulting compile
errors/warnings as structured data, and iterate — a full **generate → compile → fix** loop, without
a human clicking through the IDE.

## How it works

The ST editor does **not** expose a UI Automation `TextPattern` or a usable `ValuePattern`, so code
cannot be set directly through the accessibility tree. The bridge works around this:

- **Writing ST** — focuses the target POU window, then `Ctrl+A` → `Delete` → clipboard paste
  (`Ctrl+V`). This is the only reliable injection path.
- **Reading ST** — `Ctrl+A` → `Ctrl+C`, then reads the Windows clipboard.
- **Compile errors** — the Output/error panel *does* expose a `GridPattern`, so build errors and
  warnings are read directly and structurally (no clipboard hack there).
- **POU navigation** — each Program Organization Unit opens as its own sub-window in the UIA tree;
  the bridge walks the tree to find and focus the correct POU editor before acting.
- **Attach** — on the first tool call the server finds the running GX Works3 process and attaches
  to it (lazy attach).

Because clipboard paste can silently truncate on large inserts, every write is **read back and
hash-verified** after pasting, so a partial write is reported as an error instead of passing
silently.

## Features

- Read the full ST code of any open POU.
- Write ST code into a POU (full replace) with post-write read-back verification.
- One-shot **write → Rebuild All → return compile errors** in a single tool call.
- Structured compile error/warning extraction from the Output grid.
- **Preview → confirm → apply** write safety with a single-use `safetyToken` and a state hash that
  aborts the write if the POU changed since preview.
- Thread-safe: every tool call is serialized behind a single lock.
- stdio transport — works with Claude Code, Claude Desktop, and any MCP-compatible client.

## Available MCP tools

| Tool | What it does |
| --- | --- |
| `attach_and_list_pous` | Attaches to the running GX Works3 and lists every open window/POU title. Call this first to discover exact POU names. |
| `read_st_code` | Returns the full current ST code of the named POU. |
| `preview_write_st_code` | Dry-run of a write: reads the current code, shows a diff, and returns `currentStateHash` + a single-use `safetyToken`. Nothing is written. |
| `write_st_code` | Writes ST code into a POU (full replace). Requires `confirm=true` + the `safetyToken` from a matching preview. Reads back and verifies after writing. |
| `preview_write_compile_and_get_errors` | Same preview as above, but for the write-then-compile tool. |
| `write_compile_and_get_errors` | Writes ST code → runs *Rebuild All* → returns compile errors/warnings as JSON. Requires `confirm=true` + `safetyToken`. |
| `compile_and_get_errors` | Runs *Rebuild All* and returns all compile errors/warnings as JSON, without writing anything. |

## Write-safety flow

Destructive writes are two-step, modeled on the same idea as a database migration preview:

1. Call a `preview_*` tool. It returns a diff, a `currentStateHash`, and a single-use
   `safetyToken` (valid for 10 minutes).
2. Review the diff, then call the matching write tool with `confirm=true` and that `safetyToken`.

The write is rejected if:

- `confirm` is not `true`, or
- the `safetyToken` is missing, already used, or expired, or
- the POU changed since the preview (state-hash drift), or
- the token was issued for a different POU or a different tool type.

After a successful paste the new code is read back and hash-checked, so a truncated/partial write
fails loudly instead of silently.

## Requirements

- **Windows** (10/11) — UI Automation requires a visible, unlocked interactive desktop.
- **.NET 10 SDK** (target framework `net10.0-windows`).
- **GX Works3** installed and **running**, with the target project and POU open.
- An MCP client (Claude Code, Claude Desktop, or similar).

## Build

```bash
git clone https://github.com/<your-username>/gxworks3-mcp-bridge.git
cd gxworks3-mcp-bridge

dotnet restore
dotnet build -c Release
```

The server executable is produced at:

```
bin/Release/net10.0-windows/GXWorks3Bridge.exe
```

## Register with an MCP client

### Claude Code (CLI)

```bash
claude mcp add gxworks3 "<absolute-path>\bin\Release\net10.0-windows\GXWorks3Bridge.exe"
```

Use `--scope user` to make it available in every project, or `--scope project` to create a shared
`.mcp.json` in the repo. Verify with `claude mcp list`.

### Claude Desktop

Edit `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "gxworks3": {
      "command": "<absolute-path>\\bin\\Release\\net10.0-windows\\GXWorks3Bridge.exe",
      "args": [],
      "env": {}
    }
  }
}
```

Use double backslashes in JSON paths, then fully quit and reopen Claude Desktop.

## Usage example

A typical assistant-driven session:

1. `attach_and_list_pous` → discover that the target POU is `ProgPou`.
2. `read_st_code(pouName: "ProgPou")` → read the current logic.
3. `preview_write_st_code(pouName: "ProgPou", stCode: "...")` → review the diff, capture the token.
4. `write_st_code(pouName: "ProgPou", stCode: "...", confirm: true, safetyToken: "...")` → write + verify.
5. `compile_and_get_errors()` → read any build errors and fix them in the next iteration.

## Project structure

```
.
├── Program.cs                 # MCP host, GX Works3 controller, tool definitions, write-safety
├── GXWorks3Bridge.csproj      # net10.0-windows, WinForms (clipboard), FlaUI, MCP SDK
├── LICENSE
├── README.md
└── .gitignore
```

## Troubleshooting

- **Server attaches but no POU found** — make sure the POU editor window is actually open in GX
  Works3; the bridge only sees open sub-windows.
- **Write reported as truncated** — the read-back hash didn't match. Retry; very large inserts are
  the usual cause.
- **Nothing happens / focus issues** — UI Automation needs a visible, unlocked desktop. It won't
  work over a locked screen or a minimized-to-tray session.
- **Two clients at once** — don't drive the same GX Works3 from Claude Code and Claude Desktop
  simultaneously; UI automation conflicts. Use one client at a time.

## Disclaimer

This project interacts with GX Works3 through its user interface, not through any officially
documented Mitsubishi Electric API. It is **not affiliated with or endorsed by Mitsubishi
Electric**. Automated edits change real PLC project files — always keep backups, and never point it
at hardware in production without understanding what the generated logic does.

## License

[MIT](LICENSE)
