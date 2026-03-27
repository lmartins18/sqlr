using System.Collections.ObjectModel;
using Sqlr.Config;
using Sqlr.Db;
using Terminal.Gui;

namespace Sqlr.Tui;

public static class ConnectionPicker
{
    public static SqlrConnection? Pick(ConnectionStore store)
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
        var listView = new ListView
        {
            X           = 0, Y = 0,
            Width       = Dim.Fill(),
            Height      = Dim.Fill(),
            ColorScheme = Theme.Picker,
            AllowsMarking = false
        };

        void RefreshList()
        {
            source.Clear();
            if (store.Connections.Count == 0)
            {
                source.Add("  (no connections — press A to add one)");
                return;
            }
            foreach (var c in store.Connections)
                source.Add($"  {c.DisplayLabel}");
        }

        RefreshList();
        listView.SetSource(source);
        listFrame.Add(listView);
        top.Add(header, listFrame);

        // ── Key hint strip ─────────────────────────────────────────────────
        var hint = new Label
        {
            Text        = "  Enter=Connect   A=Add   D=Delete   T=Test   Q=Quit",
            X           = 0,
            Y           = Pos.AnchorEnd(1),
            Width       = Dim.Fill(),
            ColorScheme = Theme.StatusBar
        };
        top.Add(hint);

        // ── Events ────────────────────────────────────────────────────────
        listView.OpenSelectedItem += (_, _) =>
        {
            if (store.Connections.Count == 0) return;
            result = store.Connections[listView.SelectedItem];
            Application.RequestStop();
        };

        listView.KeyDown += (_, key) =>
        {
            switch (key.KeyCode)
            {
                case KeyCode.A:
                    ShowAddDialog(store);
                    RefreshList();
                    break;

                case KeyCode.D:
                    if (store.Connections.Count == 0) break;
                    var del = store.Connections[listView.SelectedItem];
                    if (MessageBox.Query("Delete", $"Remove '{del.Name}'?", "Yes", "No") == 0)
                    {
                        store.Remove(del.Name);
                        RefreshList();
                    }
                    break;

                case KeyCode.T:
                    if (store.Connections.Count == 0) break;
                    TestConnection(store.Connections[listView.SelectedItem]);
                    break;

                case KeyCode.Q:
                case KeyCode.Esc:
                    Application.RequestStop();
                    break;
            }
        };

        Application.Run(top);
        top.Dispose();
        return result;
    }

    // ── Add dialog ─────────────────────────────────────────────────────────

    private static void ShowAddDialog(ConnectionStore store)
    {
        bool saved = false;

        var dlg = new Dialog
        {
            Title       = " add connection ",
            Width       = 66,
            Height      = 20,
            ColorScheme = Theme.Base
        };
        Theme.SetRounded(dlg);

        TextField Field(int y) => new()
        {
            X = 18, Y = y, Width = 40,
            Text = "", ColorScheme = Theme.Input
        };

        var fldName     = Field(1);
        var fldServer   = Field(3);
        var fldDatabase = Field(5);
        var rbAuth      = new RadioGroup
        {
            X            = 18, Y = 7,
            RadioLabels  = ["Windows (integrated)", "SQL (username / password)"],
            ColorScheme  = Theme.Base
        };
        var fldUsername = Field(11);
        var fldPassword = new TextField
        {
            X = 18, Y = 13, Width = 40,
            Secret = true, Text = "", ColorScheme = Theme.Input
        };

        var btnOk = new Button
        {
            Text      = "  Save  ",
            X         = Pos.Center() - 10,
            Y         = 16,
            IsDefault = true,
            ColorScheme = Theme.Base
        };
        var btnCancel = new Button
        {
            Text        = "Cancel",
            X           = Pos.Center() + 2,
            Y           = 16,
            ColorScheme = Theme.Base
        };

        btnOk.Accepting += (_, _) =>
        {
            var name     = (fldName.Text     ?? "").Trim();
            var server   = (fldServer.Text   ?? "").Trim();
            var database = (fldDatabase.Text ?? "").Trim();

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(server) || string.IsNullOrEmpty(database))
            {
                MessageBox.ErrorQuery("Validation", "Name, Server and Database are required.", "OK");
                return;
            }
            store.Add(new SqlrConnection
            {
                Name     = name, Server   = server, Database = database,
                AuthType = rbAuth.SelectedItem == 1 ? "sql" : "windows",
                Username = rbAuth.SelectedItem == 1 ? (fldUsername.Text ?? "").Trim() : null,
                Password = rbAuth.SelectedItem == 1 ? fldPassword.Text : null
            });
            saved = true;
            Application.RequestStop();
        };
        btnCancel.Accepting += (_, _) => Application.RequestStop();

        dlg.Add(
            new Label { Text = "Name:",     X = 1, Y = 1,  ColorScheme = Theme.Base }, fldName,
            new Label { Text = "Server:",   X = 1, Y = 3,  ColorScheme = Theme.Base }, fldServer,
            new Label { Text = "Database:", X = 1, Y = 5,  ColorScheme = Theme.Base }, fldDatabase,
            new Label { Text = "Auth:",     X = 1, Y = 7,  ColorScheme = Theme.Base }, rbAuth,
            new Label { Text = "Username:", X = 1, Y = 11, ColorScheme = Theme.Base }, fldUsername,
            new Label { Text = "Password:", X = 1, Y = 13, ColorScheme = Theme.Base }, fldPassword,
            btnOk, btnCancel
        );

        Application.Run(dlg);
        dlg.Dispose();

        if (saved)
            MessageBox.Query("Saved", "Connection saved successfully.", "OK");
    }

    private static void TestConnection(SqlrConnection conn)
    {
        var t = SqlRunner.TestAsync(conn.ConnectionString);
        t.Wait();
        var (ok, err) = t.Result;
        if (ok) MessageBox.Query("Test OK",     $"'{conn.Name}' connected successfully.", "Close");
        else    MessageBox.ErrorQuery("Test Failed", $"'{conn.Name}'\n\n{err}", "Close");
    }
}
