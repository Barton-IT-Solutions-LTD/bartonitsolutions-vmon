using System.ServiceProcess;

namespace VirtualScreenViewer;

internal static class Program
{
    private const string Name = @"Local\BartonIT.VirtualScreenViewer";

    [STAThread]
    private static void Main(string[] args)
    {
        // The same exe is the Windows service that switches the device (install.ps1 sets it up).
        if (args.Contains("--service", StringComparer.OrdinalIgnoreCase))
        {
            ServiceBase.Run(new PowerService());
            return;
        }

        // One viewer at a time: a second launch just brings the running one to the front.
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, Name + ".Show", out bool first);
        if (!first) { show.Set(); return; }

        ApplicationConfiguration.Initialize();
        var form = new ViewerForm();
        var listener = new Thread(() =>
        {
            while (show.WaitOne())
                if (form.IsHandleCreated) form.BeginInvoke(form.BringToFront_);
        }) { IsBackground = true };
        listener.Start();
        Application.Run(form);
    }
}
