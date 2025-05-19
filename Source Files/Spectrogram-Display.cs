using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
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

        private float _lastPos = 0;

        private bool _seekbar = false;

        private int _seekMin = 0;

        // Declarations
        private MusicBeeApiInterface _mbApiInterface;

        private Control _panel;

        private System.Timers.Timer _timer;

        private readonly ToolTip _toolTip1 = new ToolTip();

        #endregion

        #region Properties

        private static bool _debugMode { get; set; }

        private static int _duration { get; set; }

        private static bool _fileDeletion { get; set; }

        private static string _hash { get; set; }

        private static string _fileHash { get; set; }

        private static string _imageDirectory { get; set; }

        private static bool _legend { get; set; }

        private static string _path { get; set; }

        private static int _spectHeight { get; set; }

        private static int _spectWidth { get; set; }

        private static int _spectBuffer { get; set; }

        private static string _workingDirectory { get; set; }

        #endregion

        #region Methods

        private static int CeilToNextPowerOfTwo(int number)
        {
            int a = number;
            int powOfTwo = 1;

            while (a > 1)
            {
                a = a >> 1;
                powOfTwo = powOfTwo << 1;
            }

            if (powOfTwo != number)
            {
                powOfTwo = powOfTwo << 1;
            }

            return powOfTwo;
        }

        private static int RoundToTen(int i)
        {
            return ((int)Math.Round(i / 10.0)) * 10;
        }

        // Find Closest Power of Two to Determine Appropriate Height of Spectrogram
        private static int RoundToNextPowerOfTwo(int a)
        {
            int next = CeilToNextPowerOfTwo(a);
            int prev = next >> 1;
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
            SpectrogramConfig configWindow = new SpectrogramConfig(_workingDirectory);
            configWindow.ShowDialog();


            return true;
        }

        public void configurePanel(object sender, EventArgs e)
        {
            SpectrogramConfig configWindow = new SpectrogramConfig(_workingDirectory);
            configWindow.ShowDialog();
            SaveSettings();
        }

        // Creates an MD5 hash of the settings file to determine whether it's been changed (so old images can be reused).
        private void CreateConfigHash()
        {
            using (var md5 = MD5.Create())
            {
                using (var stream = File.OpenRead(_workingDirectory + "config.xml"))
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
            string processedTitle = _fileHash + _spectHeight + _spectWidth;


            LogMessageToFile("Title: " + processedTitle);

            return processedTitle;
        }

        // The CLI Commands to be Sent to FFMPEG
        private string FfmpegArguments(string trackInput, string titleInput)
        {
            ConfigMgr configMgrRead = new ConfigMgr();
            string tempPath = _workingDirectory + @"config.xml";


            var deserializedObject = configMgrRead.DeserializeConfig(tempPath);

            var ColorScheme = deserializedObject.ColorScheme;
            var Saturation = deserializedObject.Saturation;
            var Gain = deserializedObject.Gain;
            var WindowFunction = deserializedObject.WindowFunction;
            var ChannelMode = deserializedObject.ChannelMode;
            var Scale = deserializedObject.Scale;
            var ShowLegend = (deserializedObject.ShowLegend) ? "enabled" : "disabled";

            var arguments = (@"-i " + trackInput + " -lavfi showspectrumpic=s=" + _spectWidth + "x" + _spectHeight + ":"
                             + ChannelMode + ":legend=" + ShowLegend + ":saturation=" + Saturation +
                             ":color=" + ColorScheme + ":scale=" + Scale + ":win_func=" + WindowFunction +
                             ":gain=" + Gain + " " + @"""" + _imageDirectory + titleInput + _hash + @"""" + ".png");

            LogMessageToFile("FFMPEG Arguments: " + arguments);

            return arguments;
        }

        // Sets location of Ffmpeg
        private string FfmpegPath()
        {
            string ffmpegPath;

            if (File.Exists(_workingDirectory + @"path.txt"))
            {
                ffmpegPath = File.ReadAllText(_workingDirectory + @"path.txt");
                LogMessageToFile("FFMPEG Custom Path Set To: " + ffmpegPath);
            }
            else
            {
                ffmpegPath = _workingDirectory + "ffmpeg";
            }

            return ffmpegPath;
        }

        // Add the Panel Header item for the Configuration Menu
        [UsedImplicitly]
        public List<ToolStripItem> GetMenuItems()
        {
            List<ToolStripItem> list = new List<ToolStripItem>();
            ToolStripMenuItem configure = new ToolStripMenuItem("Configure Spectrogram");

            configure.Click += configurePanel;

            list.Add(configure);

            return list;
        }

        // Check if an image already exists for this song and configuration.
        private void ImgCheck()
        {
            LogMessageToFile("Get file path.");
            _path = _imageDirectory + CurrentTitle() + _hash + ".png";
        }

        // Initialization
        [UsedImplicitly]
        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            _mbApiInterface = new MusicBeeApiInterface();
            _mbApiInterface.Initialise(apiInterfacePtr);

            string wdTemp = _mbApiInterface.Setting_GetPersistentStoragePath() + @"Dependencies\";

            // Debugging for the dependencies.
            if (!Directory.Exists(wdTemp))
            {
                MessageBox.Show("Please copy the dependency folder here: \n\n" + wdTemp +
                                "\n\n" +
                                "NOTE: You MAY have to re-enable the add-in through Edit Preferences, AND remove then re-add it to the panel layout.");
                LogMessageToFile("Dependencies not found at: " + wdTemp);
            }
            else if (!File.Exists(wdTemp + "ffmpeg.exe") && !File.Exists(wdTemp + "path.txt"))
            {
                MessageBox.Show("Please manually edit or delete the 'path.txt' file, OR put ffmpeg.exe here: \n\n" +
                                wdTemp);
                LogMessageToFile("Path.txt not found at: " + wdTemp);
            }

            InitializeSettings();


            // Create log file.
            if (File.Exists(_workingDirectory + "MBSpectrogramLog.txt"))
            {
                try
                {
                    File.Delete(_workingDirectory + "MBSpectrogramLog.txt");
                }
                catch (System.IO.IOException e)
                {
                    LogMessageToFile("File Deletion error: " + e.Message);
                }
            }


            // If file deletion has been enabled, delete the saved images as soon as the plugin loads.
            if (_fileDeletion == true)
            {
                try
                {
                    Directory.Delete(_imageDirectory, true);
                    LogMessageToFile("Spectrogram Images Deleted.");
                }
                catch (System.IO.IOException e)
                {
                    LogMessageToFile("File Deletion error: " + e.Message);
                }
            }


            Directory.CreateDirectory(_imageDirectory);

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
            if (!File.Exists(_workingDirectory + "noheader.txt"))
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
            ConfigMgr configMgrLeg = new ConfigMgr();
            string tempPath = _mbApiInterface.Setting_GetPersistentStoragePath() + @"Dependencies\config.xml";
            var deserializedObject = configMgrLeg.DeserializeConfig(tempPath);
            _legend = deserializedObject.ShowLegend;
            _debugMode = deserializedObject.EnableDebugging;
            _fileDeletion = deserializedObject.ClearImages;
            _workingDirectory = _mbApiInterface.Setting_GetPersistentStoragePath() + @"Dependencies\";
            _imageDirectory = _mbApiInterface.Setting_GetPersistentStoragePath() + @"Dependencies\Spectrogram_Images\";
        }

        // Logging
        private void LogMessageToFile(string msg)
        {
            if (_debugMode == true)
            {
                System.IO.StreamWriter sw = System.IO.File.AppendText(
                    _workingDirectory + "MBSpectrogramLog.txt");
                try
                {
                    string logLine = System.String.Format(
                        "{0:G}: {1}", System.DateTime.Now, msg);
                    sw.WriteLine(logLine);
                }
                finally
                {
                    sw.Close();
                }
            }
        }

        // GUI Settings
        [UsedImplicitly]
        public int OnDockablePanelCreated(Control panel)
        {
            // Set the Display Settings
            float dpiScaling = 0; // 0 allows dynamic resizing. < 0 allows resizing and fitting to frame. > 0 is static.

            //Enable below if DPI-scaling is off on your display:
            //using (Graphics g = panel.CreateGraphics()) {
            //   dpiScaling = g.DpiY / 96f;
            //}


            // Draw the UI
            panel.Paint += DrawPanel;
            panel.Click += PanelClick;
            panel.MouseMove += PanelMouseMove;

            this._panel = panel;
            _panelHeight = Convert.ToInt32(110 * dpiScaling); // was set to 50


            return _panelHeight;
        }

        // Update or Generate Image When Track Changes
        [UsedImplicitly]
        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            switch (type)
            {
                case NotificationType.TrackChanged:

                    LogMessageToFile("\n\n\n Track changed.");

                    CurrentDuration();

                    _lastPos = 0;

                    if (_duration > 0)
                    {
                        ImgCheck();

                        // Set Seekbar Display
                        if (File.Exists(_workingDirectory + @"\seekbar.txt"))
                        {
                            _seekbar = true;

                            if (_legend == true)
                            {
                                _seekMin = _spectBuffer;
                            }
                            else
                            {
                                _seekMin = 0;
                            }

                            initTimer();
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
                    break;
            }
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
                FfmpegArguments(@"""" + _mbApiInterface.NowPlaying_GetFileUrl() + @"""", CurrentTitle());
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
        private string convTime(float ms)
        {
            TimeSpan t = TimeSpan.FromMilliseconds(ms);

            if (ms > 3600000)
            {
                string answer = string.Format("{0:D2}:{1:D2}:{2:D2}",
                    t.Hours,
                    t.Minutes,
                    t.Seconds);
                return answer;
            }
            else
            {
                string answer = string.Format("{0:D2}:{1:D2}",
                    t.Minutes,
                    t.Seconds);

                return answer;
            }
        }

        // Draw Plugin Panel and Load Image
        private void DrawPanel(object sender, PaintEventArgs e)
        {
            _lastPos = 0;

            // Set Colors
            var bg = _panel.BackColor;
            var text1 = _panel.ForeColor;
            var text2 = text1;
            var highlight = Color.FromArgb(2021216);
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
                        SolidBrush blackFill = new SolidBrush(Color.Black);
                        Rectangle rectLeft = new Rectangle(0, _panel.Height - 10, _spectBuffer, 10);
                        Rectangle rectRight = new Rectangle(_panel.Width - _spectBuffer, _panel.Height - 10, _spectBuffer,
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
                String Placeholder = _workingDirectory + @"placeholder.png";

                if (File.Exists(Placeholder))
                {
                    LogMessageToFile("Image found.");
                    var image = Image.FromFile(Placeholder, true);
                    image = new Bitmap(image, new Size(_panel.Width, _panel.Height));
                    e.Graphics.DrawImage(image, new Point(0, 0));
                }
            }
        }

        // Find Position of Cursor in Song / Panel
        private float findPos()
        {
            Point point = _panel.PointToClient(Cursor.Position);
            float currentPosX = point.X;

            float getRelativeLocation;
            float totalLength = this._panel.Width;
            float totalTime = _duration;


            if (_legend == true)
            {
                if ((currentPosX >= _spectBuffer && currentPosX <= (totalLength - _spectBuffer)))
                {
                    float adjustedLength = totalLength - 200;
                    getRelativeLocation = ((currentPosX - _spectBuffer) / adjustedLength) * totalTime;

                    return getRelativeLocation;
                }
                else if (currentPosX < _spectBuffer)
                {
                    return 0;
                }
                else
                {
                    return totalTime;
                }
            }
            else
            {
                // Calculate Where in the Active Song you 'Clicked' (where you'd like to seek to)
                totalLength = this._panel.Width;
                getRelativeLocation = (currentPosX / totalLength) * totalTime;


                // Set the Time in Milliseconds
                return getRelativeLocation;
            }
        }

        // Start the Seekbar Timer
        private void initTimer()
        {
            _timer = new System.Timers.Timer();
            _timer.Interval = 100;
            _timer.Elapsed += new ElapsedEventHandler(onTime);
            _timer.Enabled = true;
        }

        // Draw the Seekbar on Timer Ticks
        private void onTime(object sender, ElapsedEventArgs e)
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
                    _panel.BeginInvoke((MethodInvoker)delegate()
                    {
                        Graphics myGraphics = _panel.CreateGraphics();
                        SolidBrush blackFill = new SolidBrush(Color.Black);

                        float currentPos = _mbApiInterface.Player_GetPosition();
                        float totalTime = _mbApiInterface.NowPlaying_GetDuration();
                        float totalLength = this._panel.Width;

                        if (currentPos < _lastPos)
                        {
                            _panel.Invalidate();
                        }

                        _lastPos = currentPos;


                        float currentCompletion = (currentPos / totalTime) * (totalLength - (_seekMin * 2));

                        Rectangle rect = new Rectangle(_seekMin, _panel.Height - 10, (int)currentCompletion, 10);
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
            MouseEventArgs me = (MouseEventArgs)e;
            if (me.Button == System.Windows.Forms.MouseButtons.Left)
            {
                _mbApiInterface.Player_SetPosition((int)Math.Round(findPos()));
            }
            else if (me.Button == System.Windows.Forms.MouseButtons.Right)
            {
                _mbApiInterface.Player_PlayPause();
            }
        }

        // Set Tooltip to Show Time
        private void PanelMouseMove(object sender, EventArgs e)
        {
            if (_panel.InvokeRequired)
            {
                _panel.BeginInvoke((MethodInvoker)delegate()
                {
                    _toolTip1.ShowAlways = true;
                    _toolTip1.SetToolTip(_panel, convTime(findPos()));
                });
            }
            else
            {
                _toolTip1.ShowAlways = true;
                _toolTip1.SetToolTip(_panel, convTime(findPos()));
            }
        }

        #endregion
    }
}