using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
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
    private readonly Button catalog = new() { Text = "Microsoft Update Catalog…", AutoSize = true };
    private CancellationTokenSource? cancellation;
    private readonly ElevatedLaunchRequest? launchRequest;

    public MainForm(ElevatedLaunchRequest? request = null)
    {
        launchRequest = request;
        Text = $"LocaLizard {AppInfo.Version}";
        Width = 1060; Height = 840; MinimumSize = new Size(860, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BuildUi();
        AttachIsoDrop(source, false);
        AttachIsoDrop(donor, true);
        Load += (_, _) => LoadSettings();
        FormClosing += (_, _) => SaveSettings();
        Shown += async (_, _) => await ResumeElevatedRequestAsync();
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
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 5, AutoScroll = true };
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

        var workspace = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 74,
            Panel1MinSize = 48,
            Panel2MinSize = 140,
            BorderStyle = BorderStyle.FixedSingle
        };
        workspace.Panel1.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, AutoSize = false, ForeColor = Color.DarkSlateGray,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6),
            Text = "Setup/WinPE и WinRE используют официальные ресурсы Windows ADK / WinPE Add-on или совместимый русский ISO-донор. " +
                   "Разделение SWM создаёт части до 3800 MiB. Hyper-V проверка отключает сеть; оператор вручную подтверждает экран Windows Setup. " +
                   "Перемещайте разделитель, чтобы изменить высоту журнала."
        });
        log.Dock = DockStyle.Fill; log.ReadOnly = true; log.Font = new Font("Consolas", 9);
        workspace.Panel2.Controls.Add(log);
        root.Controls.Add(workspace);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        start.Click += async (_, _) => await StartAsync();
        patch.Click += async (_, _) => await PatchExistingWimAsync();
        catalog.Click += async (_, _) => await SearchCatalogAsync();
        cancel.Click += (_, _) => cancellation?.Cancel();
        footer.Controls.Add(start); footer.Controls.Add(patch); footer.Controls.Add(catalog); footer.Controls.Add(cancel);
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
        var captionLabel = new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 8, 8, 0) };
        if (field == source) AttachIsoDrop(captionLabel, false);
        if (field == donor) AttachIsoDrop(captionLabel, true);
        panel.Controls.Add(captionLabel, 0, row);
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

    private void AttachIsoDrop(Control target, bool donorTarget)
    {
        target.AllowDrop = true;
        target.DragEnter += (_, e) =>
        {
            var paths = e.Data?.GetData(DataFormats.FileDrop) as string[];
            e.Effect = paths?.Any(path => path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) && File.Exists(path)) == true
                ? DragDropEffects.Copy : DragDropEffects.None;
        };
        target.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;
            var iso = paths.FirstOrDefault(path => path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) && File.Exists(path));
            if (iso is null) return;
            if (donorTarget)
            {
                donor.Text = iso;
                status.Text = "Выбран русский ISO-донор: " + iso;
            }
            else SetSource(iso);
        };
    }

    private void SetSource(string value)
    {
        source.Text = value;
        if (string.IsNullOrWhiteSpace(output.Text))
            output.Text = Path.Combine(Path.GetDirectoryName(value)!, Path.GetFileNameWithoutExtension(value) + "_ru-RU.iso");
        status.Text = "Выбран ISO: " + value;
    }

    private async Task StartAsync(RunOptions? requestedOptions = null)
    {
        if (cancellation is not null) return;
        if (string.IsNullOrWhiteSpace(source.Text) || string.IsNullOrWhiteSpace(output.Text))
        {
            MessageBox.Show(this, "Укажите входной и выходной ISO.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var options = requestedOptions ?? CurrentOptions();
        if (requestedOptions is null &&
            MessageBox.Show(this, "Исходный ISO не изменяется. Для DISM и монтирования ISO нужны повышенные права. Продолжить?",
                AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!IsAdministrator())
        {
            SaveSettings();
            if (TryLaunchElevated(new ElevatedLaunchRequest("build", options)))
            {
                status.Text = "Операция передана повышенному экземпляру LocaLizard.";
                Close();
            }
            return;
        }

        cancellation = new CancellationTokenSource();
        start.Enabled = false; patch.Enabled = false; catalog.Enabled = false; cancel.Enabled = true; log.Clear();
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
            start.Enabled = true; patch.Enabled = true; catalog.Enabled = true; cancel.Enabled = false;
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

    private async Task PatchExistingWimAsync(string? requestedSourceWim = null,
        string? requestedOutputWim = null, RunOptions? requestedOptions = null)
    {
        if (cancellation is not null) return;
        var sourcePath = requestedSourceWim;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            using var sourceDialog = new OpenFileDialog
            {
                Filter = "Windows image (*.wim)|*.wim",
                Title = "Выберите исходный WIM"
            };
            if (sourceDialog.ShowDialog(this) != DialogResult.OK) return;
            sourcePath = sourceDialog.FileName;
        }
        var outputPath = requestedOutputWim;
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            using var outputDialog = new SaveFileDialog
            {
                Filter = "Windows image (*.wim)|*.wim",
                DefaultExt = "wim",
                FileName = Path.GetFileNameWithoutExtension(sourcePath) + "_ru-RU.wim",
                Title = "Сохранить обработанный WIM"
            };
            if (outputDialog.ShowDialog(this) != DialogResult.OK) return;
            outputPath = outputDialog.FileName;
        }
        var options = requestedOptions ?? CurrentOptions();
        if (requestedOptions is null &&
            MessageBox.Show(this, "Исходный WIM не изменяется. Будет создана и проверена его рабочая копия. Продолжить?",
                AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!IsAdministrator())
        {
            SaveSettings();
            if (TryLaunchElevated(new ElevatedLaunchRequest("patch", options, sourcePath, outputPath)))
            {
                status.Text = "Обработка WIM передана повышенному экземпляру LocaLizard.";
                Close();
            }
            return;
        }

        cancellation = new CancellationTokenSource();
        start.Enabled = false; patch.Enabled = false; catalog.Enabled = false; cancel.Enabled = true; log.Clear();
        SaveSettings();
        var service = new IsoBuildService();
        service.Log += AppendLog;
        service.Status += message => { if (!IsDisposed) BeginInvoke((Action)(() => status.Text = message)); };
        try
        {
            var result = await Task.Run(() => service.PatchExistingWimAsync(sourcePath, outputPath,
                options, cancellation.Token));
            AppendLog("Создан WIM: " + result);
            MessageBox.Show(this, "Обработанный WIM создан и повторно проверен:" + Environment.NewLine + result,
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) { AppendLog("Обработка WIM отменена."); }
        catch (Exception ex)
        {
            AppendLog("ОШИБКА: " + ex);
            MessageBox.Show(this, ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            cancellation.Dispose(); cancellation = null;
            start.Enabled = true; patch.Enabled = true; catalog.Enabled = true; cancel.Enabled = false;
        }
    }

    private async Task ResumeElevatedRequestAsync()
    {
        if (launchRequest is null) return;
        switch (launchRequest.Mode)
        {
            case "build":
                await StartAsync(launchRequest.Options);
                break;
            case "patch":
                await PatchExistingWimAsync(launchRequest.SourceWim, launchRequest.OutputWim, launchRequest.Options);
                break;
            case "catalog":
                await SearchCatalogAsync(launchRequest.KnowledgeBase ?? "");
                break;
        }
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private bool TryLaunchElevated(ElevatedLaunchRequest request)
    {
        try
        {
            var executable = Environment.ProcessPath ?? Application.ExecutablePath;
            var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request));
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("--elevated");
            startInfo.ArgumentList.Add(payload);
            var process = Process.Start(startInfo);
            if (process is null) throw new InvalidOperationException("Windows не запустил повышенный экземпляр LocaLizard.");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show(this, "Запрос UAC отменён. Приложение осталось открытым без повышенных прав.",
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось запросить повышение прав: " + ex.Message,
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
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
