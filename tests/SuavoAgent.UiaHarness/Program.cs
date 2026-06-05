using System;
using System.Linq;
using System.Windows.Forms;

namespace SuavoAgent.UiaHarness;

/// <summary>
/// A minimal classic-Win32 (WinForms) app that stands in for a real PMS screen the agent must drive.
/// Its controls are classic UIA control types (Button) with stable AutomationIds — exactly what the
/// production FlaUiElementExtractor + UiaSignatureResolver read on a real Win32 app (PioneerRx).
///
/// Two arg-selected workflows (a sequence of buttons, each click hides itself and reveals the next —
/// a real, perceivable state transition the loop / template replay can drive and verify):
///   • (default)  saveButton → doneButton          — the 1-step loop/primitive tests.
///   • "learn"    openButton → searchButton → approveButton → doneButton — a 3-step routine the
///                watch-&-learn pipeline can observe, mine into a template, and replay.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var learn = args.Length > 0 && string.Equals(args[0], "learn", StringComparison.OrdinalIgnoreCase);
        var steps = learn
            ? new[] { ("openButton", "Open"), ("searchButton", "Search"), ("approveButton", "Approve") }
            : new[] { ("saveButton", "Save") };

        var form = new Form
        {
            Text = "SuavoAgent UIA Harness",
            Width = 460,
            Height = 260,
            StartPosition = FormStartPosition.CenterScreen,
            TopMost = true,
        };

        // Build the button sequence: each is hidden until its turn; clicking it hides it and shows the next.
        var buttons = steps.Select(s => new Button
        {
            Name = s.Item1,            // → UIA AutomationId
            Text = s.Item2,            // → UIA Name (accessible label)
            AccessibleName = s.Item2,
            Width = 200,
            Height = 48,
            Left = 120,
            Top = 70,
            Visible = false,
        }).ToList();

        var doneButton = new Button
        {
            Name = "doneButton",
            Text = "Done",
            AccessibleName = "Done",
            Width = 200,
            Height = 48,
            Left = 120,
            Top = 70,
            Visible = false,           // the completion signal (no click handler)
        };

        for (var i = 0; i < buttons.Count; i++)
        {
            var current = buttons[i];
            var next = i + 1 < buttons.Count ? buttons[i + 1] : doneButton;
            current.Click += (_, _) =>
            {
                current.Visible = false; // this step's "Button:<Name>" disappears
                next.Visible = true;     // the next step (or "Button:Done") appears
            };
            form.Controls.Add(current);
        }
        form.Controls.Add(doneButton);

        buttons[0].Visible = true; // first step is shown on launch
        Application.Run(form);
    }
}
