// Главный класс Avalonia: собирает окна, значок трея и обработчики кнопок.
// Здесь НЕТ прямых записей в контроллер ноутбука: их выполняет FanController.
// UI-поток отвечает только за элементы окна и пользовательские настройки.
// Отдельный STA-поток читает WMI и меняет охлаждение, поэтому окно не зависает.
// 
// Шпаргалка для начинающего:
//   private — деталь этого класса; readonly — ссылку нельзя заменить после создания;
//   int? — целое число либо null (датчик недоступен, а не температура 0);
//   (_, e) => ... — функция, которую вызовут позже при событии;
//   using var — автоматически освободить ресурс в конце блока;
//   async/await — дождаться результата, не блокируя интерфейс;
//   Volatile — сделать новое значение видимым другому потоку;
//   Dispatcher.UIThread.Post — безопасно передать обновление из потока WMI в UI.
// 
// settings принадлежит UI. Аппаратный поток получает отдельные копии конфигурации.
// FanScheduleConfig содержит неизменяемые точки: редактируемая таблица никогда
// не меняет массив, который в этот момент читает поток охлаждения.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Window = Avalonia.Controls.Window;
using Button = Avalonia.Controls.Button;
using CheckBox = Avalonia.Controls.CheckBox;
namespace LegionThermals;

public sealed class App : Avalonia.Application
{
    private readonly Settings settings = Settings.Load();
    private Window banner = null!;
    private Window? preferences;
    private readonly TextBlock cpu = new() { FontSize = 23, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#52D8FA") };
    private readonly TextBlock gpu = new() { FontSize = 23, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#FFB55D") };
    private TextBlock? status;
    private TrayIcon main = null!;
    private WindowIcon appIcon = null!;
    private NativeMenuItem bannerItem = null!, numbersItem = null!, startupItem = null!;
    private CancellationTokenSource stop = new();
    private Task? worker;
    private string lastStatus = L.T("Ожидание датчиков…", "Waiting for sensors…");
    private bool placing;
    private readonly DispatcherTimer positionSave = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private int? previousCpu, previousGpu;
    private bool iconsDrawn;
    private bool permissionNoticeShown;
    private readonly AutoResetEvent hardwareWake = new(false);
    private int fanManualWanted, fanPercentWanted;
    private FanScheduleConfig scheduleWanted = new(false, FanSchedule.Defaults());
    private CheckBox? scheduleToggle;
    private CheckBox? startupToggle;
    private TextBlock? startupStatus;
    private bool updatingStartup;
    private FanSnapshot lastFan = new(false, L.T("Проверка поддержки вентилятора…", "Checking fan support…"));
    private TextBlock? fanStatus, fanLevelLabel, savedStatus;
    private Slider? fanSlider;
    private CheckBox? fanToggle;
    private string? saveError, restoreError;
    private bool allowExit, exiting;
    // Добавляем тёмную тему. Стили задают внешний вид, а не аппаратное поведение.
    public override void Initialize() { Styles.Add(new FluentTheme()); RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark; }
    // Avalonia уже готова: создаём баннер/трей, переносим автозапуск и запускаем опрос.
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            positionSave.Tick += (_, _) => { positionSave.Stop(); Persist(); hardwareWake.Set(); };
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { if (StartupTask.LegacyEnabled) Settings.SetStartup(true); else StartupTask.RefreshPathIfEnabled(); }
            catch (Exception e) { lastStatus = L.T("Перенос автозапуска: ", "Startup migration: ") + e.Message; }
            desktop.ShutdownRequested += (_, e) => { if (!allowExit) { e.Cancel = true; _ = ShutdownAsync(desktop); } };
            banner = new Window { Width = 250, Height = 66, CanResize = false, WindowDecorations = WindowDecorations.None, ShowInTaskbar = false, Topmost = true, ShowActivated = false, Background = Brushes.Transparent, TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent }, Title = "Legion Thermals" };
            banner.Content = new Border { CornerRadius = new CornerRadius(12), Background = Brush.Parse("#192331"), Padding = new Thickness(13, 8), Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { cpu, gpu } } };
            banner.PointerPressed += (_, e) => { if (e.GetCurrentPoint(banner).Properties.IsLeftButtonPressed) banner.BeginMoveDrag(e); };
            banner.PointerReleased += (_, _) => { settings.X = banner.Position.X; settings.Y = banner.Position.Y; Persist(); };
            banner.PositionChanged += (_, _) => { if (!placing && banner.IsVisible) { settings.X = banner.Position.X; settings.Y = banner.Position.Y; positionSave.Stop(); positionSave.Start(); } };
            banner.Closing += (_, e) => { if (!stop.IsCancellationRequested) { e.Cancel = true; settings.Banner = false; Apply(); } };
            banner.Screens.Changed += (_, _) => Place();
            var menu = CreateTrayMenu();
            using var iconStream = AssetLoader.Open(new Uri("avares://LegionThermals/Assets/app.ico"));
            appIcon = new WindowIcon(iconStream);
            main = new TrayIcon { Icon = appIcon, ToolTipText = L.T("Legion Thermals · настройки", "Legion Thermals · settings"), Menu = menu, IsVisible = true };
            main.Clicked += (_, _) => OpenSettings();
            TrayIcon.SetIcons(this, new TrayIcons { main });
            desktop.Exit += (_, _) => { stop.Cancel(); positionSave.Stop(); main.Dispose(); try { settings.Save(); } catch { } };
            Apply();
            Volatile.Write(ref pollSeconds, settings.Interval);
            Volatile.Write(ref fanManualWanted, settings.FanManualEnabled ? 1 : 0);
            Volatile.Write(ref fanPercentWanted, settings.FanMinimumPercent);
            PublishSchedule();
            worker = Task.Run(PollLoop);
            if (Environment.GetCommandLineArgs().Contains("--settings")) OpenSettings();
        }
        base.OnFrameworkInitializationCompleted();
    }
    // Новый экземпляр меню позволяет сразу применить выбранный язык ко всем пунктам.
    private NativeMenu CreateTrayMenu()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var menu = new NativeMenu();
        bannerItem = Item(L.T("Баннер на рабочем столе", "Desktop banner"), () => { settings.Banner = !settings.Banner; Apply(); });
        numbersItem = Item(L.T("Температуры в панели задач", "Taskbar temperatures"), () => { settings.Numbers = !settings.Numbers; Apply(); });
        startupItem = Item(L.T("Автозапуск", "Start with Windows"), () => SetStartup(!Settings.Startup));
        bannerItem.ToggleType = numbersItem.ToggleType = startupItem.ToggleType = MenuItemToggleType.CheckBox;
        menu.Items.Add(Item(L.T("Настройки…", "Settings…"), OpenSettings)); menu.Items.Add(bannerItem); menu.Items.Add(numbersItem); menu.Items.Add(startupItem);
        menu.Items.Add(Item(L.T("В правый верхний угол", "Move to top right"), () => { settings.X = null; settings.Y = null; Place(); Persist(); }));
        menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(Item(L.T("Выход", "Exit"), () => _ = ShutdownAsync(desktop)));
        return menu;
    }
    private static NativeMenuItem Item(string name, Action action) { var i = new NativeMenuItem(name); i.Click += (_, _) => action(); return i; }
    // Debounce: частые движения ползунка объединяются в одну запись через 600 мс.
    private void ScheduleSave() { positionSave.Stop(); positionSave.Start(); }
    // Создаём отсортированный снимок таблицы; затем будим ожидающий аппаратный поток.
    private void PublishSchedule()
    {
        Volatile.Write(ref scheduleWanted, new(settings.FanSchedulerEnabled,
            settings.FanSchedulePoints.OrderBy(p => p.Temperature).ToArray()));
        hardwareWake.Set();
    }
    // Сохранение UI-настроек; ошибка видна отдельно и не затирается показаниями датчиков.
    private void Persist()
    {
        try { settings.Save(); saveError = null; }
        catch (Exception e) { saveError = L.T("Не удалось сохранить настройки: ", "Could not save settings: ") + e.Message; }
        if (savedStatus != null) savedStatus.Text = saveError ?? L.T("Настройки сохранены автоматически", "Settings saved automatically");
    }
    // Сначала прекращаем опрос и ждём аппаратного отката. Только потом закрываем приложение.
    private async Task ShutdownAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (exiting) return;
        exiting = true;
        Persist(); stop.Cancel(); hardwareWake.Set();
        if (worker != null) await worker;
        if (restoreError != null)
        {
            settings.FanManualEnabled = false;
            settings.FanSchedulerEnabled = false; PublishSchedule();
            Volatile.Write(ref fanManualWanted, 0);
            lastFan = new(false, L.T("Не подтверждён возврат Авто Lenovo: ", "Could not confirm Lenovo Auto restoration: ") + restoreError);
            stop = new CancellationTokenSource();
            worker = Task.Run(PollLoop);
            exiting = false;
            OpenSettings(); RefreshFan(); Persist();
            return;
        }
        allowExit = true;
        desktop.Shutdown();
    }
    // Применяем видимость, прозрачность и режим значка. Температурные команды здесь не отправляются.
    private void Apply()
    {
        banner.Opacity = settings.Opacity;
        if (settings.Banner) { if (!banner.IsVisible) { placing = true; banner.Show(); placing = false; } Place(); } else banner.Hide();
        main.Icon = settings.Numbers ? Icons.Temperatures(previousCpu, previousGpu) : appIcon;
        iconsDrawn = settings.Numbers;
        bannerItem.IsChecked = settings.Banner; numbersItem.IsChecked = settings.Numbers; startupItem.IsChecked = Settings.Startup;
        Persist();
    }
    // Находим экран сохранённой точки и ограничиваем координаты его рабочей областью.
    private void Place()
    {
        var screens = banner.Screens.All; var s = screens.FirstOrDefault(x => settings.X is int px && settings.Y is int py && x.WorkingArea.Contains(new PixelPoint(px, py))) ?? banner.Screens.Primary;
        if (s == null) return; var r = s.WorkingArea; var w = (int)Math.Ceiling(banner.Width * s.Scaling); var h = (int)Math.Ceiling(banner.Height * s.Scaling);
        placing = true;
        banner.Position = new PixelPoint(Math.Clamp(settings.X ?? r.Right - w - 16, r.X, Math.Max(r.X, r.Right - w)), Math.Clamp(settings.Y ?? r.Y + 16, r.Y, Math.Max(r.Y, r.Bottom - h)));
        placing = false;
    }
    // Рабочий цикл: измерить → выбрать запрос → применить → передать снимок в UI → ждать.
    private async Task PollLoop()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var reader = new SensorReader();
            using var fans = new FanController();
            var scheduler = new FanSchedule();
            FanScheduleConfig? previousSchedule = null;
            restoreError = null;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var reading = reader.Poll();
                    var schedule = Volatile.Read(ref scheduleWanted);
                    if (!ReferenceEquals(schedule, previousSchedule)) { scheduler.Reset(); previousSchedule = schedule; }
                    int? scheduledPercent = schedule.Enabled && reading.Cpu is int c && reading.Gpu is int g
                        ? scheduler.Select(schedule.Points, Math.Max(c, g)) : null;
                    if (schedule.Enabled && scheduledPercent == null) scheduler.Reset();
                    FanSnapshot snapshot;
                    try
                    {
                        snapshot = fans.Update(schedule.Enabled ? scheduledPercent != null : Volatile.Read(ref fanManualWanted) == 1,
                            scheduledPercent ?? Volatile.Read(ref fanPercentWanted), reading);
                        if (schedule.Enabled && snapshot.Supported && !snapshot.ManualStopped)
                            snapshot = snapshot with
                            {
                                Status = (scheduledPercent is int p
                                ? L.T($"Планировщик: max(CPU, GPU) = {Math.Max(reading.Cpu!.Value, reading.Gpu!.Value)} °C → {p}%.\n", $"Scheduler: max(CPU, GPU) = {Math.Max(reading.Cpu!.Value, reading.Gpu!.Value)} °C → {p}%.\n")
                                : L.T("Планировщик: нет надёжных температур, включено Авто Lenovo.\n", "Scheduler: reliable temperatures unavailable; Lenovo Auto enabled.\n")) + snapshot.Status
                            };
                    }
                    catch (Exception e)
                    {
                        Volatile.Write(ref fanManualWanted, 0);
                        snapshot = new(false, L.T("Управление вентиляторами: ", "Fan control: ") + e.Message, ManualStopped: true);
                    }
                    if (snapshot.ManualStopped) Volatile.Write(ref scheduleWanted, schedule with { Enabled = false });
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (stop.IsCancellationRequested) return;
                        Update(reading);
                        lastFan = snapshot;
                        if (snapshot.ManualStopped) { settings.FanManualEnabled = false; settings.FanSchedulerEnabled = false; PublishSchedule(); Persist(); }
                        RefreshFan();
                    });
                    var waitSeconds = Volatile.Read(ref scheduleWanted).Enabled ? 2 : Volatile.Read(ref pollSeconds);
                    if (WaitHandle.WaitAny(new[] { stop.Token.WaitHandle, hardwareWake }, TimeSpan.FromSeconds(waitSeconds)) == 0) break;
                }
            }
            catch (Exception e) { Dispatcher.UIThread.Post(() => { lastStatus = e.Message; if (status != null) status.Text = lastStatus; }); }
            finally
            {
                try { fans.Restore(); }
                catch (Exception e) { restoreError = e.Message; }
                completed.SetResult();
            }
        })
        { IsBackground = true, Name = "Lenovo temperature and fan worker" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await completed.Task;
    }
    // Показываем поддержку, режим и RPM. Табличная цель и измеренные обороты — разные величины.
    private void RefreshFan()
    {
        if (fanToggle != null) { fanToggle.IsEnabled = lastFan.Supported && !settings.FanSchedulerEnabled; fanToggle.IsChecked = settings.FanManualEnabled; }
        if (scheduleToggle != null) { scheduleToggle.IsEnabled = lastFan.Supported; scheduleToggle.IsChecked = settings.FanSchedulerEnabled; }
        if (fanSlider != null) fanSlider.IsEnabled = lastFan.Supported && settings.FanManualEnabled && !settings.FanSchedulerEnabled;
        if (fanStatus != null)
        {
            fanStatus.Text = lastFan.Status;
            if (lastFan.CpuActualRpm != null || lastFan.GpuActualRpm != null)
                fanStatus.Text += L.T($"\nТекущие обороты: CPU {lastFan.CpuActualRpm?.ToString() ?? "—"} / GPU {lastFan.GpuActualRpm?.ToString() ?? "—"} об/мин.", $"\nMeasured speed: CPU {lastFan.CpuActualRpm?.ToString() ?? "—"} / GPU {lastFan.GpuActualRpm?.ToString() ?? "—"} RPM.");
            if (lastFan.CpuTargetRpm != null) fanStatus.Text += L.T($"\nМинимум по таблице: CPU {lastFan.CpuTargetRpm} / GPU {lastFan.GpuTargetRpm} об/мин (не измерение текущих оборотов).", $"\nFirmware minimum: CPU {lastFan.CpuTargetRpm} / GPU {lastFan.GpuTargetRpm} RPM (target, not measured speed).");
        }
    }
    private int pollSeconds = 3;
    // Обновляем баннер/подсказку. Значок перерисовываем только при изменении цифр.
    private void Update(Reading r)
    {
        cpu.Text = $"CPU {r.Cpu?.ToString() ?? "—"}°"; gpu.Text = $"GPU {r.Gpu?.ToString() ?? "—"}°";
        lastStatus = r.Status; if (status != null) status.Text = lastStatus;
        if (!permissionNoticeShown && !SensorReader.IsAdministrator && r.Status.StartsWith(L.T("Для датчиков Lenovo нужны права", "Lenovo sensors require administrator")))
        {
            permissionNoticeShown = true;
            OpenSettings();
        }
        if (settings.Numbers && (!iconsDrawn || previousCpu != r.Cpu || previousGpu != r.Gpu))
        {
            main.Icon = Icons.Temperatures(r.Cpu, r.Gpu);
            iconsDrawn = true;
        }
        previousCpu = r.Cpu;
        previousGpu = r.Gpu;
        main.ToolTipText = $"CPU: {r.Cpu?.ToString() ?? "—"} °C · GPU: {r.Gpu?.ToString() ?? "—"} °C";
    }
    // Меняем задачу Windows и возвращаем checkbox в фактическое состояние при ошибке.
    private void SetStartup(bool enabled)
    {
        try
        {
            Settings.SetStartup(enabled);
            if (startupStatus != null) startupStatus.Text = enabled ? L.T("Автозапуск с правами администратора настроен. При входе UAC не требуется.", "Elevated startup is configured. No UAC prompt is needed at sign-in.") : L.T("Автозапуск выключен.", "Start with Windows is disabled.");
        }
        catch (Exception e) { OpenSettings(); if (startupStatus != null) startupStatus.Text = L.T("Автозапуск: ", "Startup: ") + e.Message; }
        updatingStartup = true;
        startupItem.IsChecked = Settings.Startup;
        if (startupToggle != null) startupToggle.IsChecked = startupItem.IsChecked;
        updatingStartup = false;
    }
    // Создаём окно настроек только при открытии. Закрытие окна не завершает монитор.
    private void OpenSettings()
    {
        if (preferences != null) { preferences.Show(); preferences.Activate(); return; }
        preferences = new Window { Title = L.T("Legion Thermals · настройки", "Legion Thermals · settings"), Width = 510, Height = 790, CanResize = false };
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 13 };
        panel.Children.Add(new TextBlock { Text = "Legion Thermals", FontSize = 24, FontWeight = FontWeight.Bold });
        var language = new Button { Content = L.T("Язык: Русский → English", "Language: English → Русский") };
        language.Click += (_, _) => ChangeLanguage(settings.Language == "ru" ? "en" : "ru");
        panel.Children.Add(language);
        panel.Children.Add(new TextBlock { Text = L.T("CPU — голубой сверху · GPU — оранжевый снизу", "CPU — blue, top · GPU — orange, bottom"), Foreground = Brushes.LightGray });
        var show = new CheckBox { Content = L.T("Баннер поверх окон", "Always-on-top banner"), IsChecked = settings.Banner }; show.IsCheckedChanged += (_, _) => { settings.Banner = show.IsChecked == true; Apply(); }; panel.Children.Add(show);
        var numbers = new CheckBox { Content = L.T("Цифры в области уведомлений", "Temperature numbers in the notification area"), IsChecked = settings.Numbers }; numbers.IsCheckedChanged += (_, _) => { settings.Numbers = numbers.IsChecked == true; Apply(); }; panel.Children.Add(numbers);
        startupToggle = new CheckBox { Content = L.T("Автозапуск от администратора при входе в Windows", "Start as administrator when signing in to Windows"), IsChecked = Settings.Startup };
        startupToggle.IsCheckedChanged += (_, _) => { if (!updatingStartup) SetStartup(startupToggle?.IsChecked == true); };
        panel.Children.Add(startupToggle);
        startupStatus = new TextBlock { Text = L.T("Через планировщик Windows, без пароля и повторного UAC при входе. При обычном запуске Windows сразу запрашивает права.", "Uses Windows Task Scheduler, without a password or UAC prompt at sign-in. A normal launch asks for administrator access."), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(startupStatus);
        var opacityLabel = new TextBlock { Text = L.T($"Непрозрачность: {settings.Opacity:P0}", $"Opacity: {settings.Opacity:P0}") }; panel.Children.Add(opacityLabel);
        var slider = new Slider { Minimum = 20, Maximum = 100, Value = settings.Opacity * 100 }; slider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) { settings.Opacity = slider.Value / 100; banner.Opacity = settings.Opacity; opacityLabel.Text = L.T($"Непрозрачность: {settings.Opacity:P0}", $"Opacity: {settings.Opacity:P0}"); ScheduleSave(); } }; panel.Children.Add(slider);
        var positions = new ComboBox { ItemsSource = new[] { L.T("Справа сверху", "Top right"), L.T("Слева сверху", "Top left"), L.T("Справа снизу", "Bottom right"), L.T("Слева снизу", "Bottom left") }, PlaceholderText = L.T("Положение на основном экране", "Position on the main screen"), HorizontalAlignment = HorizontalAlignment.Stretch };
        positions.SelectionChanged += (_, _) => { var s = banner.Screens.Primary; if (s == null) return; var r = s.WorkingArea; var right = positions.SelectedIndex % 2 == 0; var bottom = positions.SelectedIndex >= 2; settings.X = right ? r.Right - (int)(banner.Width * s.Scaling) - 16 : r.X + 16; settings.Y = bottom ? r.Bottom - (int)(banner.Height * s.Scaling) - 16 : r.Y + 16; Place(); Persist(); }; panel.Children.Add(positions);
        panel.Children.Add(new TextBlock { Text = L.T("Баннер можно перетаскивать мышью между экранами.", "Drag the banner between screens with the mouse."), TextWrapping = TextWrapping.Wrap });
        var interval = new ComboBox { ItemsSource = new[] { 1, 2, 3, 5, 10, 30 }, SelectedItem = settings.Interval, HorizontalAlignment = HorizontalAlignment.Stretch }; interval.SelectionChanged += (_, _) => { if (interval.SelectedItem is int i) { settings.Interval = i; Volatile.Write(ref pollSeconds, i); Persist(); } };
        var admin = new Button { Content = SensorReader.IsAdministrator ? L.T("Уже запущено от администратора", "Already running as administrator") : L.T("Перезапустить от администратора", "Restart as administrator"), IsEnabled = !SensorReader.IsAdministrator };
        admin.Click += async (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = "--restart --settings" }); await ShutdownAsync((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!); } catch (Exception e) { lastStatus = L.T("Перезапуск отменён или недоступен: ", "Restart was cancelled or unavailable: ") + e.Message; if (status != null) status.Text = lastStatus; } };
        panel.Children.Add(new TextBlock { Text = SensorReader.IsAdministrator ? L.T("Права процесса: администратор", "Process privileges: administrator") : L.T("Права процесса: обычные (датчики Lenovo могут требовать UAC)", "Process privileges: standard (Lenovo sensors may require UAC)"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        panel.Children.Add(admin);
        panel.Children.Add(new TextBlock { Text = L.T("Вентиляторы CPU и GPU", "CPU and GPU fans"), FontSize = 18, FontWeight = FontWeight.SemiBold });
        fanToggle = new CheckBox { Content = L.T("Ручной минимум (режим Lenovo Custom)", "Manual minimum (Lenovo Custom mode)"), IsChecked = settings.FanManualEnabled, IsEnabled = lastFan.Supported };
        fanToggle.IsCheckedChanged += (_, _) =>
        {
            var enabled = fanToggle?.IsChecked == true;
            if (settings.FanManualEnabled == enabled) return;
            settings.FanManualEnabled = enabled;
            Volatile.Write(ref fanManualWanted, enabled ? 1 : 0);
            Persist(); hardwareWake.Set(); RefreshFan();
        };
        panel.Children.Add(fanToggle);
        fanLevelLabel = new TextBlock { Text = L.T($"Минимальная скорость: {settings.FanMinimumPercent}%", $"Minimum fan speed: {settings.FanMinimumPercent}%") };
        panel.Children.Add(fanLevelLabel);
        fanSlider = new Slider { Minimum = FanPolicy.MinimumPercent, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true, Value = settings.FanMinimumPercent, IsEnabled = lastFan.Supported && settings.FanManualEnabled };
        fanSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty || fanSlider == null) return;
            settings.FanMinimumPercent = FanPolicy.ClampPercent((int)Math.Round(fanSlider.Value));
            Volatile.Write(ref fanPercentWanted, settings.FanMinimumPercent);
            if (fanLevelLabel != null) fanLevelLabel.Text = L.T($"Минимальная скорость: {settings.FanMinimumPercent}%", $"Minimum fan speed: {settings.FanMinimumPercent}%");
            ScheduleSave();
        };
        panel.Children.Add(fanSlider);
        panel.Children.Add(CreateScheduleEditor());
        var automatic = new Button { Content = L.T("Авто Lenovo — вернуть исходный режим", "Lenovo Auto — restore the original mode") };
        automatic.Click += (_, _) => { settings.FanManualEnabled = false; settings.FanSchedulerEnabled = false; PublishSchedule(); Volatile.Write(ref fanManualWanted, 0); Persist(); hardwareWake.Set(); RefreshFan(); };
        panel.Children.Add(automatic);
        panel.Children.Add(new TextBlock { Text = L.T("Ползунок задаёт нижнюю границу, не ограничивая охлаждение при нагреве. Ручной режим переключает Lenovo в Custom. При выходе возвращаются исходная таблица и режим.", "The slider sets a minimum without limiting cooling under load. Manual control selects Lenovo Custom. The original curve and mode are restored on exit."), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        fanStatus = new TextBlock { Text = lastFan.Status, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(fanStatus);
        panel.Children.Add(new TextBlock { Text = L.T("Опрос датчиков, секунд", "Sensor polling interval, seconds") }); panel.Children.Add(interval);
        status = new TextBlock { Text = lastStatus, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.LightGray }; panel.Children.Add(status);
        preferences.Content = new ScrollViewer { Content = panel }; preferences.PositionChanged += (_, _) => { if (preferences != null && preferences.IsVisible) { settings.PreferencesX = preferences.Position.X; settings.PreferencesY = preferences.Position.Y; ScheduleSave(); } };
        savedStatus = new TextBlock { Text = saveError ?? L.T("Настройки сохраняются автоматически", "Settings are saved automatically"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(savedStatus);
        preferences.Closed += (_, _) => { Persist(); preferences = null; status = null; fanToggle = null; fanSlider = null; scheduleToggle = null; startupToggle = null; startupStatus = null; fanStatus = null; savedStatus = null; };
        var savedPosition = settings.PreferencesX is int x && settings.PreferencesY is int y ? new PixelPoint(x, y) : (PixelPoint?)null;
        if (savedPosition is PixelPoint point && preferences.Screens.All.Any(screen => screen.WorkingArea.Contains(point))) preferences.Position = point;
        preferences.Show(); RefreshFan();
    }

    // Пересоздаём только элементы интерфейса: аппаратный поток и текущая кривая
    // продолжают работать. Немедленный wake обновляет динамические сообщения.
    private void ChangeLanguage(string language)
    {
        settings.Language = language;
        L.Language = language;
        lastStatus = L.T("Обновление показаний…", "Refreshing readings…");
        lastFan = lastFan with { Status = L.T("Обновление состояния вентиляторов…", "Refreshing fan status…") };
        Persist();
        preferences?.Close();
        main.Menu = CreateTrayMenu();
        Apply();
        OpenSettings();
        hardwareWake.Set();
    }

    // draft принадлежит редактору. Публикуем и сохраняем только целиком корректную таблицу.
    private Control CreateScheduleEditor()
    {
        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(new TextBlock { Text = L.T("Планировщик вентиляторов", "Temperature fan scheduler"), FontSize = 18, FontWeight = FontWeight.SemiBold });
        var draft = settings.FanSchedulePoints.ToList();
        var message = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var rows = new StackPanel { Spacing = 5 };
        bool ApplyDraft()
        {
            var error = FanSchedule.Validate(draft.ToArray());
            message.Text = error == null ? L.T("Таблица сохранена автоматически.", "Table saved automatically.") : error + L.T(" Изменения не применены; действует последняя корректная таблица.", " Changes were not applied; the last valid table remains active.");
            message.Foreground = error == null ? Brushes.LightGray : Brushes.Orange;
            if (error != null) return false;
            settings.FanSchedulePoints = draft.OrderBy(p => p.Temperature).ToArray();
            PublishSchedule(); ScheduleSave();
            return true;
        }
        scheduleToggle = new CheckBox { Content = L.T("Включить планировщик", "Enable temperature scheduler"), IsChecked = settings.FanSchedulerEnabled, IsEnabled = lastFan.Supported };
        scheduleToggle.IsCheckedChanged += (_, _) =>
        {
            var enabled = scheduleToggle?.IsChecked == true;
            if (enabled == settings.FanSchedulerEnabled) return;
            if (enabled && !ApplyDraft()) { scheduleToggle!.IsChecked = false; return; }
            settings.FanSchedulerEnabled = enabled;
            settings.FanManualEnabled = false;
            Volatile.Write(ref fanManualWanted, 0);
            PublishSchedule(); Persist(); RefreshFan();
        };
        root.Children.Add(scheduleToggle);
        root.Children.Add(new TextBlock { Text = L.T("Температура от, °C                         Скорость, %", "Temperature from, °C                    Fan speed, %"), FontSize = 12 });
        void Render()
        {
            rows.Children.Clear();
            for (var i = 0; i < draft.Count; i++)
            {
                var index = i;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,Auto"), ColumnSpacing = 8 };
                var temperature = new NumericUpDown { Minimum = 20, Maximum = 100, Increment = 1, FormatString = "0", Value = draft[i].Temperature, HorizontalAlignment = HorizontalAlignment.Stretch };
                var percent = new ComboBox { ItemsSource = new[] { 30, 40, 50, 60, 70, 80, 90, 100 }, SelectedItem = draft[i].Percent, HorizontalAlignment = HorizontalAlignment.Stretch };
                var remove = new Button { Content = "×", IsEnabled = draft.Count > 1 };
                temperature.PropertyChanged += (_, e) =>
                {
                    if (e.Property != NumericUpDown.ValueProperty) return;
                    if (temperature.Value is not decimal v) { message.Text = L.T("Введите температуру. Действует последняя корректная таблица.", "Enter a temperature. The last valid table remains active."); return; }
                    draft[index] = draft[index] with { Temperature = (int)v }; ApplyDraft();
                };
                percent.SelectionChanged += (_, _) => { if (percent.SelectedItem is int p) { draft[index] = draft[index] with { Percent = p }; ApplyDraft(); } };
                remove.Click += (_, _) => { draft.RemoveAt(index); Render(); ApplyDraft(); };
                Grid.SetColumn(percent, 1); Grid.SetColumn(remove, 2);
                row.Children.Add(temperature); row.Children.Add(percent); row.Children.Add(remove); rows.Children.Add(row);
            }
        }
        Render(); root.Children.Add(rows);
        var add = new Button { Content = L.T("Добавить строку", "Add row") };
        add.Click += (_, _) =>
        {
            if (draft.Count >= 16) { message.Text = L.T("Можно задать до 16 строк.", "You can add up to 16 rows."); return; }
            draft.Add(new(Math.Min(100, draft.Max(p => p.Temperature) + 5), draft.Max(p => p.Percent)));
            Render(); ApplyDraft();
        };
        root.Children.Add(add); root.Children.Add(message);
        root.Children.Add(new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Text = L.T("Используется большая из температур CPU и GPU. Ниже первого порога — 30%. Повышение на пороге, снижение после охлаждения на 3 °C. Опрос каждые 2 секунды. При CPU ≥90 °C или GPU ≥80 °C защита включает 100%. Это минимальная скорость: автоматика Lenovo может повышать её.", "Uses the higher CPU/GPU temperature. Below the first threshold: 30%. Speed increases at a threshold and decreases after cooling by more than 3 °C. Polls every 2 seconds. CPU ≥90 °C or GPU ≥80 °C forces 100%. This is a minimum; Lenovo may increase cooling.")
        });
        return root;
    }
}







