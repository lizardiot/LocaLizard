using System.Diagnostics;

namespace LocaLizard.App;

public sealed class LanguageSourcesForm : Form
{
    private readonly LanguageSourceConfiguration _configuration = LanguageSourceConfiguration.Load();
    private readonly Dictionary<string, CheckBox> _automaticChecks = new(StringComparer.OrdinalIgnoreCase);

    public int EnabledAutomaticSourceCount => _automaticChecks.Values.Count(x => x.Checked);

    public LanguageSourcesForm()
    {
        Text = "Онлайн-источники LocaLizard";
        Width = 860;
        Height = 700;
        MinimumSize = new Size(720, 560);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(14)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label
        {
            Text = "Автоматические каталоги опрашиваются независимо. Ручные и справочные площадки не используются для автоматической загрузки пакетов.",
            AutoSize = true, MaximumSize = new Size(800, 0), Padding = new Padding(0, 0, 0, 10)
        }, 0, 0);

        var content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(2)
        };
        AddAutomaticGroup(content);
        AddInformationGroup(content, "Ручной импорт", LanguageSourceCatalog.ManualSources);
        AddInformationGroup(content, "Справочные источники", LanguageSourceCatalog.ReferenceSources);
        root.Controls.Add(content, 0, 1);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true, Padding = new Padding(0, 10, 0, 0)
        };
        var save = new Button { Text = "Сохранить", Width = 112, Height = 34 };
        var cancel = new Button { Text = "Отмена", Width = 112, Height = 34, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => SaveConfiguration();
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        root.Controls.Add(actions, 0, 2);
        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;
        UiThemeManager.Apply(this, UiThemeManager.Load());
    }

    private void AddAutomaticGroup(Control parent)
    {
        var group = CreateGroup("Автоматический поиск");
        var stack = (FlowLayoutPanel)group.Controls[0];
        foreach (var source in LanguageSourceCatalog.AutomaticSources)
        {
            var row = CreateSourceRow(source, out var details);
            var check = new CheckBox
            {
                Text = source.DisplayName, AutoSize = true,
                Checked = _configuration.IsEnabled(source.SourceId), Anchor = AnchorStyles.Left
            };
            _automaticChecks.Add(source.SourceId, check);
            details.Controls.Add(check, 0, 0);
            row.Controls.Add(details);
            stack.Controls.Add(row);
        }
        parent.Controls.Add(group);
    }

    private void AddInformationGroup(Control parent, string title,
        IReadOnlyList<LanguageResourceSourceDescriptor> sources)
    {
        var group = CreateGroup(title);
        var stack = (FlowLayoutPanel)group.Controls[0];
        foreach (var source in sources)
        {
            var row = CreateSourceRow(source, out var details);
            details.Controls.Add(new Label
            {
                Text = source.DisplayName + " — только вручную/справочно",
                AutoSize = true, Font = new Font(Font, FontStyle.Bold)
            }, 0, 0);
            row.Controls.Add(details);
            stack.Controls.Add(row);
        }
        parent.Controls.Add(group);
    }

    private static GroupBox CreateGroup(string title)
    {
        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(8)
        };
        var group = new GroupBox
        {
            Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = 790, Padding = new Padding(8)
        };
        group.Controls.Add(stack);
        return group;
    }

    private Panel CreateSourceRow(LanguageResourceSourceDescriptor source, out TableLayoutPanel details)
    {
        var row = new Panel { Width = 744, Height = 68, Margin = new Padding(3), Padding = new Padding(2) };
        details = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0)
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var description = new Label
        {
            Text = source.Description, AutoSize = true, MaximumSize = new Size(620, 0),
            ForeColor = SystemColors.GrayText
        };
        details.Controls.Add(description, 0, 1);
        details.SetColumnSpan(description, 2);

        if (!string.IsNullOrWhiteSpace(source.InformationUrl) &&
            Uri.TryCreate(source.InformationUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps)
        {
            var link = new LinkLabel { Text = "Открыть", AutoSize = true, Anchor = AnchorStyles.Right };
            link.LinkClicked += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            details.Controls.Add(link, 1, 0);
        }
        return row;
    }

    private void SaveConfiguration()
    {
        try
        {
            foreach (var pair in _automaticChecks)
                _configuration.SetEnabled(pair.Key, pair.Value.Checked);
            _configuration.Save();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось сохранить настройки источников: " + ex.Message,
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
