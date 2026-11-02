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
    private readonly TextBox lxp = new();
    private readonly TextBox storeProductId = new();
    private readonly TextBox updates = new();
    private readonly TextBox drivers = new();
    private readonly TextBox unattend = new();
    private readonly RichTextBox log = new();
    private readonly Label status = new() { AutoSize = true };
    private readonly Button start = new() { Text = "Проверить условия и создать ISO", AutoSize = true };
    private readonly Button cancel = new() { Text = "Отменить", AutoSize = true, Enabled = false };
    private readonly CheckBox keep = new() { Text = "Сохранять рабочие файлы для диагностики", Checked = true, AutoSize = true };
    private readonly CheckBox setup = new() { Text = "Локализовать Setup / WinPE", Checked = true, AutoSize = true };
    private readonly CheckBox split = new() { Text = "Разбить install.wim на SWM (FAT32)", AutoSize = true };
    private readonly CheckBox hyperv = new() { Text = "Необязательно: проверить ISO в Hyper-V (120 секунд)", AutoSize = true };
    private readonly CheckBox allowPartial = new() { Text = "Разрешить частичную локализацию", AutoSize = true };
    private readonly Button preflight = new() { Text = "Предварительная проверка…", AutoSize = true };
    private readonly Button exportProfile = new() { Text = "Сохранить профиль…", AutoSize = true };
    private readonly Button importProfile = new() { Text = "Загрузить профиль…", AutoSize = true };
    private readonly Button patch = new() { Text = "Обработать существующий WIM…", AutoSize = true };
    private readonly Button catalog = new() { Text = "Microsoft Update Catalog…", AutoSize = true };
    private readonly Button uupCheck = new() { Text = "Проверить UUP Dump", AutoSize = true };
    private CancellationTokenSource? cancellation;
    private readonly ElevatedLaunchRequest? launchRequest;

    public MainForm(ElevatedLaunchRequest? request = null)
    {
        launchRequest = request;
        Text = $"LocaLizard {AppInfo.Version}";
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
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
        AddPath(paths, 7, "Каталог INF-драйверов для install.wim:", drivers, true, false);
        AddPath(paths, 8, "Файл автоматической установки (unattend.xml):", unattend, false, false,
            "Unattend XML (*.xml)|*.xml|Все файлы (*.*)|*.*");
        AddPath(paths, 9, "Каталог русских Store LXP (APPX/MSIX + License XML):", lxp, true, false);
        paths.RowCount++;
        paths.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        paths.Controls.Add(new Label { Text = "Microsoft Store Product ID для ru-RU LXP:", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 8, 8, 0) }, 0, 10);
        storeProductId.Dock = DockStyle.Fill;
        storeProductId.PlaceholderText = "Необязательно: ID продукта Microsoft Store";
        paths.Controls.Add(storeProductId, 1, 10);
        paths.SetColumnSpan(storeProductId, 2);
        root.Controls.Add(paths);

        var options = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 8, 0, 8) };
        options.Controls.Add(keep);
        options.Controls.Add(setup);
        options.Controls.Add(split);
        options.Controls.Add(hyperv);
        options.Controls.Add(allowPartial);
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
                   "Разделение SWM создаёт части до 3800 MiB. Hyper-V проверка необязательна; при её запуске сеть отключается, а экран Setup подтверждает оператор. " +
                   "Перемещайте разделитель, чтобы изменить высоту журнала."
        });
        log.Dock = DockStyle.Fill; log.ReadOnly = true; log.Font = new Font("Consolas", 9);
        workspace.Panel2.Controls.Add(log);
        root.Controls.Add(workspace);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        start.Click += async (_, _) => await StartAsync();
        patch.Click += async (_, _) => await PatchExistingWimAsync();
        catalog.Click += async (_, _) => await SearchCatalogAsync();
        uupCheck.Click += async (_, _) => await CheckUupDumpAsync();
        preflight.Click += async (_, _) => await RunPreflightAsync();
        exportProfile.Click += (_, _) => ExportProfile();
        importProfile.Click += (_, _) => ImportProfile();
        cancel.Click += (_, _) => cancellation?.Cancel();
        footer.Controls.Add(preflight); footer.Controls.Add(exportProfile); footer.Controls.Add(importProfile);
        footer.Controls.Add(start); footer.Controls.Add(patch); footer.Controls.Add(catalog); footer.Controls.Add(uupCheck); footer.Controls.Add(cancel);
        status.Padding = new Padding(10, 8, 0, 0);
        footer.Controls.Add(status);
        root.Controls.Add(footer);

        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocaLizard");
        work.Text = Path.Combine(appData, "Work");
        packages.Text = Path.Combine(appData, "Packages");
        status.Text = "Готово к настройке.";
    }

    private void AddPath(TableLayoutPanel panel, int row, string caption, TextBox field, bool folder, bool save, string? openFilter = null)
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
                using var dialog = new OpenFileDialog { Filter = openFilter ?? "ISO images (*.iso)|*.iso|All files (*.*)|*.*" };
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
        var options = requestedOptions ?? CurrentOptions();
        if (string.IsNullOrWhiteSpace(options.SourceIso) || string.IsNullOrWhiteSpace(options.OutputIso))
        {
            MessageBox.Show(this, "Укажите входной и выходной ISO.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
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
        start.Enabled = false; patch.Enabled = false; catalog.Enabled = false; uupCheck.Enabled = false; preflight.Enabled = false; cancel.Enabled = true; log.Clear();
        SaveSettings();
        try
        {
        var preflightReport = await Task.Run(() => BuildPreflightService.AnalyzeAsync(options, cancellation.Token));
        AppendLog(preflightReport.ToDisplayString());
        if (!preflightReport.CanProceed)
            throw new InvalidOperationException("Предварительная проверка обнаружила критические ошибки. Исправьте их и повторите запуск.");
        if (MessageBox.Show(this, preflightReport.ToDisplayString() + Environment.NewLine + Environment.NewLine +
            "Продолжить создание ISO?", AppInfo.Name, MessageBoxButtons.YesNo,
            preflightReport.HasWarnings ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes)
        {
            status.Text = "Сборка отменена после предварительной проверки.";
            return;
        }
        var service = new IsoBuildService();
        service.ConfirmHyperVSetup = ConfirmHyperVSetupAsync;
        service.Log += AppendLog;
        service.Status += message => { if (!IsDisposed) BeginInvoke((Action)(() => status.Text = message)); };
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
            start.Enabled = true; patch.Enabled = true; catalog.Enabled = true; uupCheck.Enabled = true; preflight.Enabled = true; cancel.Enabled = false;
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
        LxpDirectory = string.IsNullOrWhiteSpace(lxp.Text) ? null : lxp.Text.Trim(),
        StoreProductId = string.IsNullOrWhiteSpace(storeProductId.Text) ? null : storeProductId.Text.Trim(),
        UpdatesDirectory = string.IsNullOrWhiteSpace(updates.Text) ? null : updates.Text.Trim(),
        KeepWorkFiles = keep.Checked,
        ProcessSetup = setup.Checked,
        SplitWim = split.Checked,
        RunHyperVTest = hyperv.Checked,
        DriverDirectory = string.IsNullOrWhiteSpace(drivers.Text) ? null : drivers.Text.Trim(),
        UnattendFile = string.IsNullOrWhiteSpace(unattend.Text) ? null : unattend.Text.Trim(),
        AllowPartialLocalization = allowPartial.Checked
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
        if (!string.IsNullOrWhiteSpace(saved.LxpSource)) lxp.Text = saved.LxpSource;
        if (!string.IsNullOrWhiteSpace(saved.StoreProductId)) storeProductId.Text = saved.StoreProductId;
        if (!string.IsNullOrWhiteSpace(saved.UpdatesRoot)) updates.Text = saved.UpdatesRoot;
        if (!string.IsNullOrWhiteSpace(saved.DriverDirectory)) drivers.Text = saved.DriverDirectory;
        if (!string.IsNullOrWhiteSpace(saved.UnattendFile)) unattend.Text = saved.UnattendFile;
        keep.Checked = saved.KeepWorkFiles;
        setup.Checked = saved.ProcessSetup;
        split.Checked = saved.SplitWim;
        hyperv.Checked = saved.RunHyperVTest;
        allowPartial.Checked = saved.AllowPartialLocalization;
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
                LxpSource = lxp.Text.Trim(),
                StoreProductId = storeProductId.Text.Trim(),
                UpdatesRoot = updates.Text.Trim(),
                KeepWorkFiles = keep.Checked,
                ProcessSetup = setup.Checked,
                SplitWim = split.Checked,
                RunHyperVTest = hyperv.Checked,
                DriverDirectory = drivers.Text.Trim(),
                UnattendFile = unattend.Text.Trim(),
                AllowPartialLocalization = allowPartial.Checked
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
                $"Окно VMConnect открыто для виртуальной машины {vmName}. Видите экран Windows Setup?\n\nДа — подтвердить визуальную загрузку. Нет — оставить результат неподтверждённым.",
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

    private async Task CheckUupDumpAsync()
    {
        if (cancellation is not null) return;
        cancellation = new CancellationTokenSource();
        start.Enabled = false; patch.Enabled = false; catalog.Enabled = false; uupCheck.Enabled = false; cancel.Enabled = true;
        status.Text = "Проверяю доступность UUP Dump для LTSC…";
        log.Clear();
        try
        {
            using var client = new UupDumpClient();
            var report = await client.DiagnoseAsync(cancellation.Token);
            AppendLog(report);
            status.Text = "Диагностика UUP Dump завершена.";
            MessageBox.Show(this, report, "Диагностика UUP Dump", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            status.Text = "Диагностика UUP Dump отменена.";
            AppendLog("Диагностика UUP Dump отменена.");
        }
        catch (Exception ex)
        {
            status.Text = "Ошибка диагностики UUP Dump.";
            AppendLog("UUP Dump diagnostic error: " + ex);
            MessageBox.Show(this, ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            cancellation.Dispose(); cancellation = null;
            start.Enabled = true; patch.Enabled = true; catalog.Enabled = true; uupCheck.Enabled = true; cancel.Enabled = false;
        }
    }

    private async Task SearchCatalogAsync(string? requestedKnowledgeBase = null)
    {
        if (cancellation is not null) return;
        var kbInput = requestedKnowledgeBase ?? PromptText("Microsoft Update Catalog",
            "Введите номер KB (например, KB5061234). Оставьте пустым для поиска последнего стабильного LCU под выбранный ISO.", "");
        if (kbInput is null) return;
        var kb = System.Text.RegularExpressions.Regex.Replace(kbInput.Trim(), "(?i)^KB", "");
        if (kb.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(kb, "^\\d{6,8}$"))
        {
            MessageBox.Show(this, "Номер KB должен содержать 6–8 цифр.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!IsAdministrator())
        {
            SaveSettings();
            if (TryLaunchElevated(new ElevatedLaunchRequest("catalog", CurrentOptions(), KnowledgeBase: kb)))
            {
                status.Text = "Поиск Catalog передан повышенному экземпляру LocaLizard.";
                Close();
            }
            return;
        }

        string architecture;
        int? build = null;
        var sourceIso = source.Text.Trim();
        if (File.Exists(sourceIso) && sourceIso.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                status.Text = "Анализирую сборку и архитектуру исходного ISO…";
                var target = await InspectIsoTargetAsync(sourceIso);
                architecture = target.NormalizedArchitecture;
                build = target.BuildFamily;
                AppendLog($"Microsoft Update Catalog: target build={build}, architecture={architecture}.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось определить сборку/архитектуру ISO: " + ex.Message +
                    Environment.NewLine + "Укажите KB и выберите архитектуру вручную.",
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                architecture = PromptArchitecture();
                if (string.IsNullOrWhiteSpace(architecture)) return;
                build = null;
            }
        }
        else
        {
            if (kb.Length == 0)
            {
                MessageBox.Show(this, "Для поиска последнего LCU выберите входной ISO. Без ISO укажите KB.",
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            architecture = PromptArchitecture();
            if (string.IsNullOrWhiteSpace(architecture)) return;
        }

        if (kb.Length == 0 && build is null)
        {
            MessageBox.Show(this, "Не удалось получить семейство сборки. Укажите KB вручную.",
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        cancellation = new CancellationTokenSource();
        start.Enabled = false; patch.Enabled = false; catalog.Enabled = false; cancel.Enabled = true;
        SaveSettings();
        using var client = new MicrosoftUpdateCatalogClient();
        try
        {
            var query = kb.Length > 0 ? "KB" + kb : MicrosoftUpdateCatalogClient.QueryForBuild(build!.Value, architecture);
            status.Text = "Поиск обновлений в Microsoft Update Catalog…";
            var rows = await client.SearchAsync(query, architecture, kb.Length > 0 ? kb : null, build,
                cancellation.Token, AppendLog);
            if (rows.Count == 0) throw new InvalidOperationException("Catalog не вернул подходящих обновлений.");
            var selected = SelectCatalogUpdate(rows);
            if (selected is null) return;
            var match = System.Text.RegularExpressions.Regex.Match(selected.Title, "(?i)\\bKB\\s*(\\d{6,8})\\b");
            var selectedKb = match.Success ? match.Groups[1].Value : kb;
            if (selectedKb.Length == 0)
            {
                MessageBox.Show(this, "В заголовке выбранного обновления не найден номер KB.",
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (selected.BuildFamily is null)
            {
                MessageBox.Show(this, "Не удалось определить семейство сборки выбранного обновления.",
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (MessageBox.Show(this,
                "Будут загружены пакеты с официального Microsoft Update Catalog. SHA-1 проверяется, если Catalog предоставляет хеш; локально формируется SHA-256 и updates.json.\n\n" +
                selected.Title + "\n\nПродолжить?",
                AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            status.Text = $"Загружаю KB{selectedKb} и checkpoint-пакеты…";
            var result = await client.DownloadSelectedAsync(selected, selectedKb, architecture, packages.Text.Trim(),
                cancellation.Token, AppendLog);
            updates.Text = result.Folder;
            SaveSettings();
            status.Text = $"Загружено обновление KB{result.KnowledgeBase}.";
            AppendLog("Каталог обновлений сохранён: " + result.Folder);
            MessageBox.Show(this, "Пакеты загружены и проверены. Каталог updates.json выбран для следующей сборки:" +
                Environment.NewLine + result.Folder, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            status.Text = "Поиск/загрузка отменены.";
            AppendLog("Операция Microsoft Update Catalog отменена.");
        }
        catch (Exception ex)
        {
            status.Text = "Ошибка Microsoft Update Catalog.";
            AppendLog("Catalog error: " + ex);
            MessageBox.Show(this, ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            cancellation.Dispose(); cancellation = null;
            start.Enabled = true; patch.Enabled = true; catalog.Enabled = true; cancel.Enabled = false;
        }
    }

    private async Task<ImageInfo> InspectIsoTargetAsync(string isoPath)
    {
        var runner = new ProcessRunner();
        var escaped = Path.GetFullPath(isoPath).Replace("'", "''");
        var script = "$d=Mount-DiskImage -StorageType ISO -ImagePath '" + escaped +
            "' -PassThru -ErrorAction Stop | Get-Volume | Where-Object DriveLetter | Select-Object -First 1; " +
            "if(-not $d){throw 'ISO has no drive letter'}; Write-Output ($d.DriveLetter+':')";
        var mounted = await runner.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
            timeout: TimeSpan.FromMinutes(3));
        var drive = mounted.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).LastOrDefault(x => x.Length >= 2 && char.IsLetter(x[0]) && x[1] == ':')
            ?? throw new IOException("Не удалось определить букву ISO.");
        try
        {
            var sources = Path.Combine(drive + Path.DirectorySeparatorChar, "sources");
            var imageService = new WindowsImageService(runner);
            var wim = Directory.EnumerateFiles(sources, "install*.wim").FirstOrDefault()
                ?? Directory.EnumerateFiles(sources, "install*.esd").FirstOrDefault();
            if (wim is not null)
                return (await imageService.GetImageInfoAsync(wim, CancellationToken.None)).First();
            var swm = Path.Combine(sources, "install.swm");
            if (File.Exists(swm))
                return (await imageService.GetImageInfoAsync(swm, CancellationToken.None,
                    Path.Combine(sources, "install*.swm"))).First();
            throw new InvalidDataException("В ISO нет install.wim, install.esd или install.swm.");
        }
        finally
        {
            var dismount = "Dismount-DiskImage -ImagePath '" + escaped + "' -ErrorAction SilentlyContinue";
            await runner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", dismount],
                cancellationToken: CancellationToken.None, throwOnError: false, timeout: TimeSpan.FromMinutes(2));
        }
    }

    private string PromptArchitecture()
    {
        using var form = new Form
        {
            Text = "Архитектура", Width = 330, Height = 140,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false
        };
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 16, Top = 16, Width = 280 };
        combo.Items.AddRange(["x64", "x86", "arm64"]);
        combo.SelectedIndex = 0;
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 136, Top = 55, Width = 75 };
        var cancelButton = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Left = 221, Top = 55, Width = 75 };
        form.Controls.AddRange([combo, ok, cancelButton]);
        form.AcceptButton = ok; form.CancelButton = cancelButton;
        return form.ShowDialog(this) == DialogResult.OK ? combo.SelectedItem?.ToString() ?? "" : "";
    }

    private CatalogUpdate? SelectCatalogUpdate(IReadOnlyList<CatalogUpdate> rows)
    {
        using var form = new Form
        {
            Text = "Выберите обновление Microsoft Update Catalog",
            Width = 1000, Height = 420, StartPosition = FormStartPosition.CenterParent
        };
        var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        foreach (var item in rows)
            list.Items.Add($"{item.Title} | Дата: {item.Date} | Размер: {item.Size}");
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8)
        };
        var choose = new Button { Text = "Загрузить", DialogResult = DialogResult.OK, AutoSize = true };
        var close = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        panel.Controls.Add(choose); panel.Controls.Add(close);
        form.Controls.Add(list); form.Controls.Add(panel);
        form.AcceptButton = choose; form.CancelButton = close;
        return form.ShowDialog(this) == DialogResult.OK && list.SelectedIndex >= 0 ? rows[list.SelectedIndex] : null;
    }

    private string? PromptText(string caption, string message, string initialValue)
    {
        using var form = new Form
        {
            Text = caption, Width = 500, Height = 170, StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false
        };
        var label = new Label { Text = message, Left = 12, Top = 12, Width = 460, Height = 42 };
        var input = new TextBox { Text = initialValue, Left = 12, Top = 60, Width = 460 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 310, Top = 92, Width = 75 };
        var cancelButton = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Left = 397, Top = 92, Width = 75 };
        form.Controls.AddRange([label, input, ok, cancelButton]);
        form.AcceptButton = ok; form.CancelButton = cancelButton;
        return form.ShowDialog(this) == DialogResult.OK ? input.Text : null;
    }

    private void ExportProfile()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "LocaLizard profile (*.json)|*.json",
            DefaultExt = "json",
            FileName = "LocaLizard-profile.json",
            Title = "Сохранить профиль сборки"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var json = JsonSerializer.Serialize(CurrentOptions(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dialog.FileName, json);
            status.Text = "Профиль сохранён: " + dialog.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось сохранить профиль: " + ex.Message, AppInfo.Name,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ImportProfile()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "LocaLizard profile (*.json)|*.json|JSON files (*.json)|*.json",
            Title = "Загрузить профиль сборки"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var options = JsonSerializer.Deserialize<RunOptions>(File.ReadAllText(dialog.FileName))
                ?? throw new InvalidDataException("Профиль пуст или имеет неверный формат.");
            source.Text = options.SourceIso;
            donor.Text = options.DonorIso ?? "";
            output.Text = options.OutputIso;
            work.Text = options.WorkDirectory;
            packages.Text = options.PackageDirectory;
            fod.Text = options.FodDirectory ?? "";
            updates.Text = options.UpdatesDirectory ?? "";
            drivers.Text = options.DriverDirectory ?? "";
            unattend.Text = options.UnattendFile ?? "";
            keep.Checked = options.KeepWorkFiles;
            setup.Checked = options.ProcessSetup;
            split.Checked = options.SplitWim;
            hyperv.Checked = options.RunHyperVTest;
            allowPartial.Checked = options.AllowPartialLocalization;
            SaveSettings();
            status.Text = "Профиль загружен: " + dialog.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось загрузить профиль: " + ex.Message, AppInfo.Name,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RunPreflightAsync(RunOptions? requestedOptions = null)
    {
        if (cancellation is not null) return;
        var options = requestedOptions ?? CurrentOptions();
        if (!IsAdministrator())
        {
            SaveSettings();
            if (TryLaunchElevated(new ElevatedLaunchRequest("preflight", options)))
            {
                status.Text = "Проверка передана повышенному экземпляру LocaLizard.";
                Close();
            }
            return;
        }

        cancellation = new CancellationTokenSource();
        preflight.Enabled = false; start.Enabled = false; patch.Enabled = false;
        catalog.Enabled = false; uupCheck.Enabled = false; cancel.Enabled = true;
        try
        {
            status.Text = "Проверяю ISO, источники, совместимость и свободное место…";
            var report = await Task.Run(() => BuildPreflightService.AnalyzeAsync(options, cancellation.Token));
            AppendLog(report.ToDisplayString());
            status.Text = report.CanProceed ? "Предварительная проверка завершена." : "Найдены критические ошибки.";
            MessageBox.Show(this, report.ToDisplayString(), AppInfo.Name, MessageBoxButtons.OK,
                report.CanProceed ? (report.HasWarnings ? MessageBoxIcon.Warning : MessageBoxIcon.Information) : MessageBoxIcon.Error);
        }
        catch (OperationCanceledException)
        {
            status.Text = "Предварительная проверка отменена.";
        }
        catch (Exception ex)
        {
            status.Text = "Ошибка предварительной проверки.";
            AppendLog("Preflight error: " + ex);
            MessageBox.Show(this, ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            cancellation.Dispose(); cancellation = null;
            preflight.Enabled = true; start.Enabled = true; patch.Enabled = true;
            catalog.Enabled = true; uupCheck.Enabled = true; cancel.Enabled = false;
        }
    }

    private async Task ResumeElevatedRequestAsync()
    {
        if (launchRequest is null) return;
        switch (launchRequest.Mode)
        {
            case "preflight":
                await RunPreflightAsync(launchRequest.Options);
                break;
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
