using System.Drawing;
using Terminal.Gui;
using Xunit;

namespace Sqlr.Tests;

/// <summary>The Terminal.Gui behaviour QueryScreen.OnRunError relies on to stop a failing session.</summary>
[Collection("UI")]
public sealed class RunLoopTests
{
    private sealed class BrokenView : View
    {
        public override Point? PositionCursor() => throw new ArgumentException("broken selection");
    }

    [Fact]
    public void Run_returns_once_the_error_handler_gives_up()
    {
        Application.Init(new FakeDriver());
        try
        {
            var top = new Toplevel();
            var broken = new BrokenView { CanFocus = true, Width = 5, Height = 1 };
            top.Add(broken);
            broken.SetFocus();

            int calls = 0;
            var run = Task.Run(() => Application.Run(top, _ => ++calls < 5));

            Assert.True(run.Wait(TimeSpan.FromSeconds(10)), "Run kept going after the handler returned false");
            Assert.Equal(5, calls);
            top.Dispose();
        }
        finally
        {
            Application.Shutdown();
        }
    }
}
