namespace PaneWeaver;

internal sealed class LogForm : Form
{
    private readonly TextBox text;

    internal LogForm(ActivityLog log, Icon icon)
    {
        Text = "PaneWeaver activity";
        Icon = icon;
        Width = 680;
        Height = 420;
        MinimumSize = new Size(460, 280);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        text = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Cascadia Mono", 9F),
            BackColor = Color.FromArgb(250, 250, 250),
            BorderStyle = BorderStyle.None,
            Text = log.Snapshot()
        };
        Controls.Add(text);

        log.Added += OnLineAdded;
        FormClosed += (_, _) => log.Added -= OnLineAdded;
    }

    private void OnLineAdded(string line)
    {
        if (IsDisposed)
        {
            return;
        }

        BeginInvoke(() =>
        {
            text.AppendText((text.TextLength == 0 ? string.Empty : Environment.NewLine) + line);
        });
    }
}
