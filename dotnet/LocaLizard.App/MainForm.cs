using LocaLizard.Core;

namespace LocaLizard.App;

public sealed class MainForm : Form
{
    private readonly TextBox source = new();
    private readonly TextBox donor = new();
    private readonly TextBox output = new();
    private readonly TextBox work = new();
    private readonly TextBox packages = new();
    private readonly TextBox fod = new();
    private readonly TextBox updates = new();
    private readonly RichTextBox log = new();
    private readonly Label status = new() { AutoSize = true };
    private readonly Button start = new() { Text = "Проверить условия и создать ISO", AutoSize = true };
    private readonly Button cancel = new() { Text = "Отменить", AutoSize = true, Enabled = false };
    private readonly CheckBox keep = new() { Text = "Сохранять рабочие файлы для диагностики", Checked = true, AutoSize = true };
    private readonly CheckBox setup = new() { Text = "Локализовать Setup / WinPE", Checked = true, AutoSize = true };
    private readonly CheckBox split = new() { Text = "Разбить install.wim на SWM (FAT32)", AutoSize = true };
    private readonly CheckBox hyperv = new() { Text = "Проверить ISO в Hyper-V (120 секунд)", AutoSize = true };
    private readonly Button patch = new() { Text = "Обработать существующий WIM…", AutoSize = true };
    private CancellationTokenSource? cancellation;

    public MainForm()
    {
        Text = $"LocaLizard {AppInfo.Version}";
        Width = 1060; Height = 840; MinimumSize = new Size(860, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BuildUi();
        Load += (_, _) => LoadSettings();
        FormClosing += (_, _) => SaveSettings();
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
            {
                var iso = paths.FirstOrDefault(p => p.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) && File.Exists(p));
                if (iso is not null) SetSource(iso);
            }
        };
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 6, AutoScroll = true };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = "Подготовка Windows IoT Enterprise LTSC с русским языком",
            Font = new Font(Font, FontStyle.Bold), AutoSize = true, Padding = new Padding(0, 0, 0, 8)
        });

        var paths = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        AddPath(paths, 0, "Входной Windows ISO:", source, false, false);
        AddPath(paths, 1, "Русский ISO-донор:", donor, false, false);
        AddPath(paths, 2, "Выходной ISO:", output, false, true);
        AddPath(paths, 3, "Рабочая папка:", work, true, false);
        AddPath(paths, 4, "Кэш языковых пакетов:", packages, true, false);
        AddPath(paths, 5, "FOD / Language and Optional Features:", fod, true, false);
        AddPath(paths, 6, "Каталог обновлений (updates.json):", updates, true, false);
        root.Controls.Add(paths);

        var options = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 8, 0, 8) };
        options.Controls.Add(keep);
        options.Controls.Add(setup);
        options.Controls.Add(split);
        options.Controls.Add(hyperv);
        root.Controls.Add(options);

        root.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(980, 0), ForeColor = Color.DarkSlateGray,
            Text = "Setup/WinPE и WinRE используют официальные ресурсы Windows ADK / WinPE Add-on или совместимый русский ISO-донор. " +
                   "Разделение SWM создаёт части до 3800 MiB. Hyper-V проверка отключает сеть; оператор вручную подтверждает экран Windows Setup."
        });

        log.Dock = DockStyle.Fill; log.ReadOnly = true; log.Font = new Font("Consolas", 9);
        root.Controls.Add(log);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        start.Click += async (_, _) => await StartAsync();
        patch.Click += async (_, _) => await PatchExistingWimAsync();
        cancel.Click += (_, _) => cancellation?.Cancel();
        footer.Controls.Add(start); footer.Controls.Add(patch); footer.Controls.Add(cancel);
        status.Padding = new Padding(10, 8, 0, 0);
        footer.Controls.Add(status);
        root.Controls.Add(footer);

        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocaLizard");
        work.Text = Path.Combine(appData, "Work");
        packages.Text = Path.Combine(appData, "Packages");
        status.Text = "Готово к настройке.";
    }

    private void AddPath(TableLayoutPanel panel, int row, string caption, TextBox field, bool folder, bool save)
    {
        panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 8, 8, 0) }, 0, row);
        field.Dock = DockStyle.Fill;
        panel.Controls.Add(field, 1, row);
        var button = new Button { Text = "Выбрать…", AutoSize = true };
        button.Click += (_, _) =>
        {
            if (folder)
            {
                using var dialog = new FolderBrowserDialog();
                if (dialog.ShowDialog(this) == DialogResult.OK) field.Text = dialog.SelectedPath;
            }
            else if (save)
            {
                using var dialog = new SaveFileDialog { Filter = "ISO images (*.iso)|*.iso", DefaultExt = "iso" };
                if (dialog.ShowDialog(this) == DialogResult.OK) field.Text = dialog.FileName;
            }
            else
            {
                using var dialog = new OpenFileDialog { Filter = "ISO images (*.iso)|*.iso|All files (*.*)|*.*" };
                if (dialog.ShowDialog(this) == DialogResult.OK) field.Text = dialog.FileName;
            }
        };
        panel.Controls.Add(button, 2, row);
    }

    private void SetSource(string value)
    {
        source.Text = value;
        if (string.IsNullOrWhiteSpace(output.Text))
            output.Text = Path.Combine(Path.GetDirectoryName(value)!, Path.GetFileNameWithoutExtension(value) + "_ru-RU.iso");
        status.Text = "Выбран ISO: " + value;
    }

    private async Task StartAsync()
    {
        if (cancellation is not null) return;
        if (string.IsNullOrWhiteSpace(source.Text) || string.IsNullOrWhiteSpace(output.Text))
        {
            MessageBox.Show(this, "Укажите входной и выходной ISO.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var options = CurrentOptions();
        if (MessageBox.Show(this, "Исходный ISO не изменяется. Для DISM и монтирования ISO нужны права администратора. Продолжить?",
                AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        cancellation = new CancellationTokenSource();
        start.Enabled = false; patch.Enabled = false; cancel.Enabled = true; log.Clear();
        SaveSettings();
        var service = new IsoBuildService();
        service.ConfirmHyperVSetup = ConfirmHyperVSetupAsync;
        service.Log += AppendLog;
        service.Status += message => { if (!IsDisposed) BeginInvoke((Action)(() => status.Text = message)); };
        try
        {
            var result = await Task.Run(() => service.RunAsync(options, cancellation.Token));
            AppendLog("Создан ISO: " + result);
            MessageBox.Show(this, "ISO успешно создан:" + Environment.NewLine + result, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            status.Text = "Отменено. Рабочая папка сохранена.";
            AppendLog("Операция отменена.");
        }
        catch (Exception ex)
        {
            status.Text = "Ошибка. Подробности записаны в журнал.";
            AppendLog("ОШИБКА: " + ex);
            MessageBox.Show(this, ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            cancellation.Dispose(); cancellation = null;
            start.Enabled = true; patch.Enabled = true; cancel.Enabled = false;
        }
    }

    private RunOptions CurrentOptions() => new()
    {
        SourceIso = source.Text.Trim(),
        DonorIso = string.IsNullOrWhiteSpace(donor.Text) ? null : donor.Text.Trim(),
        OutputIso = output.Text.Trim(),
        WorkDirectory = work.Text.Trim(),
        PackageDirectory = packages.Text.Trim(),
        FodDirectory = string.IsNullOrWhiteSpace(fod.Text) ? null : fod.Text.Trim(),
        UpdatesDirectory = string.IsNullOrWhiteSpace(updates.Text) ? null : updates.Text.Trim(),
        KeepWorkFiles = keep.Checked,
        ProcessSetup = setup.Checked,
        SplitWim = split.Checked,
        RunHyperVTest = hyperv.Checked
    };

    private void LoadSettings()
    {
        var saved = SettingsStore.Load();
        if (!string.IsNullOrWhiteSpace(saved.SourceIso)) source.Text = saved.SourceIso;
        if (!string.IsNullOrWhiteSpace(saved.DonorIso)) donor.Text = saved.DonorIso;
        if (!string.IsNullOrWhiteSpace(saved.OutputIso)) output.Text = saved.OutputIso;
        if (!string.IsNullOrWhiteSpace(saved.WorkRoot)) work.Text = saved.WorkRoot;
        if (!string.IsNullOrWhiteSpace(saved.PackageRoot)) packages.Text = saved.PackageRoot;
        if (!string.IsNullOrWhiteSpace(saved.FodSource)) fod.Text = saved.FodSource;
        if (!string.IsNullOrWhiteSpace(saved.UpdatesRoot)) updates.Text = saved.UpdatesRoot;
        keep.Checked = saved.KeepWorkFiles;
        setup.Checked = saved.ProcessSetup;
        split.Checked = saved.SplitWim;
        hyperv.Checked = saved.RunHyperVTest;
    }

    private void SaveSettings()
    {
        try
        {
            SettingsStore.Save(new AppSettings
            {
                SourceIso = source.Text.Trim(),
                DonorIso = donor.Text.Trim(),
                OutputIso = output.Text.Trim(),
                WorkRoot = work.Text.Trim(),
                PackageRoot = packages.Text.Trim(),
                FodSource = fod.Text.Trim(),
                UpdatesRoot = updates.Text.Trim(),
                KeepWorkFiles = keep.Checked,
                ProcessSetup = setup.Checked,
                SplitWim = split.Checked,
                RunHyperVTest = hyperv.Checked
            });
        }
        catch (Exception ex) { AppendLog("Не удалось сохранить настройки: " + ex.Message); }
    }

    private Task<bool> ConfirmHyperVSetupAsync(string vmName)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke((Action)(() =>
        {
            var answer = MessageBox.Show(this,
                $"Окно VMConnect открыто для виртуальной машины {vmName}. Видите экран Windows Setup?\\n\\nДа — подтвердить визуальную загрузку. Нет — оставить результат неподтверждённым.",
                AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            completion.TrySetResult(answer == DialogResult.Yes);
        }));
        return completion.Task;
    }

    private async Task PatchExistingWimAsync()
    {
        if (cancellation is not null) return;
        using var sourceDialog = new OpenFileDialog { Filter = "Windows image (*.wim)|*.wim", Title = "Выберите исходный WIM" };
        if (sourceDialog.ShowDialog(this) != DialogResult.OK) return;
        using var outputDialog = new SaveFileDialog
        {
            Filter = "Windows image (*.wim)|*.wim", DefaultExt = "wim",
            FileName = Path.GetFileNameWithoutExtension(sourceDialog.FileName) + "_ru-RU.wim",
            Title = "Сохранить обработанный WIM"
        };
        if (outputDialog.ShowDialog(this) != DialogResult.OK) return;
        if (MessageBox.Show(this, "Исходный WIM не изменяется. Будет создана и проверена его рабочая копия. Продолжить?",
                AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        cancellation = new CancellationTokenSource();
        start.Enabled = false; patch.Enabled = false; cancel.Enabled = true; log.Clear();
        SaveSettings();
        var service = new IsoBuildService();
        service.Log += AppendLog;
        service.Status += message => { if (!IsDisposed) BeginInvoke((Action)(() => status.Text = message)); };
        try
        {
            var result = await Task.Run(() => service.PatchExistingWimAsync(sourceDialog.FileName, outputDialog.FileName,
                CurrentOptions(), cancellation.Token));
            AppendLog("Создан WIM: " + result);
            MessageBox.Show(this, "Обработанный WIM создан и повторно проверен:" + Environment.NewLine + result,
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) { AppendLog("Обработка WIM отменена."); }
        catch (Exception ex) { AppendLog("ОШИБКА: " + ex); MessageBox.Show(this, ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally
        {
            cancellation.Dispose(); cancellation = null;
            start.Enabled = true; patch.Enabled = true; cancel.Enabled = false;
        }
    }

    private void AppendLog(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke((Action)(() => AppendLog(message))); return; }
        log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        log.SelectionStart = log.TextLength; log.ScrollToCaret();
    }
}
