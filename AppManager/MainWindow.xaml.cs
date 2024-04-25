using iMobileDevice;
using iMobileDevice.iDevice;
using iMobileDevice.Lockdown;
using iMobileDevice.Plist;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AppManager
{
    public partial class MainWindow : Window
    {
        private readonly IiDeviceApi idevice = LibiMobileDevice.Instance.iDevice;
        private readonly ILockdownApi lockdown = LibiMobileDevice.Instance.Lockdown;

        private iDeviceHandle deviceHandle;
        private LockdownClientHandle lockdownHandle;

        private string deviceType;
        private string deviceName;
        private string deviceVersion;
        private string deviceUDID = "";
        bool madeTempFile = false;

        private string ipaPath = "";
        private string selectedBundleID = "";

        private bool gotDeviceInfo;

        private List<DeviceApp> appList;

        private readonly Timer deviceDetectorTimer;

        public MainWindow()
        {
            InitializeComponent();
            NativeLibraries.Load();
            appList = new List<DeviceApp>();
            deviceDetectorTimer = new Timer
            {
                Interval = 1000
            };
            deviceDetectorTimer.Elapsed += Event_deviceDetectorTimer_Tick;
        }

        private void Event_window_Loaded(object sender, RoutedEventArgs e)
        {
            deviceDetectorTimer.Start();
        }

        private void Event_deviceDetectorTimer_Tick(object sender, EventArgs e)
        {
            int count = 0;
            if (idevice.idevice_get_device_list(out ReadOnlyCollection<string> udids, ref count) == iDeviceError.NoDevice || count == 0)
            {
                deviceUDID = "";

                Dispatcher.Invoke(
                    System.Windows.Threading.DispatcherPriority.Normal,
                    new Action(
                    delegate ()
                    {
                        installNewAppButton.IsEnabled = false;
                        removeSelectedAppButton.IsEnabled = false;
                        refreshAppListButton.IsEnabled = false;
                        window.Title = $"AppManager (No device)";
                    }
                ));
                if (gotDeviceInfo)
                {
                    Dispatcher.Invoke(
                            System.Windows.Threading.DispatcherPriority.Normal,
                            new Action(
                            delegate ()
                            {
                                Log("Device disconnected.");
                                installedAppsListView.ItemsSource = null;
                            }
                        ));
                    gotDeviceInfo = false;
                }
            }
            else
            {
                if (!gotDeviceInfo)
                {
                    try
                    {
                        Dispatcher.Invoke(
                            System.Windows.Threading.DispatcherPriority.Normal,
                            new Action(
                            delegate ()
                            {
                                Log("Connecting to device...");
                            }
                        ));
                        idevice.idevice_new(out deviceHandle, udids[0]).ThrowOnError();
                        lockdown.lockdownd_client_new_with_handshake(deviceHandle, out lockdownHandle, "AppManager").ThrowOnError();

                        // get device info
                        lockdown.lockdownd_get_device_name(lockdownHandle, out deviceName).ThrowOnError();
                        lockdown.lockdownd_get_value(lockdownHandle, null, "ProductVersion", out PlistHandle temp).ThrowOnError();
                        temp.Api.Plist.plist_get_string_val(temp, out deviceVersion);
                        lockdown.lockdownd_get_value(lockdownHandle, null, "ProductType", out temp).ThrowOnError();
                        temp.Api.Plist.plist_get_string_val(temp, out deviceType);

                        temp.Dispose();

                        deviceUDID = udids[0];

                        Dispatcher.Invoke(
                            System.Windows.Threading.DispatcherPriority.Normal,
                            new Action(
                            async delegate ()
                            {
                                window.Title = $"AppManager ({deviceName}, {deviceType}, iOS {deviceVersion})";
                                installNewAppButton.IsEnabled = true;
                                removeSelectedAppButton.IsEnabled = true;
                                refreshAppListButton.IsEnabled = true;
                                await Task.Run(new Action(GetAppsThread));
                                installedAppsListView.ItemsSource = null;
                                installedAppsListView.ItemsSource = appList;
                            }
                        ));
                        gotDeviceInfo = true;
                    }
                    catch (Exception ex)
                    {
                        deviceUDID = "";
                        Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Normal, new Action(
                            delegate ()
                            {
                                installNewAppButton.IsEnabled = false;
                                removeSelectedAppButton.IsEnabled = false;
                                refreshAppListButton.IsEnabled = false;
                                window.Title = $"AppManager (No device)";
                                Log($"Could not connect to device: {ex.Message}");
                            }
                        ));
                        gotDeviceInfo = false; // never should matter but just in case
                    }
                }
            }
        }

        private void Event_installNewAppButton_Click(object sender, RoutedEventArgs e)
        {
            var openIPAFile = new Microsoft.Win32.OpenFileDialog
            {
                FileName = "app",
                DefaultExt = ".ipa",
                Filter = "iOS Apps (.ipa)|*.ipa"
            };

            openIPAFile.ShowDialog();

            string origIpaPath = openIPAFile.FileName;
            string fixedIpaPath = Regex.Replace(origIpaPath, @"[^\u0000-\u007F]+", "_");
            if (origIpaPath == fixedIpaPath) { ipaPath = origIpaPath; madeTempFile = false; }
            else
            {
                Log("Filename contains invalid characters. Making duplicate");
                madeTempFile = true;
                Debug.WriteLine(origIpaPath);
                File.Copy(origIpaPath, fixedIpaPath);
                ipaPath = fixedIpaPath;
            }

            Log($"Attempting install of {ipaPath}");
            Task.Run(new Action(InstallAppThread));
        }

        private void Event_removeSelectedAppButton_Click(object sender, RoutedEventArgs e)
        {
            selectedBundleID = ((DeviceApp)installedAppsListView.SelectedItem).CFBundleIdentifier;

            Log($"Attempting removal of of {selectedBundleID}");
            Task.Run(new Action(RemoveAppThread));
        }

        private void Event_refreshAppListButton_Click(object sender, RoutedEventArgs e)
        {
            Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Normal, new Action(
                async delegate ()
                {
                    await Task.Run(new Action(GetAppsThread));
                }
            ));
            installedAppsListView.ItemsSource = null;
            installedAppsListView.ItemsSource = appList;
            Log("Refreshed.");
        }

        private void RemoveAppThread()
        {
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "win-x86\\ideviceinstaller.exe",
                    Arguments = $"-u {deviceUDID} --uninstall \"{selectedBundleID}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            while (!proc.StandardOutput.EndOfStream && !proc.StandardError.EndOfStream)
            {
                string line = proc.StandardOutput.ReadLine();
                if (line == null || line.Trim() == "") line = proc.StandardError.ReadLine();
                Dispatcher.Invoke(() =>
                {
                    Log(line);
                });
            }
            Dispatcher.Invoke(() =>
            {
                Log($"Process ended with code {proc.ExitCode} {(proc.ExitCode == 0 ? "(Success)" : "")}");
                Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Normal, new Action(
                    async delegate ()
                    {
                        await Task.Run(new Action(GetAppsThread));
                    }
                ));
                installedAppsListView.ItemsSource = null;
                installedAppsListView.ItemsSource = appList;
            });
        }

        private void InstallAppThread()
        {
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "win-x86\\ideviceinstaller.exe",
                    Arguments = $"-u {deviceUDID} --install \"{ipaPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            while (!proc.StandardOutput.EndOfStream && !proc.StandardError.EndOfStream)
            {
                string line = proc.StandardOutput.ReadLine();
                if (line == null || line.Trim() == "") line = proc.StandardError.ReadLine();
                Dispatcher.Invoke(() =>
                {
                    Log(line);
                });
            }
            Dispatcher.Invoke(() =>
            {
                Log($"Process ended with code {proc.ExitCode} {(proc.ExitCode == 0 ? "(Success)" : "")}");
                Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Normal, new Action(
                async delegate ()
                {
                    await Task.Run(new Action(GetAppsThread));
                }
                ));
                installedAppsListView.ItemsSource = null;
                installedAppsListView.ItemsSource = appList;
            });
            if (madeTempFile)
            {
                File.Delete(ipaPath);
                madeTempFile = false;
            }
        }

        private void GetAppsThread()
        {
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "win-x86\\ideviceinstaller.exe",
                    Arguments = $"-u {deviceUDID} -l",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            appList = new List<DeviceApp>();
            appList.Clear();
            while (!proc.StandardOutput.EndOfStream)
            {
                string line = proc.StandardOutput.ReadLine();
                if (line == null || line.Trim() == "" || line.Contains("CFBundleIdentifier, CFBundleVersion, CFBundleDisplayName")) continue;
                appList.Add(new DeviceApp()
                {
                    CFBundleIdentifier = line.Split(',')[0],
                    CFBundleVersion = line.Split(',')[1].Trim().Replace("\"", ""),
                    CFBundleDisplayName = line.Split(',')[2].Trim().Replace("\"", ""),
                });
            }
            proc.WaitForExit();
        }

        public void Log(string msg)
        {
            logListBox.Items.Add(msg);
            var border = (Border)VisualTreeHelper.GetChild(logListBox, 0);
            var scrollViewer = (ScrollViewer)VisualTreeHelper.GetChild(border, 0);
            scrollViewer.ScrollToBottom();
        }
    }

    public class DeviceApp
    {
        public string CFBundleIdentifier { get; set; }
        public string CFBundleVersion { get; set; }
        public string CFBundleDisplayName { get; set; }
    }
}
