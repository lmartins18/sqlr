# sqlr

A fast, keyboard-driven SQL Server TUI client for developers who live in the terminal.

![sqlr screenshot placeholder](docs/screenshot.png)

---

## What it solves

Most SQL GUIs are slow, heavy, and mouse-driven. `sqlr` gives you:

- **Instant startup** — single self-contained exe, no install, no splash screens
- **SQL IntelliSense as you type** — context-aware completions loaded from your live schema (tables, columns, functions, keywords), exactly like [mssql-cli](https://github.com/dbcli/mssql-cli)
- **Keyboard-first** — navigate everything without touching the mouse
- **Multi-connection management** — saved connections with Windows auth or SQL auth
- **Readable results** — dark btop-inspired theme, alternating rows, `∅` for NULLs

---

## Features

| Feature | Details |
|---|---|
| Syntax highlighting | Keywords · DDL · functions · strings · comments · numbers · `@vars` |
| IntelliSense | Tables after `FROM`/`JOIN`, columns scoped to `FROM` clause, dot-completion, fuzzy match |
| Tab switching | `Tab` moves focus between SQL editor ↔ results grid |
| Word delete | `Ctrl+Backspace` deletes word left, `Ctrl+Delete` deletes word right |
| Results grid | Full bidirectional scroll, alternating rows, `∅` for NULL |
| Multi-line editor | Paste multi-statement queries, `F5` to run |
| Connection picker | Rounded-border dark TUI, `A`/`D`/`T` for add / delete / test |
| Self-contained | Single `.exe`, no .NET runtime required on target machine |

---

## Install

### 1. Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (build only)
- SQL Server accessible from your machine

### 2. Build

```powershell
git clone https://github.com/yourname/sqlr
cd sqlr
.\publish.ps1           # → dist\win-x64\sqlr.exe
```

### 3. Put it on your PATH

```powershell
# Copy the exe somewhere permanent first, e.g.:
New-Item -ItemType Directory -Force C:\tools\sqlr
Copy-Item .\dist\win-x64\* C:\tools\sqlr\

# Then register with PATH:
C:\tools\sqlr\sqlr.exe --add-to-path
# Restart your terminal
```

### 4. Add your first connection

```
sqlr connections add
```

---

## Usage

```
sqlr                              # launch connection picker
sqlr -c <name>                    # connect directly by saved name
sqlr --add-to-path                # add install dir to user PATH

sqlr connections                  # list all saved connections
sqlr connections add              # interactive wizard
sqlr connections remove <name>    # remove a connection
sqlr connections test <name>      # test connectivity
```

---

## Keybindings

### Connection picker

| Key | Action |
|---|---|
| `↑` `↓` | Navigate |
| `Enter` | Connect |
| `A` | Add new connection |
| `D` | Delete selected |
| `T` | Test selected |
| `Q` / `Esc` | Quit |

### Query editor

| Key | Action |
|---|---|
| `F5` | Execute SQL |
| `F2` | Clear results |
| `Tab` | Switch focus: editor ↔ results |
| `↑` `↓` `←` `→` | Scroll results (rows + columns) |
| `Ctrl+Space` | Force-trigger IntelliSense popup |
| `↑` `↓` (popup) | Navigate completions |
| `Tab` / `Enter` (popup) | Accept completion |
| `Esc` (popup) | Dismiss popup |
| `Ctrl+Backspace` | Delete word left |
| `Ctrl+Delete` | Delete word right |
| `Ctrl+Q` | Disconnect / exit |

---

## IntelliSense context rules

| Cursor context | Suggestions |
|---|---|
| After `FROM` / `JOIN` | Tables, views, schema names |
| After `SELECT` / `WHERE` / `ORDER BY` | Columns scoped to tables in `FROM` clause |
| After `table.` (dot) | Columns for that table |
| After `schema.` (dot) | Tables in that schema |
| After `USE` | Databases |
| Everywhere else | T-SQL keywords + all table names |

Fuzzy matching: typing `ordr` will match `ORDER`, `orderId`, etc.

---

## Connection config

Stored in `~/.sqlr/connections.json`:

```json
{
  "connections": [
    {
      "name": "staging",
      "server": "db-server.internal",
      "database": "MyApp",
      "authType": "windows"
    },
    {
      "name": "local",
      "server": "localhost",
      "database": "MyDb",
      "authType": "sql",
      "username": "sa",
      "password": "secret"
    }
  ]
}
```

> ⚠️ **Passwords are stored in plain text.** This is a local developer tool — treat `~/.sqlr/connections.json` accordingly. Future improvement: DPAPI encryption on Windows / system keyring on Unix.

---

## Building for other platforms

```powershell
# Linux
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o ./dist/linux-x64

# macOS (Intel)
dotnet publish -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true -o ./dist/osx-x64

# macOS (Apple Silicon)
dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -o ./dist/osx-arm64
```

---

## Tech stack

- **.NET 10 / C# 13**
- **[Terminal.Gui v2](https://github.com/gui-cs/Terminal.Gui)** — TUI framework (rounded borders, TableView, ListView, TextView)
- **[Microsoft.Data.SqlClient](https://github.com/dotnet/sqlclient)** — SQL Server driver
- `INFORMATION_SCHEMA` + `sys.*` for live schema metadata

---

## License

MIT — see [LICENSE](LICENSE).
