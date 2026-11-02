using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using DismGui.Services;

namespace DismGui;

public partial class MainWindow : Window
{
    private readonly SessionLog log = App.Log;
    private readonly IDismRunner runner;
    private bool busy;
    private bool stopRequested;
    private string? loadedImage;
    private (long Length, DateTime Modified) loadedStamp;
    private static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public MainWindow()
    {
        InitializeComponent();
        runner = new DismRunner(log);
        MountPath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DismStudio", "Mount");
        AdminStatus.Text = IsAdmin ? "Administrator session" : "Standard session · elevation required to service";
        ElevateButton.Visibility = IsAdmin ? Visibility.Collapsed : Visibility.Visible;
        LogPathLabel.Text = log.FilePath;
        LogOutput.Text = log.ReadAll();
        log.LineWritten += LogLine;
        log.Write("INFO", $"Administrator={IsAdmin}. Log file: {log.FilePath}");
    }

    private void LogLine(string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (LogOutput.Text.Length > 160_000) LogOutput.Text = LogOutput.Text[^100_000..];
            LogOutput.AppendText(line + Environment.NewLine);
            LogOutput.ScrollToEnd();
        });
    }

    private void Report(string text) { StatusText.Text = text; log.Write("INFO", text); }
    private async Task RunOperation(Func<Task> action, bool requiresAdmin = true, bool injection = false)
    {
        if (busy) return;
        try
        {
            if (requiresAdmin && !IsAdmin) throw new InvalidOperationException("Restart as administrator using the button above, then choose your files again.");
            busy = true;
            stopRequested = false;
            InjectionForm.IsEnabled = ToolsForm.IsEnabled = RecoveryForm.IsEnabled = ElevateButton.IsEnabled = false;
            Activity.IsIndeterminate = true;
            StopButton.IsEnabled = injection;
            await action();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Stopped: " + ex.Message;
            try { log.Write("ERROR", ex.ToString()); } catch { }
            MessageBox.Show(this, $"{ex.Message}\n\nSession log:\n{log.FilePath}", "Operation stopped", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            busy = false;
            InjectionForm.IsEnabled = ToolsForm.IsEnabled = RecoveryForm.IsEnabled = ElevateButton.IsEnabled = true;
            Activity.IsIndeterminate = false;
            StopButton.IsEnabled = false;
        }
    }

    private static string ImageFile(string text)
    {
        var path = DismCommands.FullPath(text.Trim());
        if (!File.Exists(path) || !(Path.GetExtension(path).Equals(".wim", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".esd", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Choose an existing WIM or ESD image file.");
        return path;
    }
    private static (long, DateTime) Stamp(string path) { var f = new FileInfo(path); return (f.Length, f.LastWriteTimeUtc); }
    private string SelectedImage()
    {
        var path = ImageFile(ImagePath.Text);
        if (!path.Equals(loadedImage, StringComparison.OrdinalIgnoreCase) || Stamp(path) != loadedStamp)
            throw new InvalidOperationException("Read editions again; the image path or file has changed.");
        return path;
    }
    private InjectionPlan BuildPlan()
    {
        var p = new InjectionPlan(SelectedImage(), Editions.SelectedItems.Cast<ImageEdition>().Select(e => e.Index).Order().ToArray(),
            DismCommands.FullPath(MountPath.Text.Trim()), DriverFolders.Items.Cast<string>().ToArray());
        DismCommands.Validate(p);
        return p;
    }
    private bool Confirm(string text) => MessageBox.Show(this, text, "Confirm image operation", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    private string? Folder(string title)
    {
        var d = new OpenFolderDialog { Title = title };
        return d.ShowDialog(this) == true ? d.FolderName : null;
    }
    private void BrowseImage_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Windows images (*.wim;*.esd)|*.wim;*.esd", Title = "Choose a Windows installation image" };
        if (d.ShowDialog(this) == true) ImagePath.Text = d.FileName;
    }
    private void ImagePath_Changed(object sender, TextChangedEventArgs e)
    {
        loadedImage = null;
        if (Editions != null) Editions.ItemsSource = null;
    }
    private async void ReadEditions_Click(object sender, RoutedEventArgs e) => await RunOperation(async () =>
    {
        var path = ImageFile(ImagePath.Text);
        loadedImage = null;
        Editions.ItemsSource = null;
        var before = Stamp(path);
        Report("Reading image editions…");
        var result = await runner.RunAsync(DismCommands.Info(path));
        var editions = DismCommands.ParseEditions(result.Output);
        if (editions.Count == 0) throw new InvalidOperationException("DISM did not return any recognizable editions. See the log.");
        if (Stamp(path) != before) throw new IOException("The image changed while its editions were being read. Try again.");
        loadedImage = path;
        loadedStamp = before;
        Editions.ItemsSource = editions;
        Report($"Found {editions.Count} editions. Select the editions you want to service.");
    });
    private void BrowseMount_Click(object sender, RoutedEventArgs e) { var path = Folder("Choose an empty NTFS mount folder"); if (path != null) MountPath.Text = path; }
    private void AddDriver_Click(object sender, RoutedEventArgs e)
    {
        var path = Folder("Choose a folder of extracted INF drivers");
        if (path != null && !DriverFolders.Items.Cast<string>().Contains(path, StringComparer.OrdinalIgnoreCase)) DriverFolders.Items.Add(path);
    }
    private void RemoveDriver_Click(object sender, RoutedEventArgs e) { if (DriverFolders.SelectedItem is string path) DriverFolders.Items.Remove(path); }
    private async void Preview_Click(object sender, RoutedEventArgs e) => await RunOperation(() =>
    {
        var plan = BuildPlan();
        var preview = string.Join(Environment.NewLine + Environment.NewLine, DismCommands.Preview(plan));
        log.Write("PREVIEW", preview);
        var window = new Window { Title = "Command preview · no changes made", Owner = this, Width = 900, Height = 520, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Content = new TextBox { Text = preview, IsReadOnly = true, VerticalContentAlignment = VerticalAlignment.Top, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Margin = new Thickness(16) };
        window.ShowDialog();
        return Task.CompletedTask;
    }, requiresAdmin: false);
    private async void Inject_Click(object sender, RoutedEventArgs e) => await RunOperation(async () =>
    {
        var plan = BuildPlan();
        if (!Confirm($"Add drivers from {plan.DriverFolders.Length} folder(s) to image indexes {string.Join(", ", plan.Indexes)}?\n\n{plan.ImagePath}\n\nThis saves changes to the WIM. Keep a backup of the original. If an edition fails, earlier editions remain saved and the failed mount may need recovery.")) return;
        log.Write("INFO", $"Starting injection. Indexes={string.Join(",", plan.Indexes)}; mount={plan.MountPath}");
        try { await new ImageServicing(runner, Report).InjectAsync(plan, () => stopRequested); }
        finally { loadedImage = null; }
    }, injection: true);
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        stopRequested = true;
        StopButton.IsEnabled = false;
        Report("Stop requested. The current edition will finish and save before stopping.");
    }
    private void BrowseExport_Click(object sender, RoutedEventArgs e)
    {
        var d = new SaveFileDialog { Filter = "Windows image (*.wim)|*.wim", FileName = "install.wim", Title = "Export to a new WIM file" };
        if (d.ShowDialog(this) == true) ExportPath.Text = d.FileName;
    }
    private async void Export_Click(object sender, RoutedEventArgs e) => await RunOperation(async () =>
    {
        var source = SelectedImage();
        if (Editions.SelectedItems.Count != 1) throw new ArgumentException("Select exactly one edition on the Driver injection tab.");
        var target = DismCommands.FullPath(ExportPath.Text.Trim());
        if (File.Exists(target) || Directory.Exists(target) || !Path.GetExtension(target).Equals(".wim", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a new .wim destination file. Existing files will not be overwritten or appended to.");
        if (!Directory.Exists(Path.GetDirectoryName(target))) throw new ArgumentException("The destination folder does not exist.");
        await EnsureNotMounted(source, target);
        var index = ((ImageEdition)Editions.SelectedItems[0]!).Index;
        if (!Confirm($"Export index {index} from:\n{source}\n\nTo a new file:\n{target}")) return;
        Report("Exporting edition to WIM…");
        await runner.RunAsync(DismCommands.Export(source, index, target));
        Report($"Export complete: {target}. Browse to this file and read editions to service it.");
    });
    private void BrowseSplit_Click(object sender, RoutedEventArgs e) { var path = Folder("Choose an empty destination folder for split images"); if (path != null) SplitPath.Text = path; }
    private async void Split_Click(object sender, RoutedEventArgs e) => await RunOperation(async () =>
    {
        var source = ImageFile(ImagePath.Text);
        if (!Path.GetExtension(source).Equals(".wim", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only WIM files can be split. Export ESD to WIM first.");
        var folder = DismCommands.FullPath(SplitPath.Text.Trim());
        if (!Directory.Exists(folder) || Directory.EnumerateFileSystemEntries(folder).Any()) throw new ArgumentException("Choose an existing, empty destination folder.");
        await EnsureNotMounted(source, folder);
        if (!Confirm($"Split:\n{source}\n\nInto install.swm parts in:\n{folder}")) return;
        Report("Splitting WIM…");
        await runner.RunAsync(DismCommands.Split(source, Path.Combine(folder, "install.swm")));
        Report("Split complete. Copy every SWM part into the installation media's sources folder in place of install.wim.");
    });
    private async Task EnsureNotMounted(params string[] paths)
    {
        var mounts = DismCommands.ParseMounted((await runner.RunAsync(DismCommands.Mounted())).Output);
        if (mounts.Any(m => paths.Any(p => DismCommands.Overlaps(p, m.MountPath) || p.Equals(m.ImagePath, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("The source or destination is in use by a mounted image. Unmount it first.");
    }
    private async Task RefreshMounts()
    {
        Mounts.ItemsSource = null;
        var mounts = DismCommands.ParseMounted((await runner.RunAsync(DismCommands.Mounted())).Output);
        Mounts.ItemsSource = mounts;
        Report($"Found {mounts.Count} mounted images.");
    }
    private async void RefreshMounts_Click(object sender, RoutedEventArgs e) => await RunOperation(RefreshMounts);
    private async Task RecoverMount(string action)
    {
        await RunOperation(async () =>
        {
            if (Mounts.SelectedItem is not MountedImage selected) throw new ArgumentException("Refresh mounts and select an image first.");
            var current = DismCommands.ParseMounted((await runner.RunAsync(DismCommands.Mounted())).Output);
            if (!current.Contains(selected)) throw new InvalidOperationException("The mount has changed. Refresh the list and select it again.");
            var verb = action == "remount" ? "Remount" : action == "save" ? "Save all pending changes and unmount" : "Permanently DISCARD all pending changes and unmount";
            if (!Confirm($"{verb}?\n\n{selected}\n\nThis may include changes made by other servicing tools.")) return;
            Report($"{verb}: {selected.MountPath}");
            await runner.RunAsync(action == "remount" ? DismCommands.Remount(selected.MountPath) : DismCommands.Unmount(selected.MountPath, action == "save"));
            loadedImage = null;
            await RefreshMounts();
        });
    }
    private async void Remount_Click(object sender, RoutedEventArgs e) => await RecoverMount("remount");
    private async void SaveMount_Click(object sender, RoutedEventArgs e) => await RecoverMount("save");
    private async void DiscardMount_Click(object sender, RoutedEventArgs e) => await RecoverMount("discard");
    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"\"{log.DirectoryPath}\"", UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, $"Logs: {log.DirectoryPath}\n{ex.Message}", "Open logs"); }
    }
    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the executable.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(App).Assembly.Location);
            log.Write("INFO", "Requesting administrator restart. Selections are not carried across sessions.");
            if (Process.Start(start) != null) Close();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { log.Write("INFO", "Administrator restart canceled."); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Restart failed"); }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; MessageBox.Show(this, "An operation is still running. For driver injection, use Stop after edition and wait for the current edition to finish.", "DISM is busy"); }
        else log.LineWritten -= LogLine;
    }
}
