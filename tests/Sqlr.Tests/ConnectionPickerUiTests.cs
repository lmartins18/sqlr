using Sqlr.Config;
using Sqlr.Tui;
using Terminal.Gui;
using Xunit;

namespace Sqlr.Tests;

[Collection("UI")]
public sealed class ConnectionPickerUiTests : IDisposable
{
    public ConnectionPickerUiTests() => Application.Init(new FakeDriver());

    public void Dispose() => Application.Shutdown();

    private static IEnumerable<View> All(View v) => v.Subviews.SelectMany(s => All(s).Prepend(s));

    private static string Screen()
    {
        var c = Application.Driver!.Contents!;
        return string.Join("\n", Enumerable.Range(0, c.GetLength(0))
            .Select(r => string.Concat(Enumerable.Range(0, c.GetLength(1)).Select(x => c[r, x].Rune.ToString()))));
    }

    [Fact]
    public void Add_dialog_saves_a_domain_login()
    {
        var store  = new ConnectionStore();
        var stage  = 0;
        var screen = "";
        var until  = DateTime.UtcNow.AddSeconds(10);

        void Step(object? sender, IterationEventArgs e)
        {
            if (DateTime.UtcNow > until) Application.RequestStop(Application.Top);
            var top = Application.Top;
            switch (stage)
            {
                case 0 when top is not Dialog:
                    stage = 1; // the dialog runs its own loop inside this key press
                    Application.RaiseKeyDownEvent(Key.A);
                    break;

                case 1 when top is Dialog dlg:
                    var fields = All(dlg).OfType<TextField>().ToList(); // name, server, database, username, password
                    var auth   = All(dlg).OfType<RadioGroup>().Single();
                    Assert.Equal(3, auth.RadioLabels!.Length);

                    fields[0].Text    = "mac";
                    fields[1].Text    = "sql01.corp.local";
                    auth.SelectedItem = 2;
                    Application.LayoutAndDraw(true);
                    screen = Screen();

                    fields[3].Text = @"CORP\jdoe";
                    fields[4].Text = "pw";
                    All(dlg).OfType<Button>().Single(b => b.Text == "Save").InvokeCommand(Command.Accept);
                    stage = 2;
                    break;

                case 2 when top is not Dialog:
                    Application.RequestStop(top);
                    stage = 3;
                    break;
            }
        }

        Application.Iteration += Step;
        try { ConnectionPicker.Pick(store); }
        finally { Application.Iteration -= Step; }

        Assert.Equal(3, stage);
        Assert.Contains(@"Domain (DOMAIN\user + password)", screen);
        Assert.Contains(@"DOMAIN\user", screen.Replace(@"Domain (DOMAIN\user + password)", ""));   // username hint

        var saved = Assert.Single(store.Connections);
        Assert.Equal("domain", saved.AuthType);
        Assert.Equal(@"CORP\jdoe", saved.Username);
        Assert.Equal("pw", saved.Secret);
        Assert.Equal(@"domain:CORP\jdoe", saved.AuthLabel);
    }
}
