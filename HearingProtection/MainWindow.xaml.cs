using NAudio.CoreAudioApi;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Hardcodet.Wpf.TaskbarNotification;

namespace HearingProtection
{
    [Serializable]
    internal class DataConfig
    {
        public bool VolumeChanger;
        public bool ProtectType;
        public bool VolumeSensitivity;
        public float SoundLimit;
    }

    public partial class MainWindow : Window
    {
        private Queue<float> _volumeHistory = new Queue<float>();
        private const int _maxVolumeHistoryCount = 200;
        private string _baseConfigFilePath = string.Empty;
        private float _volume = 0;
        private bool _volumeChangerIsActive = false;
        private bool _volumeRecovery = false;
        private int _penaltyPoints = 0;
        private float _targetVolumeScalar = 0;
        private float _volumeMean = 0;
        private float _volumeDifference = 0;
        private bool _volumeHightSensitivityIsChecked = false;
        private bool _detectBasedOnAverage = false;
        private float _permittedVolumeLevel = 1;
        private bool _realClose = false;

        public MainWindow()
        {
            InitializeComponent();

            Closing += OnSaveConfig;

            TrayIcon.ToolTipText = "Click to expand the application";
            TrayIcon.Icon = new System.Drawing.Icon(Properties.Resources.icon, new System.Drawing.Size(128, 128));
            TrayIcon.MenuActivation = PopupActivationMode.LeftOrRightClick;
            TrayIcon.PopupActivation = PopupActivationMode.DoubleClick;
            TrayIcon.TrayMouseDoubleClick += OnClickTrayIcon;
            //TrayIcon.Visibility = Visibility.Hidden;
            TrayIcon.Visibility = Visibility.Visible;

#if !DEBUG
            _baseConfigFilePath = AppDomain.CurrentDomain.BaseDirectory;
            _baseConfigFilePath = Path.Combine(_baseConfigFilePath, "config.json");

            if (File.Exists(_baseConfigFilePath))
            {
                try
                {
                    string configStringData = File.ReadAllText(_baseConfigFilePath);
                    var dataConfig = JsonConvert.DeserializeObject<DataConfig>(configStringData);
                    if (dataConfig != null)
                    {
                        VolumeChanger.IsChecked = dataConfig.VolumeChanger;
                        ProtectType.IsChecked = dataConfig.ProtectType;
                        VolumeSensitivity.IsChecked = dataConfig.VolumeSensitivity;
                        SoundLimit.Value = dataConfig.SoundLimit;
                    }
                }
                catch { }

                Hide();
                //TrayIcon.Visibility = Visibility.Visible;
            }

            try
            {
                Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                string getRegPath = (string)key.GetValue("Hearing Protection", null);
                string exePath = System.Reflection.Assembly.GetEntryAssembly().Location.Replace(".dll", ".exe");
                if (getRegPath == null || getRegPath != exePath)
                    key.SetValue("Hearing Protection", exePath);
            }
            catch { }
#endif

            Task.Run(VolumeWatcher);
            Task.Run(UpdateUIAndSettings);
        }

        private void OnClickTrayIcon(object sender, RoutedEventArgs e)
        {
            Show();
            //TrayIcon.Visibility = Visibility.Hidden;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_realClose)
            {
                e.Cancel = true;
                Hide();
                //TrayIcon.Visibility = Visibility.Visible;
            }
            base.OnClosing(e);
        }

        private void OnSaveConfig(object? sender, System.ComponentModel.CancelEventArgs e)
        {
#if !DEBUG
            try
            {
                var dataConfig = new DataConfig();
                dataConfig.VolumeChanger = (VolumeChanger.IsChecked != null) ? (bool)VolumeChanger.IsChecked : false;
                dataConfig.ProtectType = (ProtectType.IsChecked != null) ? (bool)ProtectType.IsChecked : false;
                dataConfig.VolumeSensitivity = (VolumeSensitivity.IsChecked != null) ? (bool)VolumeSensitivity.IsChecked : false;
                dataConfig.SoundLimit = (float)SoundLimit.Value;
                File.WriteAllText(_baseConfigFilePath, JsonConvert.SerializeObject(dataConfig));
            } catch { }
#endif
        }

        private float MoveTowards(float current, float target, float maxDelta)
        {
            if (Math.Abs(target - current) <= maxDelta)
                return target;
            return current + Math.Sign(target - current) * maxDelta;
        }

        private async Task UpdateUIAndSettings()
        {
            bool pastVolumeRecovery = _volumeRecovery;
  
            while (true)
            {
                Dispatcher.Invoke(() =>
                {
                    VolumeText.Content = _volume.ToString();
                    VolumeProgress.Value = _volume;

                    if (_volumeHistory.Count < _maxVolumeHistoryCount)
                        VolumeDifference.Content = _volumeHistory.Count.ToString();
                    else
                    {
                        VolumeDifference.Content = _volumeDifference;
                        VolumeDifference.Content += "      " + _volumeHistory.Count.ToString();
                    }

                    if (_volumeRecovery != pastVolumeRecovery)
                    {
                        pastVolumeRecovery = _volumeRecovery;
                        if (_volumeRecovery)
                            ColorDetector.Background = Brushes.Red;
                        else
                            ColorDetector.Background = Brushes.Green;
                    }

                    if (VolumeChanger.IsChecked != null)
                        _volumeChangerIsActive = (bool)VolumeChanger.IsChecked;
                    else
                        _volumeChangerIsActive = false;

                    if (VolumeSensitivity.IsChecked != null)
                        _volumeHightSensitivityIsChecked = (bool)VolumeSensitivity.IsChecked;
                    else
                        _volumeHightSensitivityIsChecked = false;

                    if (ProtectType.IsChecked != null)
                        _detectBasedOnAverage = (bool)ProtectType.IsChecked;
                    else
                        _detectBasedOnAverage = false;

                    _permittedVolumeLevel = (float)SoundLimit.Value;
                });

                await Task.Delay(200);
            }
        }

        private async Task VolumeWatcher()
        {
            DateTime updateQueueDelay = DateTime.UtcNow;
            DateTime volumeDownTime = DateTime.UtcNow;
            var enumerator = new MMDeviceEnumerator();
            MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            string deviceName = device.FriendlyName;
            float newVolumeRecoveryScalar = 0;

            Dispatcher.Invoke(() => DeviceName.Content = deviceName);

            while (true)
            {
                _volume = device.AudioMeterInformation.MasterPeakValue;

                if (!_volumeRecovery)
                {
                    if (updateQueueDelay < DateTime.UtcNow)
                    {
                        _volumeHistory.Enqueue(_volume >= .01f ? _volume : .1f);
                        updateQueueDelay = DateTime.UtcNow.AddMilliseconds(20f);;
                    }

                    if (_volumeHistory.Count > _maxVolumeHistoryCount)
                        _volumeHistory.Dequeue();
                    else
                    {
                        await Task.Yield();
                        continue;
                    }
                }

                bool volumeAboveNormal = false;

                if (_detectBasedOnAverage)
                {
                    float volumeHistorySumm = 0;
                    foreach (float volumeHistoryValue in _volumeHistory)
                        volumeHistorySumm += volumeHistoryValue;

                    _volumeMean = volumeHistorySumm / _volumeHistory.Count;
                    _volumeDifference = (float)Math.Floor((_volume / _volumeMean) * 100);
                    volumeAboveNormal = _volumeDifference >= 500;
                }
                else
                    volumeAboveNormal = _volume >= _permittedVolumeLevel;

                if (_volumeRecovery)
                {
                    if (volumeDownTime > DateTime.UtcNow)
                        device.AudioEndpointVolume.MasterVolumeLevelScalar = newVolumeRecoveryScalar >= _targetVolumeScalar ? .001f : newVolumeRecoveryScalar;
                    else
                    {
                        if (volumeAboveNormal)
                            volumeDownTime = volumeDownTime.AddSeconds(1f);
                        else
                        {
                            float currentVolumeScalar = device.AudioEndpointVolume.MasterVolumeLevelScalar;
                            float newVolumeScalar = MoveTowards(currentVolumeScalar, _targetVolumeScalar, .001f);
                            device.AudioEndpointVolume.MasterVolumeLevelScalar = newVolumeScalar;

                            if (newVolumeScalar == _targetVolumeScalar)
                                _volumeRecovery = false;
                        }
                    }

                    await Task.Yield();
                    continue;
                }

                if (_volumeChangerIsActive && volumeAboveNormal && !_volumeRecovery)
                {
                    _penaltyPoints++;

                    if (_penaltyPoints > 10)
                    {
                        _targetVolumeScalar = device.AudioEndpointVolume.MasterVolumeLevelScalar;
                        newVolumeRecoveryScalar = _targetVolumeScalar / 2;
                        volumeDownTime = DateTime.UtcNow.AddSeconds(1);
                        _volumeRecovery = true;
                        _penaltyPoints = 0;
                    }

                    await Task.Yield();
                    continue;
                }
                else if (_penaltyPoints - 1 >= 0)
                    _penaltyPoints--;

                if (_volumeHightSensitivityIsChecked)
                    await Task.Yield();
                else
                    await Task.Delay(5);

                //if (_volume <= .0001f)
                //    await Task.Delay(1);
            }
        }

        private void OnCloseViaContextMenu(object sender, RoutedEventArgs e)
        {
            _realClose = true;
            this.Close();
        }
    }
}
