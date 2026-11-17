# sqlr

A fast, keyboard-driven SQL Server TUI client for developers who live in the terminal.

![sqlr screenshot placeholder](docs/screenshot.png)

---

## What it solves

Most SQL GUIs are slow, heavy, and mouse-driven. `sqlr` gives you:

- **Instant startup** — single self-contained exe, no install, no splash screens; schema loads in the background
- **SQL IntelliSense as you type** — context-aware completions from your live schema (tables, columns, aliases, procs, variables)
- **Keyboard-first** — navigate everything without touching the mouse
- **Real script support** — `GO` batches, multiple result sets, PRINT / RAISERROR messages, rows affected, cancel with `Esc`
- **Multi-connection management** — saved connections with Windows auth, SQL auth, or domain auth (a Windows login from macOS / Linux); passwords in the macOS Keychain, DPAPI-encrypted on Windows
- **Scriptable** — `sqlr -c prod -q "select …" -f csv` for pipes and scripts

---

## Features

| Feature | Details |
|---|---|
| Syntax highlighting | Keywords · DDL · functions · strings (incl. `N'…'`) · comments (nested) · numbers · `0x` hex · `@vars` |
| IntelliSense | Tables after `FROM`/`JOIN` (schema-aware: `sales.Orders` vs `archive.Orders`), columns scoped to the query's tables, alias dot-completion, `schema.` / `sys.` / `db..`, `INSERT INTO t (…)` columns, `EXEC` procs, `USE` databases, CTE names, declared `@variables`, `[bracketed names]` |
| Run what you mean | `F5` runs the script or the selection, `F6` runs the block under the cursor; `GO` / `GO n` supported |
| Results | Multiple result sets + a Messages tab (`F7` to cycle), `∅` for NULL, right-aligned numbers, client-side sort & filter |
| Errors | Shown in the Messages tab with the **editor** line number; the cursor jumps to the failing line |
| Cell viewer | `Enter` on a cell shows the full value (JSON / XML pretty-printed) or the whole row as name: value |
| Copy & export | Copy cell / row / all (tab-separated → pastes into Excel); export to CSV, TSV, JSON, Markdown or SQL INSERTs |
| Peek | `F12` on a table / view / proc / alias: columns, types, PK/FK, defaults, indexes, row count, or the definition — `/` filters the columns or searches the definition (`n` / `N`) |
| Object explorer | `F4`: databases › schemas › objects (opens on `dbo`), vim-style `h`/`l` navigation, live structure preview, `Ctrl+F` find, filter by type, sort by name / type / modified; insert, `SELECT TOP 1000` into the editor, or peek |
| Grep | `Ctrl+G` in the explorer: plain-text search through every proc / view / function / trigger definition and column name — "where is this column used?" — opening the definition on the matching line |
| History | `F3`: every query you've run, searchable, with timing and success; reload or re-run |
| Drafts | The editor text is kept per connection — reconnect and carry on where you left off |
| Files | `Ctrl+O` / `Ctrl+S` to open and save `.sql` files, or `sqlr -c name file.sql` |
| Resilient | Lost connections are reopened automatically; long queries can be cancelled; results capped at `maxRows` without skipping later statements |
| Connection picker | Add / edit / clone / delete / test (non-blocking) connections; switch connection with `F10` |
| Themes | `F9`: 12 true-colour themes — classic, horizon, tokyodark, pastel-inu, tokyo-night, catppuccin, rose-pine, kanagawa, dracula, nord, gruvbox, synthwave — previewed live as you move, remembered between runs |
| Self-contained | Single exe, no .NET runtime required on the target machine |

---

## Install

### 1. Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (build only)
- SQL Server accessible from your machine

### 2. Build

```powershell
git clone https://github.com/yourname/sqlr
cd sqlr
.\publish.ps1           # → dist\win-x64\sqlr.exe (one file)
```

### 3. Put it on your PATH

```powershell
# Copy the exe somewhere permanent first, e.g.:
New-Item -ItemType Directory -Force C:\tools\sqlr
Copy-Item .\dist\win-x64\sqlr.exe C:\tools\sqlr\

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
sqlr                                   pick a saved connection
sqlr -c <name> [-d <db>] [file.sql]    open the editor (optionally with a file)
sqlr -c <name> -q "<sql>" [-f fmt]     run a query, print results, exit
sqlr -c <name> -i script.sql [-f fmt]  run a script file, print results, exit
echo "select 1" | sqlr -c <name>       run SQL from stdin

sqlr connections                       list saved connections
sqlr connections add                   add one (interactive)
sqlr connections edit <name>           edit one (interactive)
sqlr connections remove <name>         remove one
sqlr connections test <name>           test connectivity

sqlr --add-to-path                     add the install folder to your PATH
sqlr --help | --version
```

Output formats for `-q` / `-i`: `table` (default), `csv`, `tsv`, `json`, `md`, `sql`.
Results go to stdout; messages (PRINT, rows affected, errors) go to stderr. Exit code is `1` if any
statement failed, `2` for bad arguments.

---

## Keybindings

### Connection picker

| Key | Action |
|---|---|
| `↑` `↓` | Navigate |
| `Enter` | Connect |
| `A` / `E` / `C` | Add / edit / clone a connection |
| `D` | Delete selected |
| `T` | Test selected (result shown in the status line) |
| `F9` | Colour theme |
| `Q` / `Esc` | Quit |

### Editor

| Key | Action |
|---|---|
| `F5` / `Ctrl+R` | Run the script — or only the selection, if there is one |
| `F6` / `Ctrl+E` | Run the block under the cursor (lines between blank lines) |
| `Esc` | Cancel a running query · close the completion popup |
| `Ctrl+Space` | Show completions |
| `Tab` (popup) | Accept the highlighted completion |
| `↑` `↓` then `Enter` (popup) | Pick another completion — `Enter` on its own is always a newline |
| `Ctrl+/` or `Alt+/` | Toggle `--` comments on the line / selected lines |
| `Ctrl+Backspace` / `Ctrl+W` / `Alt+Backspace` | Delete word left |
| `Ctrl+Delete` / `Alt+D` | Delete word right |
| `Ctrl+Z` / `Ctrl+Y` | Undo / redo |
| `Ctrl+S` / `Ctrl+O` | Save / open a `.sql` file |
| `Tab` | Switch to the results |

### Results

| Key | Action |
|---|---|
| Arrows, `PgUp` `PgDn`, `Home` `End` | Move around |
| `Enter` / double-click | View the full cell (`Tab` inside: whole row; `n` / `p`: next / previous row) |
| `c` / `r` / `a` | Copy cell / row / all rows (tab-separated) |
| `/` | Filter rows (`Esc` clears the filter) |
| `s` | Sort by the current column (asc → desc → off) |
| `e` | Export (`.csv` `.tsv` `.json` `.md` `.sql` by extension) |
| `F7` / `Shift+F7` | Next / previous result set or Messages |
| `Enter` on an error (Messages) | Jump to that line in the editor |
| `Esc` / `Tab` | Back to the editor |
| Right-click | Copy the cell |

### Object explorer (`F4`)

Databases › schemas › objects; it opens on the current database's `dbo`. The right-hand pane previews the selection: a table's columns, keys and indexes, a view's or routine's definition, or — for a grep hit — the definition scrolled to the matching line.

| Key | Action |
|---|---|
| `←` / `h` / `Backspace` | Up a level (objects → schemas → databases) |
| `→` / `l` / `Enter` | Down a level; on an object, its `F12` view |
| `Ctrl+F` or `/` | Find: filter the list as you type (`Enter` keeps it, `Esc` clears it) |
| `Ctrl+G` | Grep: definition lines and column names containing the text (smart case; `Enter` opens the definition on that line) |
| `t` | Filter by type: all → tables → views → functions → procs |
| `s` | Sort: name ↑ / ↓ → type ↑ / ↓ → modified ↓ / ↑ |
| `i` | Insert the name into the editor |
| `r` | Table / view: write `SELECT TOP (1000) * FROM …` below the editor text and run it; back in the editor (`Esc`) the cursor sits at its end, ready for a `WHERE` and `F6` |
| `F5` | Table / view: `SELECT TOP (100)` into the results, leaving the editor alone |
| `p` | Hide / show the preview pane |
| `Ctrl+D` / `Ctrl+U` | Scroll the preview down / up |

### F12 view

| Key | Action |
|---|---|
| `/` or `Ctrl+F` | Columns: filter the rows · definition: search, highlighting every match |
| `n` / `N` | Next / previous match |
| `Tab` | Columns ⇄ definition |
| `c` | Copy (the visible rows, or the definition / selection) |
| `Esc` | Clear the search, then close |

### Anywhere

| Key | Action |
|---|---|
| `F1` | All keys |
| `F2` | Clear results |
| `F3` | Query history |
| `F4` | Object explorer |
| `F8` | Resize panes |
| `F9` | Colour theme — `↑` `↓` preview, `Enter` keep, `Esc` back to the previous one |
| `F10` | Switch connection |
| `F12` | Peek the table / view / procedure (or alias) under the cursor |
| `Ctrl+Q` | Quit (the editor text is kept as a draft) |

---

## IntelliSense context rules

| Cursor context | Suggestions |
|---|---|
| After `FROM` / `JOIN` / `UPDATE` / `INTO` | Tables, views, CTEs, schemas (non-`dbo` names are inserted schema-qualified) |
| After a complete table reference (`FROM t x ▌`) | `WHERE`, `JOIN`, `ON`, `GROUP BY`, … |
| After `SELECT` / `WHERE` / `ON` / `ORDER BY` / … | Columns of the tables in the query, aliases, functions, clause keywords |
| After `alias.` or `table.` | That table's columns (even after you start typing: `o.To▌`) |
| After `schema.` / `sys.` | Objects in that schema |
| Inside `INSERT INTO t (…)` | Columns of `t` |
| After `EXEC` | Procedures and functions |
| After `USE` | Databases |
| Typing `@` | Variables used in the script |
| Inside a string or comment | Nothing |

Matching is prefix-first, then substring, then fuzzy (`ordr` matches `ORDER`). Names with spaces or
reserved words are inserted `[bracketed]`. Typing `db..` expands to `db.dbo.`.

---

## Config

Everything lives in `~/.sqlr` (override with the `SQLR_HOME` environment variable):

| File | Contents |
|---|---|
| `connections.json` | Saved connections |
| `settings.json` | Optional settings (below) |
| `history.jsonl` | Query history |
| `drafts/<connection>.sql` | Editor text per connection |
| `theme` | The theme picked with `F9` |
| `error.log` | Details of any internal error |

```json
{
  "connections": [
    { "name": "staging", "server": "db-server.internal", "database": "MyApp", "authType": "windows" },
    { "name": "local",   "server": "localhost,1433",     "database": "",      "authType": "sql", "username": "sa" },
    { "name": "mac",     "server": "db.corp.local",      "database": "MyApp", "authType": "domain", "username": "CORP\\jdoe" }
  ]
}
```

An empty `database` uses the login's default database. Passwords are entered through
`sqlr connections add` or the picker, and where they're kept depends on the OS:

| OS | Stored in |
|---|---|
| macOS | the login **Keychain**: one "application password" per connection, service `sqlr`, account = the connection name. See it in Keychain Access, or `security find-generic-password -s sqlr -a staging -w` |
| Windows | `connections.json`, DPAPI-encrypted (`protectedPassword`, readable only by your Windows account) |
| Linux | `connections.json` in plain text, in a file only you can read (`0600`) |

Plain-text passwords from older configs move to the Keychain / DPAPI the next time sqlr starts.
Renaming or deleting a connection moves or deletes its Keychain item.

On macOS, sqlr reads a password the first time it connects. After you update sqlr, macOS asks
once per password whether the new build may use it (the binary is ad-hoc signed, so each build
counts as a new app). Choose **Always Allow**. Over SSH the Keychain is locked: run
`security unlock-keychain` first, or set `SQLR_NO_KEYCHAIN=1` to keep passwords in the `0600` file
instead.

### Authentication

| `authType` | Logs in as | Use it |
|---|---|---|
| `windows` | the Windows account you're signed in with | on a domain-joined Windows machine. On macOS / Linux it only works with a Kerberos ticket (`kinit you@CORP.LOCAL`) |
| `sql` | a SQL Server login (`username` + password) | when the server has SQL logins |
| `domain` | a Windows domain account: `CORP\jdoe` or `jdoe@corp.local` + its password, over NTLM | from a Mac or Linux box, like DataGrip's "domain credentials". Needs nothing installed or joined |

`domain` uses SqlClient's custom SSPI hook with .NET's built-in NTLM, so it also works on Windows
(handy for logging in as another domain account). The server must allow NTLM, which domain SQL
Servers do unless an admin turned it off.

`settings.json` (all optional):

```json
{
  "maxRows": 100000,           // rows kept per result set (later statements still run)
  "queryTimeoutSeconds": 0,    // 0 = no timeout; cancel with Esc instead
  "maxColumnWidth": 60,        // widest a grid column is drawn (Enter shows the full value)
  "historySize": 2000
}
```

---

## Building for other platforms

```powershell
.\publish.ps1 -Runtime linux-x64
.\publish.ps1 -Runtime osx-x64
.\publish.ps1 -Runtime osx-arm64     # Apple Silicon
```

or directly: `dotnet publish -c Release -r <rid> --self-contained true -p:PublishSingleFile=true -o ./dist/<rid>`

---

## Development

```bash
dotnet build
dotnet test tests/Sqlr.Tests
```

The unit and UI tests (the UI is driven through Terminal.Gui's `FakeDriver`) need nothing else.
Integration tests run when `SQLR_TEST_SQL` points at a SQL Server you can create databases on —
they build their own `SqlrTest` / `SqlrOther` databases from `tests/Sqlr.Tests/Integration/setup.sql`:

```bash
docker run -d --name sqlr-test-mssql -e ACCEPT_EULA=Y -e 'MSSQL_SA_PASSWORD=Sqlr_Test_Pw1!' \
  -p 14333:1433 mcr.microsoft.com/mssql/server:2022-latest
SQLR_TEST_SQL='Server=localhost,14333;User Id=sa;Password=Sqlr_Test_Pw1!;TrustServerCertificate=true' \
  dotnet test tests/Sqlr.Tests
```

---

## Tech stack

- **.NET 10 / C# 14**
- **[Terminal.Gui v2](https://github.com/gui-cs/Terminal.Gui)** — TUI framework (rounded borders, TableView, ListView, TextView)
- **[Microsoft.Data.SqlClient](https://github.com/dotnet/sqlclient)** — SQL Server driver
- `sys.*` catalog views for live schema metadata
- xUnit for tests

---

## License

MIT — see [LICENSE](LICENSE).
