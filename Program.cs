namespace VirtualScreenViewer;

internal static class Program
{
    private const string Name = @"Local\BartonIT.VirtualScreenViewer";

    [STAThread]
    private static void Main()
    {
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
