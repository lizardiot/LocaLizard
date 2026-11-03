using System.Diagnostics;
using LocaLizard.Core;

namespace LocaLizard.App;

public sealed class ManualPackageImportForm : Form
{
    private readonly string _packageRoot;
    private readonly ComboBox sourceBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520 };
    private readonly TextBox selectedFiles = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 110, Dock = DockStyle.Fill };
    private readonly TextBox expectedDigest = new() { PlaceholderText = "SHA-1 (40) или SHA-256 (64)" };
    private readonly TextBox directUrl = new() { PlaceholderText = "Прямая HTTPS-ссылка на CAB / ESD / ISO / APPX / MSIX", Dock = DockStyle.Fill };
    private readonly TextBox torrentInput = new() { PlaceholderText = "magnet:?xt=urn:btih:… или путь к локальному .torrent", Dock = DockStyle.Fill };
    private readonly Label sourceDescription = new() { AutoSize = true, MaximumSize = new Size(760, 0) };
    private readonly Button addFiles = new() { Text = "Выбрать файлы…", Width = 140, Height = 34 };
    private readonly Button import = new() { Text = "Проверить и импортировать", Width = 220, Height = 36 };
    private readonly Button openSource = new() { Text = "Открыть источник", Width = 140, Height = 34 };
    private readonly Button whatTheHack = new() { Text = "Ссылки WhatTheHack", Width = 170, Height = 34 };
    private readonly Button downloadDirect = new() { Text = "Скачать и импортировать", Width = 190, Height = 34 };
    private readonly Button browseTorrent = new() { Text = "Выбрать .torrent…", Width = 145, Height = 34 };
    private readonly Button downloadTorrent = new() { Text = "Загрузить раздачу", Width = 160, Height = 34 };
    private readonly List<string> _files = [];

    public string? ImportedMediaIso { get; private set; }
    public string? ImportedFodDirectory { get; private set; }
    public string? ImportedLxpDirectory { get; private set; }

    public ManualPackageImportForm(string packageRoot)
    {
        _packageRoot = Path.GetFullPath(packageRoot);
        Text = "Импорт и загрузка языковых ресурсов";
        Width = 960;
        Height = 850;
        MinimumSize = new Size(780, 740);
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
            RowCount = 7,
            Padding = new Padding(14)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            Text = "Выберите источник. Можно импортировать локальные файлы, скачать прямой файл по HTTPS или получить торрент через aria2c. CAPTCHA, закрытые сессии и учётные данные не обходятся. Community-загрузка требует контрольную сумму SHA-1/SHA-256 из независимого каталога.",
            AutoSize = true,
            MaximumSize = new Size(880, 0),
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

        var directGroup = new GroupBox
        {
            Text = "Прямая загрузка по HTTPS",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10)
        };
        var directLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2
        };
        directLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        directLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        directLayout.Controls.Add(directUrl, 0, 0);
        directLayout.Controls.Add(downloadDirect, 1, 0);
        var directNote = new Label
        {
            Text = "Разрешены только прямые файлы с домена выбранного источника либо доверенного Microsoft CDN. Ссылки на страницы поиска/просмотра не скачиваются.",
            AutoSize = true, MaximumSize = new Size(860, 0), ForeColor = SystemColors.GrayText
        };
        directLayout.Controls.Add(directNote, 0, 1);
        directLayout.SetColumnSpan(directNote, 2);
        directGroup.Controls.Add(directLayout);
        root.Controls.Add(directGroup, 0, 2);

        var torrentGroup = new GroupBox
        {
            Text = "BitTorrent через aria2c",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10)
        };
        var torrentLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 2
        };
        torrentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        torrentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        torrentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        torrentLayout.Controls.Add(torrentInput, 0, 0);
        torrentLayout.Controls.Add(browseTorrent, 1, 0);
        torrentLayout.Controls.Add(downloadTorrent, 2, 0);
        var torrentNote = new Label
        {
            Text = "Нужен установленный aria2c.exe в PATH или рядом с LocaLizard.exe. Введите magnet-ссылку либо выберите .torrent, скачанный вами в браузере. По завершении можно импортировать только поддерживаемые типы файлов.",
            AutoSize = true, MaximumSize = new Size(860, 0), ForeColor = SystemColors.GrayText
        };
        torrentLayout.Controls.Add(torrentNote, 0, 1);
        torrentLayout.SetColumnSpan(torrentNote, 3);
        torrentGroup.Controls.Add(torrentLayout);
        root.Controls.Add(torrentGroup, 0, 3);

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
        root.Controls.Add(filesGroup, 0, 4);

        var digestPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2
        };
        digestPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        digestPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        digestPanel.Controls.Add(new Label { Text = "Контрольная сумма", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        digestPanel.Controls.Add(expectedDigest, 1, 0);
        var digestNote = new Label
        {
            Text = "Для community-ссылок SHA-1/SHA-256 обязателен. Для fydexx и RG-Adguard хеш берётся из каталога автоматически; WhatTheHack использует официальную ссылку Microsoft, SHA-256 вычисляется и сохраняется.",
            AutoSize = true, MaximumSize = new Size(880, 0), ForeColor = SystemColors.GrayText
        };
        digestPanel.Controls.Add(digestNote, 0, 1);
        digestPanel.SetColumnSpan(digestNote, 2);
        root.Controls.Add(digestPanel, 0, 5);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 10, 0, 0)
        };
        var cancel = new Button { Text = "Закрыть", Width = 112, Height = 36, DialogResult = DialogResult.Cancel };
        downloadDirect.Click += async (_, _) => await DownloadDirectAndImportAsync();
        downloadTorrent.Click += async (_, _) => await DownloadTorrentAsync();
        browseTorrent.Click += (_, _) => SelectTorrentFile();
        import.Click += async (_, _) => await ImportSelectedFilesAsync();
        addFiles.Click += (_, _) => SelectFiles();
        openSource.Click += (_, _) => OpenSelectedSource();
        whatTheHack.Click += async (_, _) => await ShowWhatTheHackReferencesAsync();
        actions.Controls.Add(cancel);
        actions.Controls.Add(import);
        root.Controls.Add(actions, 0, 6);

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
        var hasCatalogDigest = source.SourceId is "fydexx-rs5" or "rg-adguard-files";
        expectedDigest.Enabled = !hasCatalogDigest;
        expectedDigest.PlaceholderText = hasCatalogDigest
            ? "SHA-1/SHA-256 будет получен из закреплённого каталога"
            : source.SourceId == "what-the-hack"
                ? "Необязательно: SHA-1 (40) или SHA-256 (64)"
                : "Обязательно для прямой community-загрузки: SHA-1 или SHA-256";
        downloadDirect.Enabled = source.Kind == LanguageSourceKind.ManualImport ||
                                 source.SourceId is "what-the-hack" or "rg-adguard-files";
        downloadTorrent.Enabled = source.Kind == LanguageSourceKind.ManualImport;
        browseTorrent.Enabled = source.Kind == LanguageSourceKind.ManualImport;
        torrentInput.Enabled = source.Kind == LanguageSourceKind.ManualImport;
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

    private void SelectTorrentFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Выберите ранее скачанный .torrent",
            Multiselect = false,
            CheckFileExists = true,
            Filter = "BitTorrent metadata (*.torrent)|*.torrent"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            torrentInput.Text = dialog.FileName;
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
                Text = "WhatTheHack — Microsoft ISO",
                Width = 820, Height = 330, StartPosition = FormStartPosition.CenterParent,
                AutoScaleMode = AutoScaleMode.Dpi, Font = Font
            };
            var layout = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, AutoScroll = true, Padding = new Padding(14)
            };
            foreach (var reference in references)
            {
                var row = new FlowLayoutPanel
                {
                    AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true
                };
                var link = new LinkLabel
                {
                    Text = $"{reference.DisplayName} — {reference.FileName}",
                    AutoSize = true, MaximumSize = new Size(560, 0)
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
                var download = new Button { Text = "Скачать и импортировать", AutoSize = true };
                download.Click += async (_, _) =>
                {
                    dialog.Close();
                    await DownloadDirectAndImportAsync(captured.AbsoluteUri);
                };
                row.Controls.Add(link);
                row.Controls.Add(download);
                layout.Controls.Add(row);
                layout.Controls.Add(new Label
                {
                    Text = reference.BuildLabel + ". " + reference.Notes,
                    AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = SystemColors.GrayText
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

    private async Task DownloadDirectAndImportAsync(string? overrideUrl = null)
    {
        if (sourceBox.SelectedItem is not LanguageResourceSourceDescriptor source)
            return;
        var url = overrideUrl ?? directUrl.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(this, "Введите прямую HTTPS-ссылку на файл.", AppInfo.Name,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        downloadDirect.Enabled = false;
        whatTheHack.Enabled = false;
        import.Enabled = false;
        addFiles.Enabled = false;
        string? downloadedPath = null;
        string? downloadDirectory = null;
        try
        {
            using var downloader = new ExternalPackageDownloadService();
            var downloaded = await downloader.DownloadAsync(
                source.SourceId, url, _packageRoot,
                expectedDigest.Enabled ? expectedDigest.Text : null,
                CancellationToken.None,
                message => sourceDescription.Text = message);
            downloadedPath = downloaded.FilePath;
            downloadDirectory = Path.GetDirectoryName(downloadedPath);

            var result = await new ManualPackageImportService().ImportAsync(
                source.SourceId, [downloadedPath], _packageRoot,
                expectedDigest.Enabled ? expectedDigest.Text : null,
                CancellationToken.None,
                message => sourceDescription.Text = message);
            ApplyImportResult(result);
            MessageBox.Show(this,
                $"Загружен и импортирован файл: {downloaded.FileName}{Environment.NewLine}" +
                $"Размер: {downloaded.Size:N0} байт{Environment.NewLine}" +
                $"SHA-256: {downloaded.Sha256}{Environment.NewLine}" +
                $"Манифест: {result.ManifestPath}",
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Загрузка/импорт остановлены: " + ex.Message,
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            try { if (downloadedPath is not null && File.Exists(downloadedPath)) File.Delete(downloadedPath); } catch { }
            try { if (downloadDirectory is not null && Directory.Exists(downloadDirectory)) Directory.Delete(downloadDirectory, true); } catch { }
            downloadDirect.Enabled = source.Kind == LanguageSourceKind.ManualImport ||
                                     source.SourceId is "what-the-hack" or "rg-adguard-files";
            whatTheHack.Enabled = source.SourceId == "what-the-hack";
            import.Enabled = true;
            addFiles.Enabled = true;
        }
    }

    private async Task DownloadTorrentAsync()
    {
        if (sourceBox.SelectedItem is not LanguageResourceSourceDescriptor source ||
            source.Kind != LanguageSourceKind.ManualImport)
            return;
        if (!TorrentDownloadService.IsValidTorrentInput(torrentInput.Text))
        {
            MessageBox.Show(this, "Укажите действительную magnet-ссылку либо выберите локальный .torrent-файл.",
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        downloadTorrent.Enabled = false;
        browseTorrent.Enabled = false;
        import.Enabled = false;
        addFiles.Enabled = false;
        try
        {
            var service = new TorrentDownloadService();
            var result = await service.DownloadAsync(
                torrentInput.Text, _packageRoot, CancellationToken.None,
                message => sourceDescription.Text = message);
            foreach (var file in result.PackageFiles)
                if (!_files.Contains(file, StringComparer.OrdinalIgnoreCase))
                    _files.Add(file);
            selectedFiles.Text = string.Join(Environment.NewLine, _files);
            MessageBox.Show(this,
                $"Загрузка завершена. Найдено поддерживаемых файлов: {result.PackageFiles.Count}.{Environment.NewLine}" +
                "Файлы ещё не установлены: нажмите «Проверить и импортировать», чтобы выполнить хеширование, проверку подписи и CBS Package Identity.",
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "BitTorrent-загрузка остановлена: " + ex.Message,
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            downloadTorrent.Enabled = source.Kind == LanguageSourceKind.ManualImport;
            browseTorrent.Enabled = source.Kind == LanguageSourceKind.ManualImport;
            import.Enabled = true;
            addFiles.Enabled = true;
        }
    }

    private void ApplyImportResult(ManualPackageImportResult result)
    {
        ImportedMediaIso = result.ImportedMediaIso;
        ImportedFodDirectory = result.FodDirectory;
        ImportedLxpDirectory = result.LxpDirectory;
    }

    private async Task ImportSelectedFilesAsync()
    {
        if (sourceBox.SelectedItem is not LanguageResourceSourceDescriptor source)
            return;
        if (_files.Count == 0)
        {
            MessageBox.Show(this, "Сначала выберите локальные файлы или завершите загрузку раздачи.",
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            ApplyImportResult(result);
            var summary = $"Добавлено файлов: {result.ImportedFiles.Count}; уже было в каталоге: {result.SkippedFiles.Count}."
                + Environment.NewLine + $"Манифест: {result.ManifestPath}";
            MessageBox.Show(this, summary, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Импорт остановлен: " + ex.Message,
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            import.Enabled = true;
            addFiles.Enabled = true;
        }
    }
}
