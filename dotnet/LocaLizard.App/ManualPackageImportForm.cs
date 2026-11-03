using System.Diagnostics;
using LocaLizard.Core;

namespace LocaLizard.App;

public sealed class ManualPackageImportForm : Form
{
    private readonly string _packageRoot;
    private readonly ComboBox sourceBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520 };
    private readonly TextBox selectedFiles = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 110, Dock = DockStyle.Fill };
    private readonly TextBox expectedDigest = new() { PlaceholderText = "Необязательно: SHA-1 (40) или SHA-256 (64)" };
    private readonly Label sourceDescription = new() { AutoSize = true, MaximumSize = new Size(760, 0) };
    private readonly Button addFiles = new() { Text = "Выбрать файлы…", Width = 140, Height = 34 };
    private readonly Button import = new() { Text = "Проверить и импортировать", Width = 220, Height = 36 };
    private readonly Button openSource = new() { Text = "Открыть источник", Width = 140, Height = 34 };
    private readonly Button whatTheHack = new() { Text = "Ссылки WhatTheHack", Width = 170, Height = 34 };
    private readonly List<string> _files = [];

    public string? ImportedMediaIso { get; private set; }
    public string? ImportedFodDirectory { get; private set; }
    public string? ImportedLxpDirectory { get; private set; }

    public ManualPackageImportForm(string packageRoot)
    {
        _packageRoot = Path.GetFullPath(packageRoot);
        Text = "Ручной импорт языковых ресурсов";
        Width = 900;
        Height = 690;
        MinimumSize = new Size(760, 590);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);

        var importSources = LanguageSourceCatalog.ManualSources
            .Concat(LanguageSourceCatalog.ReferenceSources.Where(x =>
                x.SourceId is "rg-adguard-files" or "what-the-hack"))
            .OrderBy(x => x.Priority).ToList();
        sourceBox.DataSource = importSources;
        sourceBox.DisplayMember = nameof(LanguageResourceSourceDescriptor.DisplayName);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(14)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            Text = "Выберите источник, с которого вы получили файл, затем импортируйте локальные CAB/ESD, ISO или Store LXP. Скрипты, EXE, произвольные архивы и прямые загрузки с community-зеркал не принимаются.",
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            Padding = new Padding(0, 0, 0, 10)
        };
        root.Controls.Add(intro, 0, 0);

        var sourcePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 2, RowCount = 3, AutoSize = true
        };
        sourcePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourcePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sourcePanel.Controls.Add(new Label { Text = "Источник", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        sourcePanel.Controls.Add(sourceBox, 1, 0);
        sourcePanel.Controls.Add(sourceDescription, 0, 1);
        sourcePanel.SetColumnSpan(sourceDescription, 2);
        sourcePanel.Controls.Add(openSource, 0, 2);
        sourcePanel.Controls.Add(whatTheHack, 1, 2);
        root.Controls.Add(sourcePanel, 0, 1);

        var filesGroup = new GroupBox
        {
            Text = "Локальные файлы для импорта",
            Dock = DockStyle.Fill,
            Padding = new Padding(10)
        };
        var filesLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2
        };
        filesLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        filesLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        addFiles.Anchor = AnchorStyles.Left;
        filesLayout.Controls.Add(addFiles, 0, 0);
        filesLayout.Controls.Add(selectedFiles, 0, 1);
        filesGroup.Controls.Add(filesLayout);
        root.Controls.Add(filesGroup, 0, 2);

        var digestPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2
        };
        digestPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        digestPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        digestPanel.Controls.Add(new Label { Text = "Дополнительный хеш", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        digestPanel.Controls.Add(expectedDigest, 1, 0);
        digestPanel.SetColumnSpan(new Label
        {
            Text = "Для fydexx и RG-Adguard хеш запрашивается из закреплённого каталога автоматически. Для остальных community-источников можно ввести известный SHA-1/SHA-256.",
            AutoSize = true, MaximumSize = new Size(820, 0), ForeColor = SystemColors.GrayText
        }, 2);
        digestPanel.Controls.Add(new Label
        {
            Text = "Для fydexx и RG-Adguard хеш запрашивается из закреплённого каталога автоматически. Для остальных community-источников можно ввести известный SHA-1/SHA-256.",
            AutoSize = true, MaximumSize = new Size(820, 0), ForeColor = SystemColors.GrayText
        }, 0, 1);
        digestPanel.SetColumnSpan(digestPanel.GetControlFromPosition(0, 1)!, 2);
        root.Controls.Add(digestPanel, 0, 3);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 10, 0, 0)
        };
        var cancel = new Button { Text = "Закрыть", Width = 112, Height = 36, DialogResult = DialogResult.Cancel };
        import.Click += async (_, _) => await ImportSelectedFilesAsync();
        addFiles.Click += (_, _) => SelectFiles();
        openSource.Click += (_, _) => OpenSelectedSource();
        whatTheHack.Click += async (_, _) => await ShowWhatTheHackReferencesAsync();
        actions.Controls.Add(cancel);
        actions.Controls.Add(import);
        root.Controls.Add(actions, 0, 4);

        Controls.Add(root);
        AcceptButton = import;
        CancelButton = cancel;
        sourceBox.SelectedIndexChanged += (_, _) => UpdateSourceDescription();
        UpdateSourceDescription();
        UiThemeManager.Apply(this, UiThemeManager.Load());
    }

    private void UpdateSourceDescription()
    {
        if (sourceBox.SelectedItem is not LanguageResourceSourceDescriptor source) return;
        sourceDescription.Text = $"{source.Kind}: {source.ResourceTypes}. {source.Description}";
        whatTheHack.Enabled = source.SourceId == "what-the-hack";
        openSource.Enabled = !string.IsNullOrWhiteSpace(source.InformationUrl);
        expectedDigest.Enabled = source.SourceId is not ("fydexx-rs5" or "rg-adguard-files");
        expectedDigest.PlaceholderText = source.SourceId switch
        {
            "fydexx-rs5" => "SHA-1 будет проверен автоматически по закреплённому Gist",
            "rg-adguard-files" => "SHA-1/SHA-256 будет получен из RG-Adguard",
            _ => "Необязательно: SHA-1 (40) или SHA-256 (64)"
        };
    }

    private void SelectFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Выберите CAB, ESD, ISO или Store LXP",
            Multiselect = true,
            CheckFileExists = true,
            Filter = "Поддерживаемые файлы|*.cab;*.esd;*.iso;*.appx;*.msix;*.appxbundle;*.msixbundle;License.xml|Все файлы|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        foreach (var file in dialog.FileNames)
        {
            if (!ManualPackageImportService.IsSupportedImportExtension(Path.GetFileName(file)))
                continue;
            if (!_files.Contains(file, StringComparer.OrdinalIgnoreCase)) _files.Add(file);
        }
        selectedFiles.Text = string.Join(Environment.NewLine, _files);
    }

    private void OpenSelectedSource()
    {
        if (sourceBox.SelectedItem is not LanguageResourceSourceDescriptor source ||
            !Uri.TryCreate(source.InformationUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task ShowWhatTheHackReferencesAsync()
    {
        whatTheHack.Enabled = false;
        try
        {
            using var provider = new WhatTheHackMediaReferenceProvider();
            var references = await provider.DiscoverAsync(CancellationToken.None);
            if (references.Count == 0)
                throw new InvalidDataException("В закреплённом скрипте не найдены доверенные ссылки Language ISO/FOD ISO.");
            using var dialog = new Form
            {
                Text = "WhatTheHack — исторические Microsoft ISO",
                Width = 780, Height = 270, StartPosition = FormStartPosition.CenterParent,
                AutoScaleMode = AutoScaleMode.Dpi, Font = Font
            };
            var layout = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, AutoScroll = true, Padding = new Padding(14)
            };
            foreach (var reference in references)
            {
                var link = new LinkLabel
                {
                    Text = $"{reference.DisplayName} — {reference.FileName}",
                    AutoSize = true, MaximumSize = new Size(720, 0)
                };
                var captured = reference.DownloadUri;
                link.LinkClicked += (_, _) =>
                {
                    try { Process.Start(new ProcessStartInfo(captured.AbsoluteUri) { UseShellExecute = true }); }
                    catch (Exception ex)
                    {
                        MessageBox.Show(dialog, ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };
                layout.Controls.Add(link);
                layout.Controls.Add(new Label
                {
                    Text = reference.BuildLabel + ". " + reference.Notes,
                    AutoSize = true, MaximumSize = new Size(720, 0), ForeColor = SystemColors.GrayText
                });
            }
            dialog.Controls.Add(layout);
            dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось получить ссылки WhatTheHack: " + ex.Message,
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { whatTheHack.Enabled = true; }
    }

    private async Task ImportSelectedFilesAsync()
    {
        if (sourceBox.SelectedItem is not LanguageResourceSourceDescriptor source)
            return;
        if (_files.Count == 0)
        {
            MessageBox.Show(this, "Сначала выберите локальные файлы.", AppInfo.Name,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        import.Enabled = false;
        addFiles.Enabled = false;
        try
        {
            var result = await new ManualPackageImportService().ImportAsync(
                source.SourceId, _files, _packageRoot,
                expectedDigest.Enabled ? expectedDigest.Text : null,
                CancellationToken.None,
                message => sourceDescription.Text = message);
            ImportedMediaIso = result.ImportedMediaIso;
            ImportedFodDirectory = result.FodDirectory;
            ImportedLxpDirectory = result.LxpDirectory;
            var summary = $"Добавлено файлов: {result.ImportedFiles.Count}; уже было в каталоге: {result.SkippedFiles.Count}."
                + Environment.NewLine + $"Манифест: {result.ManifestPath}";
            MessageBox.Show(this, summary, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Импорт остановлен: " + ex.Message, AppInfo.Name,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            import.Enabled = true;
            addFiles.Enabled = true;
        }
    }
}
