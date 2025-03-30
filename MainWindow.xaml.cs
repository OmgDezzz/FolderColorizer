using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Threading;
using System.ComponentModel;
using System.Security;

namespace FolderColorChanger
{
    public partial class MainWindow : Window
    {
        // Shell notification constants
        private const int SHCNE_ALLEVENTS = -2147483648;
        private const int SHCNE_UPDATEDIR = 0x08000000;
        private const int SHCNE_UPDATEITEM = 0x00002000;
        private const int SHCNF_PATHW = 0x0005;
        private const int SHCNF_FLUSH = 0x1000;

        // Context menu refresh constants
        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_FLUSHNOWAIT = 0x2000;
        private const int WM_SETTINGCHANGE = 0x001A;
        private const int SMTO_ABORTIFHUNG = 0x0002;

        private ColorOption? _selectedColor;
        private readonly string _iconsFolder;
        private readonly Dictionary<string, IntPtr> _folderHandles = new();
        private CancellationTokenSource _refreshTokenSource = new();
        private bool _isContextMenuInstalled;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, int Msg, IntPtr wParam, string lParam,
            int fuFlags, int uTimeout, out IntPtr lpdwResult);

        public class ColorOption
        {
            public string ColorName { get; set; } = string.Empty;
            public string IconPath { get; set; } = string.Empty;
            public BitmapImage? IconImage => File.Exists(IconPath) ? new BitmapImage(new Uri(IconPath)) : null;
        }

        public MainWindow()
        {
            _iconsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons");
            InitializeComponent();
            LoadColorOptions();
            CheckContextMenuInstallation();
            this.StateChanged += MainWindow_StateChanged;
        }

        private void CheckContextMenuInstallation()
        {
            try
            {
                using (var key = Registry.ClassesRoot.OpenSubKey(@"Directory\shell\FolderColorChanger"))
                {
                    _isContextMenuInstalled = key != null;
                }
                ContextMenuToggle.IsChecked = _isContextMenuInstalled;

                if (!_isContextMenuInstalled)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            InstallContextMenu();
                        }
                        catch (SecurityException)
                        {
                            AdminInstallButton.Visibility = Visibility.Visible;
                        }
                    });
                }
            }
            catch
            {
                _isContextMenuInstalled = false;
            }
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
        }

        protected override void OnClosed(EventArgs e)
        {
            _refreshTokenSource?.Cancel();
            CleanupHandles();
            base.OnClosed(e);
        }

        private void CleanupHandles()
        {
            foreach (var handle in _folderHandles.Values.Where(h => h != IntPtr.Zero))
            {
                SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, handle, IntPtr.Zero);
                Marshal.FreeHGlobal(handle);
            }
            _folderHandles.Clear();
        }

        private void LoadColorOptions()
        {
            if (!Directory.Exists(_iconsFolder))
            {
                MessageBox.Show("Icons folder not found. Please ensure an 'icons' folder exists in the application directory.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var colorOptions = new List<ColorOption>();

            AddColorOption(colorOptions, "Default", "default.ico");
            AddColorOption(colorOptions, "Black", "black.ico");
            AddColorOption(colorOptions, "Blue", "blue.ico");
            AddColorOption(colorOptions, "Brown", "brown.ico");
            AddColorOption(colorOptions, "Gray", "gray.ico");
            AddColorOption(colorOptions, "Green", "green.ico");
            AddColorOption(colorOptions, "Orange", "orange.ico");
            AddColorOption(colorOptions, "Red", "red.ico");
            AddColorOption(colorOptions, "Yellow", "yellow.ico");

            ColorPalette.ItemsSource = colorOptions;
        }

        private void AddColorOption(List<ColorOption> colorOptions, string colorName, string iconFile)
        {
            string iconPath = Path.Combine(_iconsFolder, iconFile);
            if (File.Exists(iconPath))
            {
                colorOptions.Add(new ColorOption { ColorName = colorName, IconPath = iconPath });
            }
        }

        private void AddFoldersButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select folders to change color",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true && dialog.FolderNames != null)
            {
                foreach (var folder in dialog.FolderNames.Where(f => !string.IsNullOrWhiteSpace(f)))
                {
                    if (!FoldersListView.Items.Contains(folder))
                    {
                        FoldersListView.Items.Add(folder);
                        _folderHandles[folder] = Marshal.StringToHGlobalUni(folder);
                    }
                }
                StatusText.Text = $"{dialog.FolderNames.Length} folder(s) added";
            }
        }

        private void RemoveFoldersButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = FoldersListView.SelectedItems.Cast<string>().ToList();
            foreach (var item in selectedItems)
            {
                FoldersListView.Items.Remove(item);
                if (_folderHandles.TryGetValue(item, out var handle) && handle != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(handle);
                    _folderHandles.Remove(item);
                }
            }
            StatusText.Text = $"{selectedItems.Count} folder(s) removed";
        }

        private void ColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: ColorOption colorOption } && colorOption != null)
            {
                _selectedColor = colorOption;
                StatusText.Text = $"Selected color: {_selectedColor.ColorName}";
            }
        }

        private async void ApplyColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedColor == null || FoldersListView.Items.Count == 0)
            {
                StatusText.Text = _selectedColor == null ? "Please select a color first" : "No folders selected";
                return;
            }

            ApplyColorButton.IsEnabled = false;
            StatusText.Text = "Applying changes...";

            try
            {
                await ApplyChangesAsync();
            }
            finally
            {
                ApplyColorButton.IsEnabled = true;
            }
        }

        private async Task ApplyChangesAsync()
        {
            _refreshTokenSource?.Cancel();
            _refreshTokenSource = new CancellationTokenSource();
            var token = _refreshTokenSource.Token;

            int successCount = 0;
            var tasks = new List<Task>();

            foreach (string folderPath in FoldersListView.Items.OfType<string>())
            {
                if (string.IsNullOrEmpty(folderPath)) continue;

                tasks.Add(Task.Run(() =>
                {
                    if (token.IsCancellationRequested) return;

                    try
                    {
                        if (ChangeFolderIcon(folderPath, _selectedColor?.IconPath ?? string.Empty))
                        {
                            Interlocked.Increment(ref successCount);
                            ForceExplorerRefresh(folderPath);
                        }
                    }
                    catch { /* Ignore individual folder errors */ }
                }, token));
            }

            await Task.WhenAll(tasks);
            Dispatcher.Invoke(() => StatusText.Text = $"Applied {_selectedColor?.ColorName} to {successCount} folder(s)");
        }

        private bool ChangeFolderIcon(string folderPath, string iconPath)
        {
            if (!Directory.Exists(folderPath)) return false;

            string desktopIniPath = Path.Combine(folderPath, "desktop.ini");

            if (iconPath.EndsWith("default.ico", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(desktopIniPath))
                {
                    MakeFileWritable(desktopIniPath);
                    File.Delete(desktopIniPath);
                    File.SetAttributes(folderPath, File.GetAttributes(folderPath) & ~FileAttributes.System);
                }
                return true;
            }

            if (!File.Exists(iconPath)) return false;

            MakeFileWritable(desktopIniPath);
            File.WriteAllLines(desktopIniPath, new[]
            {
                "[.ShellClassInfo]",
                $"IconResource={iconPath},0",
                "ConfirmFileOp=0"
            });

            File.SetAttributes(desktopIniPath, FileAttributes.Hidden | FileAttributes.System);
            File.SetAttributes(folderPath, File.GetAttributes(folderPath) | FileAttributes.System);

            return true;
        }

        private bool IsCloudSyncedFolder(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                  (path.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("Dropbox", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("Google Drive", StringComparison.OrdinalIgnoreCase));
        }

        private void MakeFileWritable(string filePath)
        {
            if (File.Exists(filePath))
            {
                File.SetAttributes(filePath, FileAttributes.Normal);
            }
        }

        private void ForceExplorerRefresh(string folderPath)
        {
            try
            {
                SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);

                if (_folderHandles.TryGetValue(folderPath, out var handle) && handle != IntPtr.Zero)
                {
                    SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSH, handle, IntPtr.Zero);
                }

                Task.Run(() =>
                {
                    try
                    {
                        foreach (var process in Process.GetProcessesByName("explorer"))
                        {
                            try { process.Kill(); } catch { }
                        }
                        Process.Start("explorer.exe");
                    }
                    catch { /* Ignore explorer restart errors */ }
                });
            }
            catch { /* Ignore refresh errors */ }
        }

        private void ContextMenuToggle_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isContextMenuInstalled)
                {
                    UninstallContextMenu();
                    ContextMenuToggle.IsChecked = false;
                    StatusText.Text = "Removed from context menu";
                }
                else
                {
                    InstallContextMenu();
                    ContextMenuToggle.IsChecked = true;
                    StatusText.Text = "Added to context menu - refreshing Explorer...";
                }
                _isContextMenuInstalled = !_isContextMenuInstalled;
            }
            catch (SecurityException)
            {
                RequestAdminInstall();
            }
        }

        private void AdminInstallButton_Click(object sender, RoutedEventArgs e)
        {
            RequestAdminInstall();
        }

        private void InstallContextMenu()
        {
            string? appPath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(appPath))
            {
                StatusText.Text = "Error: Could not get application path";
                return;
            }

            try
            {
                using (var key = Registry.ClassesRoot.CreateSubKey(@"Directory\shell\FolderColorChanger"))
                {
                    key.SetValue("", "Change Folder Color");
                    key.SetValue("Icon", appPath);
                    key.SetValue("ExtendedSubCommandsKey", "Directory\\FolderColorChanger");

                    using (var shellKey = key.CreateSubKey("shell"))
                    {
                        if (ColorPalette.ItemsSource is IEnumerable<ColorOption> colors)
                        {
                            foreach (var color in colors)
                            {
                                if (color == null) continue;

                                using (var colorKey = shellKey.CreateSubKey(color.ColorName))
                                {
                                    colorKey.SetValue("", color.ColorName);
                                    colorKey.SetValue("Icon", color.IconPath);

                                    using (var cmdKey = colorKey.CreateSubKey("command"))
                                    {
                                        string command = $"\"{appPath}\" \"%1\" \"{color.ColorName}\"";
                                        cmdKey.SetValue("", command);
                                        Debug.WriteLine($"Created context menu command: {command}");
                                    }
                                }
                            }
                        }
                    }
                }

                RefreshExplorer();
                _isContextMenuInstalled = true;
                ContextMenuToggle.IsChecked = true;
                AdminInstallButton.Visibility = Visibility.Collapsed;
                StatusText.Text = "Context menu installed!";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Install failed: {ex.Message}";
                Debug.WriteLine($"Context menu installation error: {ex}");
            }
        }

        private void RefreshExplorer()
        {
            try
            {
                SHChangeNotify((int)SHCNE_ASSOCCHANGED, (int)SHCNF_FLUSHNOWAIT, IntPtr.Zero, IntPtr.Zero);

                IntPtr result;
                SendMessageTimeout(
                    new IntPtr(0xFFFF),
                    WM_SETTINGCHANGE,
                    IntPtr.Zero,
                    "Environment",
                    SMTO_ABORTIFHUNG,
                    1000,
                    out result);

                foreach (var process in Process.GetProcessesByName("explorer"))
                {
                    try { process.Kill(); } catch { }
                }
                Process.Start("explorer.exe");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Explorer refresh error: {ex}");
            }
        }

        private void RequestAdminInstall()
        {
            string? appPath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(appPath)) return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = appPath,
                    Arguments = "--install-context-menu",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                };

                Process.Start(psi);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Admin installation failed";
                Debug.WriteLine($"Admin install error: {ex}");
            }
        }

        private void UninstallContextMenu()
        {
            try
            {
                Registry.ClassesRoot.DeleteSubKeyTree(@"Directory\shell\FolderColorChanger", false);
                RefreshExplorer();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Uninstall error: {ex}");
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);
    }
}