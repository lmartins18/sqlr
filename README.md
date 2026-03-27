# sqlr

A keyboard-driven SQL Server TUI client. Dark theme, pretty result tables, full bidirectional scroll.

```
┌──────────────────────────────────────────────────────────┐
│ sqlr  [staging]  fpp-coreapps-staging / FPP              │
├──────────────────────────────────────────────────────────┤
│                                                          │
│   Results  ↑↓ rows  ←→ columns                         │
│                                                          │
├──────────────────────────────────────────────────────────┤
│ SQL  (F5=Execute  F2=Clear  Ctrl+Q=Disconnect)           │
│                                                          │
│  SELECT TOP 100 * FROM ...                              │
│                                                          │
└──────────────────────────────────────────────────────────┘
│ 100 rows  42ms  |  Row 3/100  Col 2/8                    │
└──────────────────────────────────────────────────────────┘
```

---

## Install

### 1. Build

```powershell
git clone <repo>
cd sqlr
.\publish.ps1           # produces dist\win-x64\sqlr.exe
```

Requires .NET 8 SDK.

### 2. Add to PATH

```powershell
# Copy the dist folder somewhere permanent first, e.g.:
Copy-Item .\dist\win-x64 C:\tools\sqlr -Recurse

C:\tools\sqlr\sqlr.exe --add-to-path
# Restart your terminal
```

### 3. Add your first connection

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

### Connection Picker

| Key       | Action                     |
|-----------|----------------------------|
| `↑ ↓`     | Navigate connections       |
| `Enter`   | Connect to selected        |
| `A`       | Add new connection         |
| `D`       | Delete selected connection |
| `T`       | Test selected connection   |
| `Q / Esc` | Quit                       |

### Query Screen

| Key         | Action                     |
|-------------|----------------------------|
| `F5`        | Execute SQL                |
| `F2`        | Clear results              |
| `↑ ↓`       | Scroll result rows         |
| `← →`       | Scroll result columns      |
| `Ctrl+Q`    | Disconnect / exit          |
| `Tab`       | Indent in SQL editor       |

---

## Connection config

Stored in `~/.sqlr/connections.json`:

```json
{
  "connections": [
    {
      "name": "staging",
      "server": "fpp-coreapps-staging.fpp.local",
      "database": "FPP",
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

`authType` is `"windows"` (Integrated Security) or `"sql"` (username + password).

> **Note:** Passwords are stored in plain text. Future improvement: encrypt with DPAPI on Windows / system keyring on Unix.

---

## Features

- Dark navy colour theme with gold headers, alternating row shading
- `∅` displayed (dimmed) for SQL `NULL` values
- Status bar shows row count, query time, and current cell position
- Full bidirectional scroll in the results grid
- Multi-line SQL editor with Tab support
- Error dialogs show Msg/Level/State/text for SQL errors
- 60-second query timeout
- 10-second connection timeout
- `TrustServerCertificate=true` by default (internal dev tool)
# sqlr
