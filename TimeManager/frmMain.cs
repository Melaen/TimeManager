using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Threading.Timer;

namespace TimeManager
{
    public partial class frmMain : Form
    {
        #region Consts

        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_SCREENSAVE = 0xF140;
        private const int SPI_GETSCREENSAVERRUNNING = 0x0072;
        private const int TimerInterval = 1000;

        #endregion

        #region Variables

        private readonly Timer _timer;
        private readonly System.Windows.Forms.Timer _ssTimer = new System.Windows.Forms.Timer();
        private readonly TMDataContext _dc = new TMDataContext();
        private readonly List<Log> _windowsInfo = new List<Log>();
        private readonly string _userName;
        private Log _prevLog;

        private readonly List<string> _excludedTitles = new List<string> { "Program Manager", "TimeManager" };

        private readonly Dictionary<string, Regex> _regexps = new Dictionary<string, Regex>
        {
            ["Ssms.exe"] = new Regex("(?<=- ).*?(?= )", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            ["devenv.exe"] = new Regex("^.+(?= \\(Running\\)| \\(Debugging\\))|^.+(?= \\-)|(?<=\\- ).+$", RegexOptions.Compiled | RegexOptions.IgnoreCase)
        };

        #endregion

        #region Ctor

        public frmMain()
        {
            InitializeComponent();
            _timer = new Timer(Action);
            _timer.Change(5000, TimerInterval);
            _ssTimer.Interval = 1000;
            _ssTimer.Tick += SsTimerOnTick;
            _userName = Environment.UserName;
            tray.Text = Application.ProductName + " : " + Application.ProductVersion;
            SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;
            this.WriteLog("Application start. Start timer...");
        }

        #endregion

        #region Events

        private void SsTimerOnTick(object sender, EventArgs eventArgs)
        {
            var active = 1;
            SystemParametersInfo(SPI_GETSCREENSAVERRUNNING, 0, ref active, 0);

            if (active != 0) return;

            _ssTimer.Stop();
            this.WriteLog("Screensaver off. Start timer...");
            StartTimer();
        }

        private void frmMain_Load(object sender, EventArgs e)
        {
            Trace.WriteLine("Appstart...");
        }

        private void Action(object state)
        {
            Debug.WriteLine("Timer tick!");
            try
            {
                var log = GetActiveWindowInfo();

                if (_prevLog == null)
                {
                    _prevLog = log;
                    return;
                }

                if (log.WindowTitle != _prevLog.WindowTitle || log.ProcessFullName != _prevLog.ProcessFullName)
                {
                    if (string.IsNullOrEmpty(log.WindowTitle) || _excludedTitles.Contains(log.WindowTitle))
                    {
                        Debug.WriteLine("title is empty or in excluded list");
                        return;
                    }

                    if (_regexps.ContainsKey(log.ProcessName))
                    {
                        log.Code = _regexps[log.ProcessName].Match(log.WindowTitle).Value;
                    }
                    
                    CommitLastLog(log.StartDateTime);

                    _prevLog = log;
                }

                this.WriteLog(_prevLog.WindowTitle + " | " + _prevLog.ProcessFullName);
            }
            catch (Exception ex)
            {
                this.WriteLog(ex.ToString());
            }
        }

        private void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                    this.WriteLog("Session locked...");
                    StopTimer();
                    CommitLastLog();
                    break;
                case SessionSwitchReason.SessionUnlock:
                    this.WriteLog("Session unlocked...");
                    StartTimer();
                    break;
            }
        }

        private void tb_Log_TextChanged(object sender, EventArgs e)
        {
            if (tb_Log.Lines.Length > 500)
            {
                tb_Log.Clear();
            }
        }

        private void mi_Close_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void frmMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.WindowState = FormWindowState.Minimized;
                this.Hide();
                return;
            }

            CommitLastLog();
        }

        private void tray_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (this.WindowState == FormWindowState.Normal)
            {
                this.WindowState = FormWindowState.Minimized;
                this.Hide();
            }
            else if (this.WindowState == FormWindowState.Minimized)
            {
                this.Show();
                this.WindowState = FormWindowState.Normal;
            }
        }

        protected override void WndProc(ref Message m)
        {
            Console.WriteLine(m.ToString());
            if (m.Msg == WM_SYSCOMMAND && (m.WParam.ToInt32() & 0xfff0) == SC_SCREENSAVE)
            {
                _ssTimer.Start();
                this.WriteLog("Screensaver active. Stop timer...");
                StopTimer();
                CommitLastLog();
            }
            base.WndProc(ref m);
        }

        #endregion

        #region Functions

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(int action, int param, ref int retval, int updini);

        private void WriteLog(string text)
        {
            var log = DateTime.Now + " : " + text + "\r\n";
            if (tb_Log.InvokeRequired)
            {
                tb_Log.Invoke(new Action(() => tb_Log.AppendText(log)));
            }
            else
            {
                tb_Log.AppendText(log);
            }
            Debug.WriteLine(log);
        }

        private static Log GetActiveWindowInfo()
        {
            var result = new Log();
            const int nChars = 256;
            var title = new StringBuilder(nChars);
            var handle = GetForegroundWindow();

            if (GetWindowText(handle, title, nChars) > 0)
            {
                result.WindowTitle = title.ToString();
            }
            if (GetWindowThreadProcessId(handle, out uint pId) > 0)
            {
                result.ProcessFullName = Process.GetProcessById((int)pId).MainModule.FileName;
                result.ProcessName = Path.GetFileName(result.ProcessFullName);
            }
            result.StartDateTime = DateTime.Now;
            return result;
        }

        private void CommitLastLog(DateTime? dateTime = null)
        {
            if (!dateTime.HasValue)
            {
                dateTime = DateTime.Now;
            }

            _prevLog.EndDateTime = dateTime.Value;
            _prevLog.Username = _userName;
            _prevLog.Id = Guid.NewGuid();

            _dc.Logs.InsertOnSubmit(_prevLog);
            _dc.SubmitChanges();

            _prevLog = null;
        }

        private void StartTimer()
        {
            this.WriteLog("Start timer...");
            this._timer.Change(0, TimerInterval);
        }

        private void StopTimer()
        {
            this.WriteLog("Stop timer...");
            this._timer.Change(0, Timeout.Infinite);
        }

        #endregion
    }
}