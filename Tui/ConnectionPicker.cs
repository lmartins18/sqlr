using System.Collections.ObjectModel;
using Sqlr.Config;
using Sqlr.Db;
using Terminal.Gui;

namespace Sqlr.Tui;

public static class ConnectionPicker
{
    public static SqlrConnection? Pick(ConnectionStore store, string? preselect = null)
    {
        SqlrConnection? result = null;

        var top = new Toplevel { ColorScheme = Theme.Base };

        // ── Header strip ───────────────────────────────────────────────────
        var header = new Label
        {
            Text        = "  ◈ sqlr  —  connection manager",
            X           = 0, Y = 0,
            Width       = Dim.Fill(),
            ColorScheme = Theme.TitleBar
        };

        // ── Connection list ────────────────────────────────────────────────
        var listFrame = new FrameView
        {
            Title       = " connections ",
            X           = 0, Y = 1,
            Width       = Dim.Fill(),
            Height      = Dim.Fill()! - 2,
            ColorScheme = Theme.Picker
        };
        Theme.SetRounded(listFrame);

        var source   = new ObservableCollection<string>();
        var listView = new KeyListView
        {
            X           = 0, Y = 0,
            Width       = Dim.Fill(),
            Height      = Dim.Fill(),
            ColorScheme = Theme.Picker,
            AllowsMarking = false
        };

        // ── Status + key hint strips ───────────────────────────────────────
        var status = new Label
        {
            Text        = "",
            X           = 0,
            Y           = Pos.AnchorEnd(2),
            Width       = Dim.Fill(),
            ColorScheme = Theme.StatusBar
        };
        var hint = new Label
        {
            Text        = "  Enter=Connect  A=Add  E=Edit  C=Clone  D=Delete  T=Test  F9=Theme  Q/Esc=Quit",
            X           = 0,
            Y           = Pos.AnchorEnd(1),
            Width       = Dim.Fill(),
            ColorScheme = Theme.StatusBar
        };

        void SetStatus(string text, ColorScheme scheme)
        {
            status.Text        = "  " + text;
            status.ColorScheme = scheme;
            status.SetNeedsDraw();
        }

        void RefreshList(string? select = null)
        {
            var prev = listView.SelectedItem;
            source.Clear();
            if (store.Connections.Count == 0)
            {
                source.Add("  (no connections — press A to add one)");
                listView.SetSource(source);
                return;
            }
            foreach (var c in store.Connections)
                source.Add($"  {c.DisplayLabel}");
            listView.SetSource(source);

            var idx = select is null ? -1 : store.Connections.FindIndex(c => c.Name.Equals(select, StringComparison.OrdinalIgnoreCase));
            listView.SelectedItem = idx >= 0 ? idx : Math.Clamp(prev, 0, store.Connections.Count - 1);
            listView.EnsureSelectedItemVisible();
        }

        SqlrConnection? Selected() =>
            store.Connections.Count > 0 && listView.SelectedItem >= 0 && listView.SelectedItem < store.Connections.Count
                ? store.Connections[listView.SelectedItem]
                : null;

        RefreshList(preselect);
        listFrame.Add(listView);
        top.Add(header, listFrame, status, hint);
        var home   = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = SqlrPaths.Connections.StartsWith(home) ? "~" + SqlrPaths.Connections[home.Length..] : SqlrPaths.Connections;
        SetStatus($"{Fmt.Plural(store.Connections.Count, "connection")} · {config}", Theme.StatusBar);

        // ── Events ────────────────────────────────────────────────────────
        listView.OpenSelectedItem += (_, _) =>
        {
            if (Selected() is not { } c) return;
            result = c;
            Application.RequestStop(top);
        };

        int testGeneration = 0;

        listView.PreviewKey = key =>
        {
            if (key.IsCtrl || key.IsAlt) return false;
            switch (key.NoShift.KeyCode)
            {
                case KeyCode.A:
                    if (ShowEditDialog(store, null, cloneOf: null) is { } added)
                    {
                        RefreshList(added);
                        SetStatus($"Saved '{added}'.", Theme.StatusOk);
                    }
                    break;

                case KeyCode.E:
                    if (Selected() is { } toEdit && ShowEditDialog(store, toEdit, cloneOf: null) is { } edited)
                    {
                        RefreshList(edited);
                        SetStatus($"Saved '{edited}'.", Theme.StatusOk);
                    }
                    break;

                case KeyCode.C:
                    if (Selected() is { } toClone && ShowEditDialog(store, null, cloneOf: toClone) is { } cloned)
                    {
                        RefreshList(cloned);
                        SetStatus($"Saved '{cloned}'.", Theme.StatusOk);
                    }
                    break;

                case KeyCode.D:
                    if (Selected() is not { } del) break;
                    if (MessageBox.Query("Delete", $"Remove '{del.Name}'?", "Yes", "No") == 0)
                    {
                        store.Remove(del.Name);
                        RefreshList();
                        SetStatus($"Removed '{del.Name}'.", Theme.StatusBar);
                    }
                    break;

                case KeyCode.T:
                {
                    if (Selected() is not { } conn) break;
                    int gen = ++testGeneration;
                    SetStatus($"Testing '{conn.Name}'…", Theme.StatusBusy);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    _ = Task.Run(async () =>
                    {
                        var (ok, err, version) = await SqlRunner.TestAsync(conn.ConnectionString);
                        Application.Invoke(() =>
                        {
                            if (gen != testGeneration) return; // a newer test superseded this one
                            if (ok) SetStatus($"✓ '{conn.Name}' connected in {sw.ElapsedMilliseconds} ms (SQL Server {version}).", Theme.StatusOk);
                            else    SetStatus($"✗ '{conn.Name}': {err.Replace('\n', ' ')}", Theme.StatusError);
                        });
                    });
                    break;
                }

                case KeyCode.F9:
                    Dialogs.PickTheme();
                    break;

                case KeyCode.Q:
                case KeyCode.Esc:
                    Application.RequestStop(top);
                    break;

                default:
                    return false; // let the ListView handle it (arrows, type-to-search…)
            }
            return true;
        };

        listView.SetFocus();
        Application.Run(top);
        top.Dispose();
        return result;
    }

    // ── Add / edit dialog ──────────────────────────────────────────────────

    /// <summary>Shows the add/edit form. Returns the saved connection's name, or null if cancelled.</summary>
    private static string? ShowEditDialog(ConnectionStore store, SqlrConnection? existing, SqlrConnection? cloneOf)
    {
        string? savedName = null;
        var src = existing ?? cloneOf;

        var dlg = new Dialog
        {
            Title       = existing is null ? " add connection " : $" edit '{existing.Name}' ",
            Width       = 70,
            Height      = 21,
            ColorScheme = Theme.Base
        };
        Theme.SetRounded(dlg);

        TextField Field(int y, string text) => new()
        {
            X = 18, Y = y, Width = 44,
            Text = text, ColorScheme = Theme.Input
        };

        var fldName     = Field(1, existing?.Name ?? (cloneOf is null ? "" : cloneOf.Name + "-copy"));
        var fldServer   = Field(3, src?.Server ?? "");
        var fldDatabase = Field(5, src?.Database ?? "");
        var rbAuth      = new RadioGroup
        {
            X            = 18, Y = 7,
            RadioLabels  = ["Windows (integrated)", "SQL (username / password)"],
            SelectedItem = src?.IsSqlAuth == true ? 1 : 0,
            ColorScheme  = Theme.Base
        };
        var fldUsername = Field(10, src?.Username ?? "");
        var fldPassword = new TextField
        {
            X = 18, Y = 12, Width = 44,
            Secret = true, Text = "", ColorScheme = Theme.Input,
            Caption = src?.IsSqlAuth == true ? "(unchanged)" : ""
        };
        var lblDbHint = new Label { Text = "empty = login's default database", X = 18, Y = 6, ColorScheme = Theme.StatusBar };
        var lblResult = new Label { Text = "", X = 1, Y = 14, Width = Dim.Fill(1), ColorScheme = Theme.StatusBar };

        void SyncAuth()
        {
            bool sql = rbAuth.SelectedItem == 1;
            fldUsername.Enabled = sql;
            fldPassword.Enabled = sql;
        }
        rbAuth.SelectedItemChanged += (_, _) => SyncAuth();
        SyncAuth();

        SqlrConnection? Build(out string? error)
        {
            error = null;
            var name     = (fldName.Text     ?? "").Trim();
            var server   = (fldServer.Text   ?? "").Trim();
            var database = (fldDatabase.Text ?? "").Trim();
            bool sql     = rbAuth.SelectedItem == 1;

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(server))
            {
                error = "Name and Server are required.";
                return null;
            }
            if (sql && string.IsNullOrWhiteSpace(fldUsername.Text))
            {
                error = "Username is required for SQL authentication.";
                return null;
            }

            var conn = new SqlrConnection
            {
                Name     = name, Server = server, Database = database,
                AuthType = sql ? "sql" : "windows",
                Username = sql ? (fldUsername.Text ?? "").Trim() : null,
                TrustServerCertificate = src?.TrustServerCertificate ?? true
            };
            if (sql)
            {
                var typed = fldPassword.Text ?? "";
                // Blank password on edit = keep the stored one
                conn.Secret = typed.Length > 0 || src is null || !src.IsSqlAuth ? typed : src.Secret;
            }
            return conn;
        }

        var btnOk = new Button { Text = "Save", IsDefault = true };
        btnOk.Accepting += (_, e) =>
        {
            e.Cancel = true;
            var conn = Build(out var error);
            if (conn is null)
            {
                MessageBox.ErrorQuery("Validation", error!, "OK");
                return;
            }
            // Adding, cloning, or renaming onto a name that's taken would replace that connection.
            bool clashes = store.Find(conn.Name) is not null
                           && (existing is null || !existing.Name.Equals(conn.Name, StringComparison.OrdinalIgnoreCase));
            if (clashes &&
                MessageBox.Query("Overwrite", $"A connection named '{conn.Name}' exists. Replace it?", "Replace", "Cancel") != 0)
                return;

            if (existing is null) store.Add(conn);
            else store.Update(existing.Name, conn);
            savedName = conn.Name;
            Application.RequestStop(dlg);
        };

        var btnTest = new Button { Text = "Test" };
        btnTest.Accepting += (_, e) =>
        {
            e.Cancel = true;
            var conn = Build(out var error);
            if (conn is null) { lblResult.Text = error!; return; }
            lblResult.Text        = "Testing…";
            lblResult.ColorScheme = Theme.StatusBusy;
            _ = Task.Run(async () =>
            {
                var (ok, err, version) = await SqlRunner.TestAsync(conn.ConnectionString);
                Application.Invoke(() =>
                {
                    lblResult.Text        = ok ? $"✓ Connected (SQL Server {version})" : $"✗ {err.Replace('\n', ' ')}";
                    lblResult.ColorScheme = ok ? Theme.StatusOk : Theme.StatusError;
                    lblResult.SetNeedsDraw();
                });
            });
        };

        var btnCancel = new Button { Text = "Cancel" };
        btnCancel.Accepting += (_, e) => { e.Cancel = true; Application.RequestStop(dlg); };

        dlg.Add(
            new Label { Text = "Name:",     X = 1, Y = 1,  ColorScheme = Theme.Base }, fldName,
            new Label { Text = "Server:",   X = 1, Y = 3,  ColorScheme = Theme.Base }, fldServer,
            new Label { Text = "Database:", X = 1, Y = 5,  ColorScheme = Theme.Base }, fldDatabase, lblDbHint,
            new Label { Text = "Auth:",     X = 1, Y = 7,  ColorScheme = Theme.Base }, rbAuth,
            new Label { Text = "Username:", X = 1, Y = 10, ColorScheme = Theme.Base }, fldUsername,
            new Label { Text = "Password:", X = 1, Y = 12, ColorScheme = Theme.Base }, fldPassword,
            lblResult
        );
        dlg.AddButton(btnOk);
        dlg.AddButton(btnTest);
        dlg.AddButton(btnCancel);

        fldName.SetFocus();
        Application.Run(dlg);
        dlg.Dispose();
        return savedName;
    }
}
