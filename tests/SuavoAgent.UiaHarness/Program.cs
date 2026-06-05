using System;
using System.Drawing;
using System.Windows.Forms;

namespace SuavoAgent.UiaHarness;

/// <summary>
/// A minimal classic-Win32 (WinForms) app that stands in for a real PMS screen the agent must drive.
/// Its controls are classic UIA control types (Button, Edit, Text) with stable AutomationIds — exactly
/// what the production FlaUiElementExtractor + UiaSignatureResolver read on a real Win32 app (PioneerRx).
///
/// Workflow: a "Save" button (AutomationId=saveButton, Name=Save) is shown. Clicking it hides the button
/// and reveals a "Done" button (AutomationId=doneButton, Name=Done) — a real, perceivable state transition
/// the agentic loop / template replay can drive to completion and verify. (Both are ControlType.Button, an
/// InterestingType the production FlaUiElementExtractor perceives; a Label/Text is filtered out.)
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var form = new Form
        {
            Text = "SuavoAgent UIA Harness",
            Width = 420,
            Height = 240,
            StartPosition = FormStartPosition.CenterScreen,
            TopMost = true,
        };

        var saveButton = new Button
        {
            Name = "saveButton",        // → UIA AutomationId
            Text = "Save",              // → UIA Name (accessible label)
            AccessibleName = "Save",
            Width = 160,
            Height = 48,
            Left = 130,
            Top = 60,
        };

        var doneButton = new Button
        {
            Name = "doneButton",        // → UIA AutomationId
            Text = "Done",              // → UIA Name
            AccessibleName = "Done",
            Width = 200,
            Height = 48,
            Left = 110,
            Top = 120,
            Visible = false,            // appears only after the Save click — the completion signal (no click handler)
        };

        saveButton.Click += (_, _) =>
        {
            saveButton.Visible = false; // "Button:Save" disappears
            doneButton.Visible = true;  // "Button:Done" appears
        };

        form.Controls.Add(saveButton);
        form.Controls.Add(doneButton);
        Application.Run(form);
    }
}
