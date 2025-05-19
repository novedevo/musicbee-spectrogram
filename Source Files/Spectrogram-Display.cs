using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Timers;
using System.Windows.Forms;
using JetBrains.Annotations;

namespace MusicBeePlugin
{
    [UsedImplicitly]
    public partial class Plugin
    {
        #region Fields

        private readonly PluginInfo _about = new PluginInfo();

        private int _panelHeight;

        private float _lastPos;

        private bool _seekbar;

        /// <summary>
        /// Represents the minimum offset value used in the spectrogram display's seek bar calculations.
        /// This variable defines the starting point or padding for the seek bar rendering in the visual representation.
        /// Is non-zero when legend is enabled.
        /// </summary>
        private int _seekMin;

        // Declarations
        private MusicBeeApiInterface _mbApiInterface;

        private Control _panel;

        private System.Timers.Timer _timer;

        private readonly ToolTip _toolTip1 = new ToolTip();

        #endregion

        #region Properties

        private bool _debugMode { get; set; }
        private int _duration { get; set; }
        private bool _fileDeletion { get; set; }
        private string _hash { get; set; }
        private string _fileHash { get; set; }
        private string _imageDirectory { get; set; }
        private bool _legend { get; set; }
        private string _path { get; set; }
        private int _spectHeight { get; set; }
        private int _spectWidth { get; set; }
        private int _spectBuffer { get; set; }
        private string _workingDirectory { get; set; }

        #endregion

        #region Methods

        private static int CeilToNextPowerOfTwo(int number)
        {
            var a = number;
            var powOfTwo = 1;

            while (a > 1)
            {
                a >>= 1;
                powOfTwo <<= 1;
            }

            if (powOfTwo != number)
            {
                powOfTwo <<= 1;
            }

            return powOfTwo;
        }

        private static int RoundToTen(int i)
        {
            return (int)Math.Round(i / 10.0) * 10;
        }

        // Find Closest Power of Two to Determine Appropriate Height of Spectrogram
        private static int RoundToNextPowerOfTwo(int a)
        {
            var next = CeilToNextPowerOfTwo(a);
            var prev = next >> 1;
            return next - a <= a - prev ? next : prev;
        }

        // Disabled or Shutting Down
        [UsedImplicitly]
        public void Close(PluginCloseReason reason)
        {
        }

        // Configuration
        [UsedImplicitly]
        public bool Configure(IntPtr panelHandle)
        {
            var configWindow = new SpectrogramConfig(_workingDirectory);
            configWindow.ShowDialog();

            return true;
        }

        private void ConfigurePanel(object sender, EventArgs e)
        {
            var configWindow = new SpectrogramConfig(_workingDirectory);
            configWindow.ShowDialog();
            SaveSettings();
        }

        // Creates an MD5 hash of the settings file to determine whether it's been changed (so old images can be reused).
        private void CreateConfigHash()
        {
            using (var md5 = MD5.Create())
            {
                using (var stream = File.OpenRead(Path.Combine(_workingDirectory, "config.xml")))
                {
                    var temp = md5.ComputeHash(stream);
                    _hash = BitConverter.ToString(temp).Replace("-", "").ToLowerInvariant();
                }
            }
        }

        private void CreateFileHash()
        {
            using (var md5 = MD5.Create())
            {
                using (var stream = File.OpenRead(_mbApiInterface.NowPlaying_GetFileUrl()))
                {
                    var temp = md5.ComputeHash(stream);
                    _fileHash = BitConverter.ToString(temp).Replace("-", "").ToLowerInvariant();
                }
            }
        }

        // The duration of the current track. Used to determine if the file is a stream.
        private void CurrentDuration()
        {
            _duration = _mbApiInterface.NowPlaying_GetDuration();

            LogMessageToFile("Current Song Duration: " + _duration);
        }

        // The title of the current song, stripped down to the characters which can be used in a file-name.
        private string CurrentTitle()
        {
            CreateFileHash();
            _spectHeight = RoundToNextPowerOfTwo(_panel.Height);
            _spectWidth = RoundToTen(_panel.Width);
            var buffer = 141 * ((decimal)_spectWidth / (_spectWidth + 282));
            _spectBuffer = (int)buffer;
            var processedTitle = _fileHash + _spectHeight + _spectWidth;


            LogMessageToFile("Title: " + processedTitle);

            return processedTitle;
        }

        // The CLI Commands to be Sent to FFMPEG
        private string FfmpegArguments(string trackInput, string titleInput)
        {
            var configMgrRead = new ConfigMgr();
            var tempPath = Path.Combine(_workingDirectory, "config.xml");


            var cfg = configMgrRead.DeserializeConfig(tempPath);
            var showLegend = cfg.ShowLegend ? "enabled" : "disabled";

            var imagePath = Path.Combine(_imageDirectory, titleInput + _hash + ".png");
            var dimensions = _spectWidth + "x" + _spectHeight;
            var showSpectrumPicArgs = string.Join(":",
                $"showspectrumpic=s={dimensions}",
                cfg.ChannelMode,
                $"legend={showLegend}",
                $"saturation={cfg.Saturation}",
                $"color={cfg.ColorScheme}",
                $"scale={cfg.Scale}",
                $"win_func={cfg.WindowFunction}",
                $"gain={cfg.Gain}"
            );

            var arguments = string.Join(" ",
                "-i", trackInput,
                "-lavfi", showSpectrumPicArgs,
                "\"" + imagePath + "\""
            );

            LogMessageToFile("FFMPEG Arguments: " + arguments);

            return arguments;
        }

        // Sets location of Ffmpeg
        private string FfmpegPath()
        {
            string ffmpegPath;

            if (File.Exists(Path.Combine(_workingDirectory, "path.txt")))
            {
                ffmpegPath = File.ReadAllText(Path.Combine(_workingDirectory, "path.txt"));
                LogMessageToFile("FFMPEG Custom Path Set To: " + ffmpegPath);
            }
            else
            {
                ffmpegPath = Path.Combine(_workingDirectory, "ffmpeg");
            }

            return ffmpegPath;
        }

        // Add the Panel Header item for the Configuration Menu
        [UsedImplicitly]
        public List<ToolStripItem> GetMenuItems()
        {
            var list = new List<ToolStripItem>();
            var configure = new ToolStripMenuItem("Configure Spectrogram");

            configure.Click += ConfigurePanel;

            list.Add(configure);

            return list;
        }

        // Check if an image already exists for this song and configuration.
        private void ImgCheck()
        {
            LogMessageToFile("Get file path.");
            _path = Path.Combine(_imageDirectory, CurrentTitle() + _hash + ".png");
        }

        private void CheckFfmpegLocation()
        {
            // Debugging for the dependencies.
            if (!Directory.Exists(_workingDirectory))
            {
                MessageBox.Show(
                    string.Join("\n\n",
                        (
                            "Please copy the dependency folder here:",
                            _workingDirectory,
                            "NOTE: You MAY have to re-enable the add-in through Edit Preferences, " +
                            "AND remove then re-add it to the panel layout."
                        )));
                LogMessageToFile($"Dependencies not found at: {_workingDirectory}");
            }
            else if (!File.Exists(Path.Combine(_workingDirectory, "ffmpeg.exe")) &&
                     !File.Exists(Path.Combine(_workingDirectory, "path.txt")))
            {
                MessageBox.Show(
                    "Please manually edit or delete the 'path.txt' file, OR put ffmpeg.exe here: \n\n" +
                    "_workingDirectory");
                LogMessageToFile($"Path.txt not found at: {_workingDirectory}");
            }
        }

        private void InitializeFilesystem()
        {
            // Create log file.
            var logFile = Path.Combine(_workingDirectory, "MBSpectrogramLog.txt");
            if (File.Exists(logFile))
            {
                try
                {
                    File.Delete(logFile);
                }
                catch (IOException e)
                {
                    LogMessageToFile($"File Deletion error: {e.Message}");
                }
            }

            // If file deletion has been enabled, delete the saved images as soon as the plugin loads.
            if (_fileDeletion)
            {
                try
                {
                    Directory.Delete(_imageDirectory, true);
                    LogMessageToFile("Spectrogram Images Deleted.");
                }
                catch (IOException e)
                {
                    LogMessageToFile($"File Deletion error: {e.Message}");
                }
            }

            Directory.CreateDirectory(_imageDirectory);
        }

        // Initialization
        [UsedImplicitly]
        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            _mbApiInterface = new MusicBeeApiInterface();
            _mbApiInterface.Initialise(apiInterfacePtr);

            _workingDirectory = Path.Combine(_mbApiInterface.Setting_GetPersistentStoragePath(), "Dependencies");

            CheckFfmpegLocation();
            InitializeSettings();
            InitializeFilesystem();

            _about.PluginInfoVersion = PluginInfoVersion;
            _about.Name = "Spectrogram-Display";
            _about.Description = "This plugin displays the spectrogram of the song being played.";
            _about.Author = "zkhcohen";
            _about.Type = PluginType.PanelView;
            _about.VersionMajor = 1;
            _about.VersionMinor = 8;
            _about.Revision = 0;
            _about.MinInterfaceVersion = MinInterfaceVersion;
            _about.MinApiRevision = MinApiRevision;
            _about.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents | ReceiveNotificationFlags.TagEvents;
            _about.ConfigurationPanelHeight = 0;

            //Disables panel header and title. This is only useful for a small number of users...
            if (!File.Exists($"{_workingDirectory}noheader.txt"))
            {
                _about.TargetApplication = "Spectrogram";
            }
            else
            {
                LogMessageToFile("No header enabled.");
            }

            CurrentDuration();
            CreateConfigHash();

            return _about;
        }

        // Check if Spectrogram legend and debugging mode are enabled.
        private void InitializeSettings()
        {
            var configMgrLeg = new ConfigMgr();
            var tempPath = Path.Combine(_workingDirectory, "config.xml");
            var deserializedObject = configMgrLeg.DeserializeConfig(tempPath);
            _legend = deserializedObject.ShowLegend;
            _debugMode = deserializedObject.EnableDebugging;
            _fileDeletion = deserializedObject.ClearImages;
            _imageDirectory = Path.Combine(_workingDirectory, "Spectrogram_Images");
        }

        // Logging
        private void LogMessageToFile(string msg)
        {
            Console.WriteLine(msg);
            if (!_debugMode) return;
            var sw = File.AppendText(
                Path.Combine(_workingDirectory, "MBSpectrogramLog.txt"));
            try
            {
                var logLine = $"{DateTime.Now:G}: {msg}";
                sw.WriteLine(logLine);
            }
            finally
            {
                sw.Close();
            }
        }

        // GUI Settings
        [UsedImplicitly]
        public int OnDockablePanelCreated(Control panel)
        {
            // Set the Display Settings
            const float dpiScaling = 0;
            // 0 allows dynamic resizing. < 0 allows resizing and fitting to frame. > 0 is static.

            //Enable below if DPI-scaling is off on your display:
            //using (Graphics g = panel.CreateGraphics()) {
            //   dpiScaling = g.DpiY / 96f;
            //}


            // Draw the UI
            panel.Paint += DrawPanel;
            panel.Click += PanelClick;
            panel.MouseMove += PanelMouseMove;

            _panel = panel;
            _panelHeight = Convert.ToInt32(110 * dpiScaling); // was set to 50

            RenderToPanel();

            return _panelHeight;
        }

        private void RenderToPanel()
        {
            if (_duration > 0)
            {
                ImgCheck();

                // Set Seekbar Display
                if (File.Exists(Path.Combine(_workingDirectory, "seekbar.txt")))
                {
                    _seekbar = true;
                    _seekMin = _legend ? _spectBuffer : 0;

                    InitTimer();
                }
                else
                {
                    _seekbar = false;
                }

                //LogMessageToFile("Size: " + mbApiInterface.NowPlaying_GetFileProperty(FilePropertyType.Size));

                // If the Spectrogram Image for the Song that Just Started Playing Doesn't Exist, Create One (if it's not a stream: size "N/A").
                if (!File.Exists(_path))
                {
                    LogMessageToFile("Path: " + _path);
                    LogMessageToFile("Beginning generation of image.");
                    RunCmd();
                }
            }
            else
            {
                _path = null;
            }

            // Refresh the Panel.
            _panel.Invalidate();

            // Rebuild the Panel on Track Changes
            _panel.Paint += DrawPanel;
        }

        // Update or Generate Image When Track Changes
        [UsedImplicitly]
        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            if (type != NotificationType.TrackChanged) return;

            LogMessageToFile("\n\n\n Track changed.");
            CurrentDuration();
            _lastPos = 0;

            RenderToPanel();
        }

        // The Function for Triggering the Generation of Spectrogram Images
        private void RunCmd()
        {
            /*if (mbApiInterface.NowPlaying_GetFileProperty(FilePropertyType.Size) != "N/A")
            {*/

            // Start a Background ffmpeg Process with the Arguments we Feed it   
            var proc = new Process();
            proc.StartInfo.WorkingDirectory = _imageDirectory;
            proc.StartInfo.FileName = FfmpegPath();
            proc.StartInfo.Arguments =
                FfmpegArguments($@"""{_mbApiInterface.NowPlaying_GetFileUrl()}""", CurrentTitle());
            proc.StartInfo.CreateNoWindow = true;
            proc.StartInfo.RedirectStandardError = true;
            proc.StartInfo.UseShellExecute = false;

            if (!proc.Start())
            {
                MessageBox.Show("Ffmpeg didn't start properly.");
                LogMessageToFile("Ffmpeg didn't start properly.");
                return;
            }

            var reader = proc.StandardError;
            string line;
            while ((line = reader.ReadLine()) != null) Console.WriteLine(line);

            proc.Close();

            //SetLastPlayed();

            LogMessageToFile("Image generated.");
        }

        // Save Settings
        [UsedImplicitly]
        public void SaveSettings()
        {
            CreateConfigHash();
            InitializeSettings();
        }

        // Uninstall
        [UsedImplicitly]
        public void Uninstall()
        {
        }

        // Convert to Time
        private static string ConvTime(float ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);

            return ms > 3600000 ? t.ToString(@"%h\:mm\:ss") : t.ToString(@"%m\:ss");
        }

        // Draw Plugin Panel and Load Image
        private void DrawPanel(object sender, PaintEventArgs e)
        {
            _lastPos = 0;

            // Set Colors
            var bg = _panel.BackColor;
            e.Graphics.Clear(bg);

            // Load Spectrogram Image if it Exists Already
            if (File.Exists(_path))
            {
                LogMessageToFile("Image found.");
                var image = Image.FromFile(_path, true);

                if (_seekbar)
                {
                    image = new Bitmap(image, new Size(_panel.Width, _panel.Height - 10));

                    if (_legend)
                    {
                        var blackFill = new SolidBrush(Color.Black);
                        var rectLeft = new Rectangle(0, _panel.Height - 10, _spectBuffer, 10);
                        var rectRight = new Rectangle(_panel.Width - _spectBuffer, _panel.Height - 10,
                            _spectBuffer,
                            10);

                        e.Graphics.FillRectangle(blackFill, rectLeft);
                        e.Graphics.FillRectangle(blackFill, rectRight);
                        blackFill.Dispose();
                    }
                }
                else
                {
                    image = new Bitmap(image, new Size(_panel.Width, _panel.Height));
                }

                e.Graphics.DrawImage(image, new Point(0, 0));
            }
            else if (_duration <= 0)
            {
                var placeholder = Path.Combine(_workingDirectory, "placeholder.png");

                if (!File.Exists(placeholder)) return;

                LogMessageToFile("Image found.");
                var image = Image.FromFile(placeholder, true);
                image = new Bitmap(image, new Size(_panel.Width, _panel.Height));
                e.Graphics.DrawImage(image, new Point(0, 0));
            }
        }

        // Find Position of Cursor in Song / Panel
        private float FindPos()
        {
            var point = _panel.PointToClient(Cursor.Position);
            float currentPosX = point.X;

            float getRelativeLocation;
            float totalLength = _panel.Width;
            float totalTime = _duration;


            if (_legend)
            {
                if (currentPosX >= _spectBuffer && currentPosX <= totalLength - _spectBuffer)
                {
                    var adjustedLength = totalLength - 200;
                    getRelativeLocation = (currentPosX - _spectBuffer) / adjustedLength * totalTime;

                    return getRelativeLocation;
                }

                if (currentPosX < _spectBuffer)
                {
                    return 0;
                }

                return totalTime;
            }

            // Calculate Where in the Active Song you 'Clicked' (where you'd like to seek to)
            totalLength = _panel.Width;
            getRelativeLocation = currentPosX / totalLength * totalTime;


            // Set the Time in Milliseconds
            return getRelativeLocation;
        }

        // Start the Seekbar Timer
        private void InitTimer()
        {
            _timer = new System.Timers.Timer();
            _timer.Interval = 100;
            _timer.Elapsed += OnTime;
            _timer.Enabled = true;
        }

        // Draw the Seekbar on Timer Ticks
        private void OnTime(object sender, ElapsedEventArgs e)
        {
            if (!_seekbar)
            {
                LogMessageToFile("Timer disabled.");
                _timer.Stop();
                _timer.Dispose();
            }
            else
            {
                if (_panel.InvokeRequired)
                {
                    _panel.BeginInvoke((MethodInvoker)delegate
                    {
                        var myGraphics = _panel.CreateGraphics();
                        var blackFill = new SolidBrush(Color.Black);

                        var currentPosMs = _mbApiInterface.Player_GetPosition() - 400f;
                        float totalTime = _mbApiInterface.NowPlaying_GetDuration();
                        float totalLength = _panel.Width;

                        if (currentPosMs < _lastPos)
                        {
                            _panel.Invalidate();
                        }

                        _lastPos = currentPosMs;


                        var currentCompletionPx = currentPosMs / totalTime * (totalLength - _seekMin * 2);

                        var rect = new Rectangle(_seekMin, _panel.Height - 10, (int)currentCompletionPx, 10);
                        myGraphics.FillRectangle(blackFill, rect);

                        blackFill.Dispose();
                        myGraphics.Dispose();
                    });
                }
            }
        }

        // Panel Click Event (seekbar)
        private void PanelClick(object sender, EventArgs e)
        {
            var me = (MouseEventArgs)e;
            // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault - Intentional
            switch (me.Button)
            {
                case MouseButtons.Left:
                    _mbApiInterface.Player_SetPosition((int)Math.Round(FindPos()));
                    break;
                case MouseButtons.Right:
                    _mbApiInterface.Player_PlayPause();
                    break;
            }
        }

        // Set Tooltip to Show Time
        private void PanelMouseMove(object sender, EventArgs e)
        {
            if (_panel.InvokeRequired)
            {
                _panel.BeginInvoke((MethodInvoker)delegate
                {
                    _toolTip1.ShowAlways = true;
                    _toolTip1.SetToolTip(_panel, ConvTime(FindPos()));
                });
            }
            else
            {
                _toolTip1.ShowAlways = true;
                _toolTip1.SetToolTip(_panel, ConvTime(FindPos()));
            }
        }

        #endregion
    }
}