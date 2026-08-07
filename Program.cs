using System;
using System.Windows.Forms;

namespace HWMonitor;

static class Program
{
    [STAThread]
    static int Main()
    {
        // ── Elevation guard ───────────────────────────────────────────────────────────
        if (!IsAdmin())
        {
            MessageBox.Show(
                "[FATAL] Must run as Administrator.\n\n" +
                "WinRing0x64.sys (Ring-0 MSR driver) requires elevation.\n" +
                "The embedded app.manifest should have triggered UAC automatically.",
                "Elevation Required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());

        return 0;
    }

    // ── Admin check ───────────────────────────────────────────────────────────────
    static bool IsAdmin()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
