using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO.Ports;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("SerialCom")]
[assembly: System.Reflection.AssemblyProduct("SerialCom")]
[assembly: System.Reflection.AssemblyDescription("SerialCom 串口助手")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("V1.0")]

namespace SerialCom
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MonitorWindow());
        }
    }

    sealed class MonitorWindow : Form
    {
        internal static readonly Color Blue = Color.FromArgb(53, 96, 230);
        internal static Color Edge { get { return Palette.Edge; } }
        static Color Ink { get { return Palette.Ink; } }
        static Color Muted { get { return Palette.Muted; } }
        const int DisplayLimit = 480000;
        readonly ComboBox ports = new ThemeCombo { Width = 108 };
        readonly ComboBox baud = new ThemeCombo { Width = 112, AccessibleName = "波特率" };
        readonly TextBox filter = new TextBox { BorderStyle = BorderStyle.None, Width = 200, AccessibleName = "筛选内容" };
        readonly TextBox collapseFilter = new TextBox { BorderStyle = BorderStyle.None, Width = 200, AccessibleName = "折叠内容" };
        readonly TextBox sendText = new TextBox { BorderStyle = BorderStyle.None, Width = 520, AccessibleName = "串口发送内容" };
        readonly LogButton connect = new LogButton("连接串口", true);
        readonly LogButton refresh = new LogButton("刷新串口", false);
        readonly LogButton settingsButton = new LogButton("串口设置", false);
        readonly LogButton send = new LogButton("发送", true);
        readonly CheckBox showTimestamps = new ThemeCheck { Text = "显示时间", Size = new Size(92, 34), Checked = true, Margin = new Padding(10, 0, 4, 0) };
        readonly CheckBox follow = new ThemeCheck { Text = "自动滚动", Size = new Size(104, 34), Checked = true, Margin = new Padding(10, 0, 4, 0) };
        readonly CheckBox hexDisplay = new ThemeCheck { Text = "HEX 显示", Size = new Size(100, 34) };
        readonly CheckBox hexSend = new ThemeCheck { Text = "HEX", Size = new Size(62, 34), AccessibleName = "HEX 发送" };
        readonly ComboBox sendEnding = new ThemeCombo { Width = 118, AccessibleName = "发送结束符", FlatStyle = FlatStyle.Flat };
        static readonly SendEnding[] EndingChoices = { SendEnding.None, SendEnding.CR, SendEnding.LF, SendEnding.CRLF, SendEnding.LFCR };
        SendEnding SelectedEnding { get { return sendEnding.SelectedIndex >= 0 ? EndingChoices[sendEnding.SelectedIndex] : SendEnding.None; } }
        readonly ToolTip hints = new ToolTip();
        readonly ContextMenuStrip logMenu = new ContextMenuStrip();
        readonly Label status = new Label();
        readonly Label count = new Label();
        readonly LogOutput output = new LogOutput { Dock = DockStyle.Fill, ReadOnly = true,
            BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(248, 250, 255), WordWrap = false,
            DetectUrls = false, HideSelection = false, ScrollBars = RichTextBoxScrollBars.Both };
        readonly Timer poll = new Timer { Interval = 60 };
        readonly Timer saveDelay = new Timer { Interval = 400 };
        readonly LogHistory history = new LogHistory();
        readonly LogHistory hexHistory = new LogHistory();
        Decoder receiveDecoder = Encoding.UTF8.GetDecoder();
        Encoding connectionEncoding = Encoding.UTF8;
        Encoding TextEncoding { get { return serial == null ? serialSettings.TextEncoding : connectionEncoding; } }
        readonly SessionLogArchive archive = new SessionLogArchive(StoragePaths.Root, () => DateTime.Now);
        string archiveError;
        LogHistory ViewHistory { get { return hexDisplay.Checked ? hexHistory : history; } }
        readonly SerialSettings serialSettings = SerialSettings.Load();
        SerialPort serial;
        int matchCount, hiddenMatches;
        bool statusError, changingSendMode;
        public MonitorWindow()
        {
            ApplyCacheBudget();
            Text = "SerialCom " + Application.ProductVersion;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 10);
            ClientSize = new Size(1060, 710);
            MinimumSize = new Size(870, 500);
            BackColor = Palette.Canvas;
            ForeColor = Ink;
            filter.Text = serialSettings.FilterText;
            collapseFilter.Text = serialSettings.CollapseText;
            sendText.Text = serialSettings.SendText;
            showTimestamps.Checked = serialSettings.ShowTime;
            follow.Checked = serialSettings.AutoScroll;
            hexDisplay.Checked = serialSettings.HexDisplay;
            hexSend.Checked = serialSettings.HexSend;
            sendEnding.Items.AddRange(new object[] { "不追加", "回车\\r", "换行\\n", "回车换行", "换行回车" });
            sendEnding.SelectedIndex = Array.IndexOf(EndingChoices, serialSettings.LineEnding);
            if (serialSettings.WindowWidth >= 854 && serialSettings.WindowHeight >= 461)
                ClientSize = new Size(serialSettings.WindowWidth, serialSettings.WindowHeight);
            if (serialSettings.Maximized) WindowState = FormWindowState.Maximized;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 3 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

            ports.FlatStyle = baud.FlatStyle = FlatStyle.Flat;
            PopulateBaudRates();
            layout.Controls.Add(Toolbar(Caption("串口"), ports, refresh, Caption("波特率"), baud, connect, settingsButton), 0, 0);
            var filterInput = new KeywordInput(filter, "请输入筛选内容，可以用|分割", serialSettings.FilterRules, RulesChanged);
            var collapseInput = new KeywordInput(collapseFilter, "请输入折叠内容，可以用|分割", serialSettings.CollapseRules, RulesChanged);
            hints.SetToolTip(filterInput, "请输入筛选内容，可以用|分割");
            hints.SetToolTip(filter, "请输入筛选内容，可以用|分割");
            hints.SetToolTip(collapseInput, "请输入折叠内容，可以用|分割");
            hints.SetToolTip(collapseFilter, "请输入折叠内容，可以用|分割");
            var sendInput = new CommandInput(sendText, () => serialSettings.SendHistory, SelectCommand, DeleteCommand);
            layout.Controls.Add(SendToolbar(sendInput, hexSend, sendEnding, send), 0, 2);
            hints.SetToolTip(hexSend, "按原始字节发送。示例：01 02 FF 或 0102FF；结束符由右侧下拉框决定。");
            hints.SetToolTip(sendEnding, "不追加：不发送结束符；回车：0D；换行：0A；回车换行：0D 0A；换行回车：0A 0D。文本与 HEX 模式均适用。");
            hints.SetToolTip(hexDisplay, "显示原始收发字节，每行最多 16 字节，保留 00、回车和换行等字节。");
            hints.SetToolTip(sendInput, "文本模式使用串口设置中的编码（UTF-8 / GBK）；HEX 模式按字节发送。连接期间修改编码在下次连接生效。");

            var console = new LogCard { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(16, 8, 16, 8) };
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Palette.Surface, ColumnCount = 1, RowCount = 4 };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            status.Dock = count.Dock = DockStyle.Fill;
            status.TextAlign = count.TextAlign = ContentAlignment.MiddleLeft;
            status.AutoEllipsis = true;
            count.ForeColor = Muted;
            output.ForeColor = Ink;

            string selectedLogText = "";
            var copyLog = new ToolStripMenuItem("复制") { ShortcutKeyDisplayString = "Ctrl+C" };
            var clearLog = new ToolStripMenuItem("清空日志") { ToolTipText = "仅清空界面缓存，不删除本地原始日志。" };
            logMenu.Items.Add(copyLog);
            logMenu.Items.Add(clearLog);
            clearLog.Click += (s, e) => { history.Clear(); hexHistory.Clear(); receiveDecoder.Reset(); RebuildView(); };
            logMenu.Opening += (s, e) =>
            {
                selectedLogText = output.SelectedText;
                copyLog.Enabled = selectedLogText.Length > 0;
            };
            copyLog.Click += (s, e) =>
            {
                if (selectedLogText.Length == 0) return;
                try { Clipboard.SetText(selectedLogText); }
                catch (ExternalException) { SetStatus("复制失败：剪贴板正被其他程序占用，请重试。", true); }
            };
            output.ContextMenuStrip = logMenu;
            output.Font = new Font("Consolas", 11);
            body.Controls.Add(ToolbarRow(filterInput, collapseInput, hexDisplay, showTimestamps, follow), 0, 0);
            body.Controls.Add(status, 0, 1);
            body.Controls.Add(output, 0, 2);
            body.Controls.Add(count, 0, 3);
            console.Controls.Add(body);
            layout.Controls.Add(console, 0, 1);
            Controls.Add(layout);
            ApplyColors(this, Palette.Canvas);
            output.BackColor = Palette.Input;
            output.ForeColor = Ink;

            refresh.Click += (s, e) => ScanPorts();
            settingsButton.Click += (s, e) => ShowSerialSettings();
            connect.Click += (s, e) => { if (serial == null) OpenPort(); else ClosePort("已断开"); };
            send.Click += (s, e) => SendText();
            sendText.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    SendText();
                }
            };
            ports.SelectedIndexChanged += (s, e) => UpdateControls();
            filter.TextChanged += (s, e) => RebuildView();
            collapseFilter.TextChanged += (s, e) => RebuildView();
            showTimestamps.CheckedChanged += (s, e) => RebuildView();
            hexDisplay.CheckedChanged += (s, e) => RebuildView();
            follow.CheckedChanged += (s, e) => { if (follow.Checked) ScrollToEnd(); };
            output.ScrolledAway += (s, e) =>
            {
                if (follow.Checked) follow.Checked = false;
            };
            output.MouseWheel += (s, e) => output.CheckScrollPosition();
            output.KeyUp += (s, e) => output.CheckScrollPosition();
            poll.Tick += (s, e) => ReadPort();
            saveDelay.Tick += (s, e) => SaveView();
            FormClosing += (s, e) => { ClosePort("已断开"); SaveView(); };
            FormClosed += (s, e) => { archive.Dispose(); saveDelay.Dispose(); poll.Dispose(); hints.Dispose(); logMenu.Dispose(); };
            ScanPorts();
            UpdateCount();
            filter.TextChanged += QueueSave;
            collapseFilter.TextChanged += QueueSave;
            sendText.TextChanged += QueueSave;
            showTimestamps.CheckedChanged += QueueSave;
            follow.CheckedChanged += QueueSave;
            hexDisplay.CheckedChanged += QueueSave;
            hexSend.CheckedChanged += ChangeSendMode;
            hexSend.CheckedChanged += QueueSave;
            sendEnding.SelectedIndexChanged += QueueSave;
            ports.SelectedIndexChanged += (s, e) => { if (ports.SelectedItem != null) QueueSave(s, e); };
            baud.SelectedIndexChanged += QueueSave;
            SizeChanged += QueueSave;
            Shown += (s, e) => SaveView();
            hints.SetToolTip(count, "RAM 档位为文本与 HEX 历史缓存的合计估算预算，各占一半；不含界面、临时对象及程序自身内存。原始日志持续保存，不设上限。\n" + StoragePaths.Root);
        }

        void QueueSave(object sender, EventArgs e) { saveDelay.Stop(); saveDelay.Start(); }

        void RulesChanged() { RebuildView(); SaveView(); }

        void ChangeSendMode(object sender, EventArgs e)
        {
            if (changingSendMode) return;
            string converted, error;
            if (SerialData.TryConvertInput(sendText.Text, hexSend.Checked, TextEncoding, out converted, out error))
            {
                sendText.Text = converted;
                sendText.SelectionStart = sendText.TextLength;
            }
            else
            {
                changingSendMode = true;
                try { hexSend.Checked = !hexSend.Checked; }
                finally { changingSendMode = false; }
                SetStatus(error, true);
            }
        }

        void SelectCommand(SentCommand command)
        {
            changingSendMode = true;
            try { hexSend.Checked = command.Hex; sendText.Text = command.Text; sendEnding.SelectedIndex = Array.IndexOf(EndingChoices, command.Ending); }
            finally { changingSendMode = false; }
            sendText.Focus();
            sendText.SelectionStart = sendText.TextLength;
            QueueSave(this, EventArgs.Empty);
        }

        void RememberSentCommand(string text, bool hex)
        {
            serialSettings.RememberCommand(text, hex, SelectedEnding);
            SaveView();
        }

        void DeleteCommand(SentCommand command)
        {
            if (serialSettings.SendHistory.Remove(command)) SaveView();
        }

        void CaptureView()
        {
            serialSettings.FilterText = filter.Text;
            serialSettings.CollapseText = collapseFilter.Text;
            KeywordRules.Sync(serialSettings.FilterRules, filter.Text);
            KeywordRules.Sync(serialSettings.CollapseRules, collapseFilter.Text);
            serialSettings.SendText = sendText.Text;
            serialSettings.ShowTime = showTimestamps.Checked;
            serialSettings.AutoScroll = follow.Checked;
            serialSettings.HexDisplay = hexDisplay.Checked;
            serialSettings.HexSend = hexSend.Checked;
            serialSettings.LineEnding = SelectedEnding;
            if (ports.SelectedItem != null) serialSettings.PortName = (string)ports.SelectedItem;
            if (baud.SelectedItem != null) serialSettings.CurrentBaud = (int)baud.SelectedItem;
            if (WindowState == FormWindowState.Normal)
            {
                serialSettings.WindowWidth = ClientSize.Width;
                serialSettings.WindowHeight = ClientSize.Height;
            }
            if (WindowState != FormWindowState.Minimized)
                serialSettings.Maximized = WindowState == FormWindowState.Maximized;
        }

        void SaveView()
        {
            saveDelay.Stop();
            CaptureView();
            try { serialSettings.Save(); }
            catch (Exception error) { SetStatus("配置保存失败：" + error.Message, true); }
        }

        internal static void ApplyColors(Control parent, Color background)
        {
            foreach (Control child in parent.Controls)
            {
                Color fill = child is LogCard ? Palette.Surface :
                    child is ThemeInput || child is TextBox || child is ComboBox || child is RichTextBox
                        ? Palette.Input : background;
                child.BackColor = fill;
                child.ForeColor = child is Label && child.Font.Size < 20 ? Muted : Ink;
                ApplyColors(child, fill);
            }
        }

        static Control Toolbar(params Control[] controls)
        {
            var card = new LogCard { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(12, 2, 12, 2) };
            card.Controls.Add(ToolbarRow(controls));
            return card;
        }

        static Control ToolbarRow(params Control[] controls)
        {
            bool hasInputs = controls.Any(control => control is ThemeInput);
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = controls.Length + (hasInputs ? 0 : 1), RowCount = 1 };
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            int column = 0;
            foreach (Control control in controls)
            {
                // Let the empty column absorb spare width, keeping actions at the right edge.
                if (!hasInputs && column == controls.Length - 2)
                {
                    row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                    column++;
                }
                control.Anchor = control is ThemeInput ? AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.None;
                control.Margin = new Padding(4, 0, 4, 0);
                row.ColumnStyles.Add(control is ThemeInput
                    ? new ColumnStyle(SizeType.Percent, 50)
                    : new ColumnStyle(SizeType.AutoSize));
                row.Controls.Add(control, column++, 0);
            }
            return row;
        }

        static Control SendToolbar(Control input, Control mode, Control ending, Control sendButton)
        {
            var card = new LogCard { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(12, 2, 12, 2) };
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 4, RowCount = 1 };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            input.Margin = new Padding(4, 0, 12, 0);
            sendButton.Anchor = AnchorStyles.None;
            sendButton.Margin = new Padding(4, 0, 4, 0);
            row.Controls.Add(input, 0, 0);
            mode.Anchor = AnchorStyles.None;
            mode.Margin = new Padding(4, 0, 8, 0);
            row.Controls.Add(mode, 1, 0);
            ending.Anchor = AnchorStyles.None;
            ending.Margin = new Padding(4, 0, 8, 0);
            row.Controls.Add(ending, 2, 0);
            row.Controls.Add(sendButton, 3, 0);
            card.Controls.Add(row);
            return card;
        }

        static Label Caption(string text)
        {
            return new Label { Text = text, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted };
        }

        void ScanPorts()
        {
            string previous = ports.SelectedItem as string ?? serialSettings.PortName;
            ports.Items.Clear();
            try
            {
                string[] names = SerialPort.GetPortNames().OrderBy(p => p.Length).ThenBy(p => p).ToArray();
                ports.Items.AddRange(names);
                if (names.Contains(previous)) ports.SelectedItem = previous;
                else if (names.Contains("COM30")) ports.SelectedItem = "COM30";
                else if (names.Length == 1) ports.SelectedIndex = 0;
                SetStatus(names.Length == 0 ? "未发现串口，请连接设备后刷新。" : "选择串口后连接。", false);
            }
            catch (Exception error) { SetStatus(error.Message, true); }
            UpdateControls();
        }

        void PopulateBaudRates()
        {
            baud.Items.Clear();
            foreach (int rate in serialSettings.BaudPresets) baud.Items.Add(rate);
            int selected = serialSettings.CurrentBaud;
            if (serialSettings.BaudPresets.Contains(selected))
                baud.SelectedItem = selected;
            else if (baud.Items.Count > 0) baud.SelectedIndex = 0;
        }

        void ShowSerialSettings()
        {
            using (var dialog = new SerialSettingsDialog(serialSettings))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    CaptureView();
                    dialog.Settings.CopyViewFrom(serialSettings);
                    dialog.Settings.NormalizeBaudSelection();
                    dialog.Settings.Save();
                    serialSettings.CopyFrom(dialog.Settings);
                    serialSettings.CurrentBaud = dialog.Settings.CurrentBaud;
                    ApplyCacheBudget();
                    RebuildView();
                }
                catch (Exception error)
                {
                    MessageBox.Show(this, "设置保存失败，程序不会重启：" + error.Message, "保存设置失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            PopulateBaudRates();
        }

        void OpenPort()
        {
            if (ports.SelectedItem == null) return;
            if (baud.SelectedItem == null) return;
            int baudRate = (int)baud.SelectedItem;
            string name = (string)ports.SelectedItem;
            var next = new SerialPort(name, baudRate, serialSettings.Parity, serialSettings.DataBits, serialSettings.StopBits)
            {
                Encoding = serialSettings.TextEncoding, Handshake = serialSettings.Handshake, DtrEnable = false, RtsEnable = false,
                ReadBufferSize = 65536
            };
            try
            {
                next.Open();
                PrepareArchive();
                connectionEncoding = serialSettings.TextEncoding;
                receiveDecoder = connectionEncoding.GetDecoder();
                serial = next;
                poll.Start();
                SetStatus("已连接  " + name + "  ·  " + baudRate + "  ·  " + serialSettings.DataBits +
                    ParityLabel(serialSettings.Parity) + StopBitsLabel(serialSettings.StopBits) +
                    "  ·  " + HandshakeLabel(serialSettings.Handshake) + "  ·  " + serialSettings.TextEncodingName, false);
            }
            catch (Exception error)
            {
                next.Dispose();
                SetStatus("无法打开 " + name + "。请检查连接或关闭占用串口的程序。 " + error.Message, true);
            }
            UpdateControls();
        }

        static string ParityLabel(Parity parity)
        {
            switch (parity)
            {
                case Parity.Even: return "E";
                case Parity.Odd: return "O";
                case Parity.Mark: return "M";
                case Parity.Space: return "S";
                default: return "N";
            }
        }

        static string StopBitsLabel(StopBits stopBits)
        {
            return stopBits == StopBits.One ? "1" : stopBits == StopBits.Two ? "2" : "1.5";
        }

        static string HandshakeLabel(Handshake handshake)
        {
            switch (handshake)
            {
                case Handshake.XOnXOff: return "XON/XOFF";
                case Handshake.RequestToSend: return "RTS/CTS";
                case Handshake.RequestToSendXOnXOff: return "RTS/CTS + XON/XOFF";
                default: return "无流控";
            }
        }

        void SendText()
        {
            string text = sendText.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            byte[] payload;
            string parseError;
            if (!SerialData.TryBuildPayload(text, hexSend.Checked, SelectedEnding, TextEncoding, out payload, out parseError))
            {
                SetStatus(parseError, true);
                return;
            }
            if (serial == null)
            {
                SetStatus("请先连接串口，再发送数据。", true);
                return;
            }

            try
            {
                serial.Write(payload, 0, payload.Length);
                RememberSentCommand(text, hexSend.Checked);
                AddSentLine(hexSend.Checked ? "[HEX] " + SerialData.ToHex(payload) : text);
                RecordHex(payload, "<-");
            }
            catch (Exception error)
            {
                ClosePort("串口发送失败。 " + error.Message, true);
            }
        }

        void AddSentLine(string text)
        {
            var line = new LogLine(text, DateTime.Now, "<-");
            UpdateHistory(history, (added, removed) => history.Add(line, added, removed));
        }

        void ReadPort()
        {
            try
            {
                int available = Math.Min(serial.BytesToRead, 65536);
                if (available == 0) return;
                byte[] bytes = new byte[available];
                int read = serial.Read(bytes, 0, bytes.Length);
                if (read != bytes.Length) Array.Resize(ref bytes, read);
                ReceiveBytes(bytes);
            }
            catch (Exception error) { ClosePort("串口读取中断。 " + error.Message, true); }
        }

        void ClosePort(string message, bool error = false)
        {
            poll.Stop();
            if (serial != null)
            {
                char[] remaining = new char[4];
                int count = receiveDecoder.GetChars(new byte[0], 0, 0, remaining, 0, true);
                if (count > 0) Receive(new string(remaining, 0, count));
                Receive("\n");
                SerialPort closing = serial;
                serial = null;
                try { closing.Dispose(); }
                catch (Exception closeError) { message += " " + closeError.Message; error = true; }
            }
            try { archive.Dispose(); }
            catch (Exception closeError) { archiveError = closeError.Message; message += " 日志关闭失败：" + closeError.Message; error = true; }
            UpdateCount();
            SetStatus(message, error);
            UpdateControls();
        }

        void Receive(string chunk)
        {
            UpdateHistory(history, (added, removed) => history.Accept(chunk, "->", added, removed));
        }

        void ReceiveBytes(byte[] bytes)
        {
            SaveRaw(bytes);
            RecordHex(bytes, "->");
            char[] chars = new char[connectionEncoding.GetMaxCharCount(bytes.Length)];
            int decoded = receiveDecoder.GetChars(bytes, 0, bytes.Length, chars, 0, false);
            if (decoded > 0) Receive(new string(chars, 0, decoded));
        }

        void RecordHex(byte[] bytes, string direction)
        {
            DateTime timestamp = DateTime.Now;
            UpdateHistory(hexHistory, (added, removed) =>
            {
                for (int offset = 0; offset < bytes.Length; offset += 16)
                    hexHistory.Add(new LogLine(BitConverter.ToString(bytes, offset, Math.Min(16, bytes.Length - offset)).Replace('-', ' '),
                        timestamp, direction), added, removed);
            });
        }

        void UpdateHistory(LogHistory target, Action<Action<LogLine>, Action<LogLine>> update)
        {
            if (target != ViewHistory) { update(line => { }, line => { }); return; }
            var added = new StringBuilder();
            string[] query = KeywordRules.Parse(filter.Text);
            string[] collapse = KeywordRules.Parse(collapseFilter.Text);
            int evictedMatches = 0;
            update(line =>
            {
                if (PassesFilter(line.Text, query, collapse))
                {
                    matchCount++;
                    added.AppendLine(FormatLogLine(line));
                }
            }, line => { if (PassesFilter(line.Text, query, collapse)) evictedMatches++; });
            if (evictedMatches > hiddenMatches)
            {
                RebuildView();
                return;
            }
            hiddenMatches -= evictedMatches;
            matchCount -= evictedMatches;
            if (output.TextLength + added.Length > DisplayLimit)
            {
                RebuildView();
                return;
            }
            if (added.Length > 0) output.AppendLogText(added.ToString(), follow.Checked && output.SelectionLength == 0);
            UpdateCount();
        }

        void RebuildView()
        {
            string[] query = KeywordRules.Parse(filter.Text);
            string[] collapse = KeywordRules.Parse(collapseFilter.Text);
            var matching = ViewHistory.Lines.Where(line => PassesFilter(line.Text, query, collapse)).ToList();
            matchCount = matching.Count;
            int first = matchCount;
            int chars = 0;
            while (first > 0)
            {
                int length = FormatLogLine(matching[first - 1]).Length + Environment.NewLine.Length;
                if (chars + length > DisplayLimit * 3 / 4) break;
                chars += length;
                first--;
            }
            hiddenMatches = first;
            output.ReplaceLogText(first == matchCount ? "" : string.Join(Environment.NewLine, matching.Skip(first).Select(FormatLogLine)) + Environment.NewLine, follow.Checked);
            UpdateCount();
        }

        bool PassesFilter(string line, string[] query, string[] collapse)
        {
            StringComparison comparison = hexDisplay.Checked ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return (query.Length == 0 || query.Any(word => line.IndexOf(word, comparison) >= 0)) &&
                !collapse.Any(word => line.IndexOf(word, comparison) >= 0);
        }

        string FormatLogLine(LogLine line)
        {
            string prefix = showTimestamps.Checked
                ? "[" + line.Timestamp.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + "]"
                : "";
            return prefix + line.Direction + " " + line.Text;
        }

        void ScrollToEnd()
        {
            output.Select(output.TextLength, 0);
            output.ScrollToCaret();
        }

        void UpdateControls()
        {
            bool idle = serial == null;
            ports.Enabled = baud.Enabled = refresh.Enabled = settingsButton.Enabled = idle;
            send.Enabled = !idle;
            connect.Enabled = !idle || ports.SelectedItem != null;
            connect.Text = idle ? "连接串口" : "断开串口";
        }

        void SetStatus(string message, bool error)
        {
            statusError = error;
            status.ForeColor = error ? Palette.Error : Muted;
            status.Text = message;
        }

        void UpdateCount()
        {
            count.Text = "显示 " + (matchCount - hiddenMatches).ToString("N0") + " / 筛选后 " + matchCount.ToString("N0") +
                " / 缓存 " + ViewHistory.Count.ToString("N0") + " 行 · RAM≈" +
                ((history.EstimatedBytes + hexHistory.EstimatedBytes) / 1048576.0).ToString("F1") + "/" + serialSettings.LogRamMB + " MB · " +
                (archiveError == null ? "原始日志自动保存（无上限）" : "日志保存失败：" + archiveError);
            count.ForeColor = archiveError == null ? Muted : Palette.Error;
        }

        void PrepareArchive()
        {
            try { archive.StartSession(); archiveError = null; UpdateCount(); }
            catch (Exception error) { throw ArchiveFailure(error); }
        }

        void SaveRaw(byte[] bytes)
        {
            try { archive.Write(bytes); }
            catch (Exception error) { throw ArchiveFailure(error); }
        }

        IOException ArchiveFailure(Exception error)
        {
            archiveError = error.Message;
            UpdateCount();
            return new IOException("原始日志保存失败，已停止通信，请检查磁盘空间和目录权限：" + error.Message, error);
        }

        void ApplyCacheBudget()
        {
            long perHistory = serialSettings.LogRamMB * 1048576L / 2;
            history.SetBudget(perHistory);
            hexHistory.SetBudget(perHistory);
        }
    }

    static class StoragePaths
    {
        internal static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SerialCom");
    }

    // Each successful port connection fixes its folder and files until disconnect.
    // Persist raw bytes independently of display decoding, filtering or trimming.
    sealed class SessionLogArchive : IDisposable
    {
        readonly string root;
        readonly Func<DateTime> clock;
        FileStream received;

        internal SessionLogArchive(string root, Func<DateTime> clock)
        {
            this.root = root;
            this.clock = clock;
        }

        internal void StartSession()
        {
            Dispose();
            DateTime opened = clock();
            string session = opened.ToString("HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string folder = Path.Combine(root, opened.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            try
            {
                received = Open(Path.Combine(folder, session + ".log"));
            }
            catch { Dispose(); throw; }
        }

        static FileStream Open(string path)
        {
            return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        }

        internal void Write(byte[] bytes)
        {
            if (received == null) throw new IOException("日志会话尚未打开。");
            received.Write(bytes, 0, bytes.Length);
            // Flush each received batch, including incomplete lines, to disk.
            received.Flush(true);
        }

        public void Dispose()
        {
            FileStream rx = received;
            received = null;
            if (rx != null) rx.Dispose();
        }
    }

    sealed class KeywordRule
    {
        public readonly string Text;
        public bool Enabled;
        public KeywordRule(string text, bool enabled) { Text = text; Enabled = enabled; }
    }

    static class KeywordRules
    {
        public static string[] Parse(string text)
        {
            return text.Split(new[] { '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.Trim()).Where(word => word.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        }

        public static string Expression(IEnumerable<KeywordRule> rules)
        {
            return string.Join(" | ", rules.Where(rule => rule.Enabled).Select(rule => rule.Text));
        }

        public static void Sync(List<KeywordRule> rules, string expression)
        {
            string[] active = Parse(expression);
            rules.RemoveAll(rule => rule.Enabled && !active.Contains(rule.Text));
            foreach (string text in active)
            {
                KeywordRule rule = rules.FirstOrDefault(item => item.Text == text);
                if (rule == null) rules.Add(new KeywordRule(text, true));
                else rule.Enabled = true;
            }
        }

    }

    enum SendEnding { None, CR, CRLF, LF, LFCR }

    static class SerialData
    {
        public static string ToHex(byte[] bytes) { return BitConverter.ToString(bytes).Replace('-', ' '); }

        public static bool TryBuildPayload(string text, bool hex, SendEnding ending, out byte[] bytes, out string error)
        {
            return TryBuildPayload(text, hex, ending, Encoding.UTF8, out bytes, out error);
        }

        public static bool TryBuildPayload(string text, bool hex, SendEnding ending, Encoding encoding, out byte[] bytes, out string error)
        {
            error = null;
            bytes = null;
            if (!Enum.IsDefined(typeof(SendEnding), ending)) { error = "请选择有效的发送结束符。"; return false; }
            if (hex)
            {
                if (!TryBuildPayload(text, true, out bytes, out error)) return false;
            }
            else
            {
                try { bytes = StrictEncoding(encoding).GetBytes(text); }
                catch (EncoderFallbackException) { error = "内容包含当前编码无法表示的字符，请更换编码或修改内容。"; return false; }
            }
            int length = bytes.Length;
            if (ending != SendEnding.None)
            {
                Array.Resize(ref bytes, length + (ending == SendEnding.CR || ending == SendEnding.LF ? 1 : 2));
                bytes[length] = ending == SendEnding.LF || ending == SendEnding.LFCR ? (byte)0x0A : (byte)0x0D;
                if (ending == SendEnding.CRLF) bytes[length + 1] = 0x0A;
                if (ending == SendEnding.LFCR) bytes[length + 1] = 0x0D;
            }
            return true;
        }

        public static bool TryConvertInput(string text, bool toHex, out string converted, out string error)
        {
            return TryConvertInput(text, toHex, Encoding.UTF8, out converted, out error);
        }

        static Encoding StrictEncoding(Encoding encoding)
        {
            return Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }

        public static bool TryConvertInput(string text, bool toHex, Encoding encoding, out string converted, out string error)
        {
            converted = text;
            error = null;
            if (text.Length == 0) return true;
            if (toHex)
            {
                byte[] encoded;
                if (!TryBuildPayload(text, false, SendEnding.None, encoding, out encoded, out error)) return false;
                converted = ToHex(encoded);
                return true;
            }
            byte[] bytes;
            if (!TryBuildPayload(text, true, out bytes, out error)) return false;
            try
            {
                string decoded = StrictEncoding(encoding).GetString(bytes);
                if (decoded.Any(char.IsControl))
                {
                    error = "内容含有换行或不可显示的控制字节，已保持 HEX 模式，避免内容丢失。";
                    return false;
                }
                converted = decoded;
                return true;
            }
            catch (DecoderFallbackException)
            {
                error = "这些字节不是当前编码的有效文本，已保持 HEX 模式，避免内容丢失。";
                return false;
            }
        }

        public static bool TryBuildPayload(string text, bool hex, out byte[] bytes, out string error)
        {
            bytes = null;
            error = null;
            if (!hex) { bytes = Encoding.UTF8.GetBytes(text + "\r\n"); return true; }
            if (string.IsNullOrWhiteSpace(text)) { error = "请输入 HEX 字节，例如 01 02 FF。"; return false; }
            var result = new List<byte>();
            foreach (string part in Regex.Split(text.Trim(), @"\s+"))
            {
                if (part.Length % 2 != 0)
                {
                    error = "HEX 格式错误：每个字节需要两位，例如 01 02 FF 或 0102FF。";
                    return false;
                }
                for (int i = 0; i < part.Length; i += 2)
                {
                    byte value;
                    if (!byte.TryParse(part.Substring(i, 2), System.Globalization.NumberStyles.AllowHexSpecifier,
                        System.Globalization.CultureInfo.InvariantCulture, out value))
                    {
                        error = "HEX 格式错误：仅支持 0–9、A–F 和空白分隔符。";
                        return false;
                    }
                    result.Add(value);
                }
            }
            bytes = result.ToArray();
            return true;
        }
    }

    sealed class SentCommand
    {
        public readonly string Text;
        public readonly bool Hex;
        public readonly SendEnding Ending;
        public SentCommand(string text, bool hex) : this(text, hex, hex ? SendEnding.None : SendEnding.CRLF) { }
        public SentCommand(string text, bool hex, SendEnding ending) { Text = text; Hex = hex; Ending = ending; }
    }

    sealed class SerialSettings
    {
        internal static readonly int[] LogRamChoices = { 8, 16, 32, 64, 128, 256 };
        public int LogRamMB = 16;
        public string TextEncodingName = "UTF-8";
        public Encoding TextEncoding { get { return TextEncodingName == "GBK" ? Encoding.GetEncoding(936) : Encoding.UTF8; } }
        internal static readonly string SettingsFile = Path.Combine(StoragePaths.Root, "SerialCom.ini");
        static readonly string PortableSettingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SerialCom.ini");
        static readonly string PreviousSettingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SerialLog.ini");
        static readonly string LegacySettingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KuyiSerialLog", "serial.txt");
        static readonly int[] DefaultRates = { 9600, 115200, 460800, 921600, 1000000, 2000000, 3000000 };
        static readonly int[] PreviousDefaultRates = { 110, 150, 300, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 256000, 460800, 576000, 921600, 1000000, 2000000, 3000000 };

        public List<int> BaudPresets { get; set; }
        public Parity Parity { get; set; }
        public int DataBits { get; set; }
        public StopBits StopBits { get; set; }
        public Handshake Handshake { get; set; }
        public string FilterText = "", CollapseText = "", SendText = "", PortName = "";
        public bool ShowTime = true, AutoScroll = true, Maximized, HexDisplay, HexSend;
        public int CurrentBaud, WindowWidth = 1060, WindowHeight = 710;
        public List<SentCommand> SendHistory = new List<SentCommand>();
        public List<KeywordRule> FilterRules = new List<KeywordRule>(), CollapseRules = new List<KeywordRule>();
        public SendEnding LineEnding = SendEnding.CRLF;

        public void RememberCommand(string text, bool hex, SendEnding ending)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (hex)
            {
                byte[] bytes; string error;
                if (!SerialData.TryBuildPayload(text, true, out bytes, out error)) return;
                text = SerialData.ToHex(bytes);
            }
            SendHistory.RemoveAll(command => command.Hex == hex && command.Text == text && command.Ending == ending);
            SendHistory.Insert(0, new SentCommand(text, hex, ending));
            if (SendHistory.Count > 50) SendHistory.RemoveRange(50, SendHistory.Count - 50);
        }

        public static SerialSettings Defaults()
        {
            return new SerialSettings
            {
                CurrentBaud = 921600,
                BaudPresets = new List<int>(DefaultRates),
                Parity = Parity.None,
                DataBits = 8,
                StopBits = StopBits.One,
                Handshake = Handshake.None
            };
        }

        public static SerialSettings Load(string source = null)
        {
            SerialSettings settings = Defaults();
            bool endingLoaded = false;
            bool currentBaudLoaded = false;
            int legacyBaud = settings.CurrentBaud;
            try
            {
                if (source == null) source = File.Exists(SettingsFile) ? SettingsFile :
                    File.Exists(PortableSettingsFile) ? PortableSettingsFile :
                    File.Exists(PreviousSettingsFile) ? PreviousSettingsFile : LegacySettingsFile;
                foreach (string line in File.ReadAllLines(source, Encoding.UTF8))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    string key = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).Trim();
                    int number;
                    bool flag;
                    if (key == "baud" && int.TryParse(value, out number) && number > 0) legacyBaud = number;
                    else if (key == "presets")
                    {
                        List<int> parsed = ParseRates(value);
                        if (parsed.Count > 0) settings.BaudPresets = parsed;
                    }
                    else if (key == "dataBits" && int.TryParse(value, out number) && number >= 5 && number <= 8) settings.DataBits = number;
                    else if (key == "logRamMB" && int.TryParse(value, out number) && LogRamChoices.Contains(number)) settings.LogRamMB = number;
                    else if (key == "textEncoding" && (value == "UTF-8" || value == "GBK")) settings.TextEncodingName = value;
                    else if (key == "parity")
                    {
                        Parity parsed;
                        if (TryParseEnum(value, out parsed)) settings.Parity = parsed;
                    }
                    else if (key == "stopBits")
                    {
                        StopBits parsed;
                        if (TryParseEnum(value, out parsed) && parsed != StopBits.None) settings.StopBits = parsed;
                    }
                    else if (key == "handshake")
                    {
                        Handshake parsed;
                        if (TryParseEnum(value, out parsed)) settings.Handshake = parsed;
                    }
                    else if (key == "filter") settings.FilterText = Decode(line.Substring(separator + 1));
                    else if (key == "collapse") settings.CollapseText = Decode(line.Substring(separator + 1));
                    else if (key == "filterRule" || key == "collapseRule")
                    {
                        string entry = line.Substring(separator + 1);
                        if (entry.StartsWith("1:") || entry.StartsWith("0:"))
                        {
                            string word = Decode(entry.Substring(2));
                            var rules = key == "filterRule" ? settings.FilterRules : settings.CollapseRules;
                            if (!string.IsNullOrWhiteSpace(word) && !rules.Any(rule => rule.Text == word))
                                rules.Add(new KeywordRule(word, entry[0] == '1'));
                        }
                    }
                    else if (key == "send") settings.SendText = Decode(line.Substring(separator + 1));
                    else if (key == "sendEnding")
                    {
                        SendEnding parsed;
                        if (TryParseEnum(value, out parsed)) { settings.LineEnding = parsed; endingLoaded = true; }
                    }
                    else if (key == "history2" && settings.SendHistory.Count < 50)
                    {
                        string[] entry = line.Substring(separator + 1).Split(new[] { ':' }, 3);
                        SendEnding ending;
                        if (entry.Length == 3 && (entry[0] == "T" || entry[0] == "H") && TryParseEnum(entry[1], out ending))
                        {
                            string command = Decode(entry[2]);
                            if (!string.IsNullOrWhiteSpace(command)) settings.SendHistory.Add(new SentCommand(command, entry[0] == "H", ending));
                        }
                    }
                    else if (key == "history" && settings.SendHistory.Count < 50)
                    {
                        string entry = line.Substring(separator + 1);
                        if (entry.StartsWith("T:") || entry.StartsWith("H:"))
                        {
                            string command = Decode(entry.Substring(2));
                            if (!string.IsNullOrWhiteSpace(command)) settings.SendHistory.Add(new SentCommand(command, entry[0] == 'H'));
                        }
                    }
                    else if (key == "port") settings.PortName = Decode(line.Substring(separator + 1));
                    else if (key == "showTime" && bool.TryParse(value, out flag)) settings.ShowTime = flag;
                    else if (key == "autoScroll" && bool.TryParse(value, out flag)) settings.AutoScroll = flag;
                    else if (key == "hexDisplay" && bool.TryParse(value, out flag)) settings.HexDisplay = flag;
                    else if (key == "hexSend" && bool.TryParse(value, out flag)) settings.HexSend = flag;
                    else if (key == "maximized" && bool.TryParse(value, out flag)) settings.Maximized = flag;
                    else if (key == "currentBaud" && int.TryParse(value, out number) && number > 0) { settings.CurrentBaud = number; currentBaudLoaded = true; }
                    else if (key == "windowWidth" && int.TryParse(value, out number) && number >= 854 && number <= 10000) settings.WindowWidth = number;
                    else if (key == "windowHeight" && int.TryParse(value, out number) && number >= 461 && number <= 10000) settings.WindowHeight = number;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
            if (!endingLoaded) settings.LineEnding = settings.HexSend ? SendEnding.None : SendEnding.CRLF;
            if (settings.FilterRules.Count == 0) KeywordRules.Sync(settings.FilterRules, settings.FilterText);
            else settings.FilterText = KeywordRules.Expression(settings.FilterRules);
            if (settings.CollapseRules.Count == 0) KeywordRules.Sync(settings.CollapseRules, settings.CollapseText);
            else settings.CollapseText = KeywordRules.Expression(settings.CollapseRules);
            if (settings.BaudPresets.SequenceEqual(PreviousDefaultRates))
                settings.BaudPresets = new List<int>(DefaultRates);
            if (!currentBaudLoaded) settings.CurrentBaud = legacyBaud;
            if (!settings.BaudPresets.Contains(settings.CurrentBaud))
                settings.BaudPresets.Insert(0, settings.CurrentBaud);
            return settings;
        }

        public void NormalizeBaudSelection()
        {
            if (!BaudPresets.Contains(CurrentBaud)) CurrentBaud = BaudPresets[0];
        }

        public void Save()
        {
            Directory.CreateDirectory(StoragePaths.Root);
            string temporary = SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, "; SerialCom 配置，自动保存。\n" + Serialize(), new UTF8Encoding(true));
                if (File.Exists(SettingsFile)) File.Replace(temporary, SettingsFile, null);
                else File.Move(temporary, SettingsFile);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal string Serialize()
        {
            return string.Join("\n", new[]
                {
                "logRamMB=" + LogRamMB,
                "textEncoding=" + TextEncodingName,
                "presets=" + string.Join(",", BaudPresets),
                "parity=" + Parity,
                "dataBits=" + DataBits,
                "stopBits=" + StopBits,
                "handshake=" + Handshake,
                "currentBaud=" + CurrentBaud,
                "port=" + Encode(PortName),
                "filter=" + Encode(FilterText),
                "collapse=" + Encode(CollapseText),
                "send=" + Encode(SendText),
                "showTime=" + ShowTime,
                "autoScroll=" + AutoScroll,
                "hexDisplay=" + HexDisplay,
                "hexSend=" + HexSend,
                "sendEnding=" + LineEnding,
                "windowWidth=" + WindowWidth,
                "windowHeight=" + WindowHeight,
                "maximized=" + Maximized
                }.Concat(SendHistory.Take(50).Select(command => "history2=" + (command.Hex ? "H:" : "T:") + command.Ending + ":" + Encode(command.Text)))
                .Concat(FilterRules.Select(rule => "filterRule=" + (rule.Enabled ? "1:" : "0:") + Encode(rule.Text)))
                .Concat(CollapseRules.Select(rule => "collapseRule=" + (rule.Enabled ? "1:" : "0:") + Encode(rule.Text))));
        }

        static string Encode(string text)
        {
            return text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        static string Decode(string text)
        {
            var result = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    char next = text[i + 1];
                    if (next == 'n' || next == 'r' || next == '\\')
                    {
                        result.Append(next == 'n' ? '\n' : next == 'r' ? '\r' : '\\');
                        i++;
                        continue;
                    }
                }
                result.Append(text[i]);
            }
            return result.ToString();
        }

        public void CopyViewFrom(SerialSettings other)
        {
            FilterText = other.FilterText; CollapseText = other.CollapseText;
            FilterRules = other.FilterRules.Select(rule => new KeywordRule(rule.Text, rule.Enabled)).ToList();
            CollapseRules = other.CollapseRules.Select(rule => new KeywordRule(rule.Text, rule.Enabled)).ToList();
            SendText = other.SendText; PortName = other.PortName;
            ShowTime = other.ShowTime; AutoScroll = other.AutoScroll;
            HexDisplay = other.HexDisplay; HexSend = other.HexSend;
            LineEnding = other.LineEnding;
            SendHistory = new List<SentCommand>(other.SendHistory);
            CurrentBaud = other.CurrentBaud;
            WindowWidth = other.WindowWidth; WindowHeight = other.WindowHeight; Maximized = other.Maximized;
        }

        public void CopyFrom(SerialSettings other)
        {
            LogRamMB = other.LogRamMB;
            TextEncodingName = other.TextEncodingName;
            BaudPresets = new List<int>(other.BaudPresets);
            Parity = other.Parity;
            DataBits = other.DataBits;
            StopBits = other.StopBits;
            Handshake = other.Handshake;
        }

        public static List<int> ParseRates(string value)
        {
            var rates = new List<int>();
            foreach (string part in value.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int rate;
                if (!int.TryParse(part, out rate) || rate <= 0) return new List<int>();
                if (!rates.Contains(rate)) rates.Add(rate);
            }
            return rates;
        }

        static bool TryParseEnum<T>(string value, out T result) where T : struct
        {
            return Enum.TryParse<T>(value, true, out result) && Enum.IsDefined(typeof(T), result);
        }
    }

    sealed class SerialSettingsDialog : Form
    {
        readonly TextBox presetsText = new TextBox { BorderStyle = BorderStyle.None, Dock = DockStyle.Fill };
        readonly ComboBox parity = new ThemeCombo();
        readonly ComboBox dataBits = new ThemeCombo();
        readonly ComboBox stopBits = new ThemeCombo();
        readonly ComboBox handshake = new ThemeCombo();
        readonly ComboBox logRam = new ThemeCombo { AccessibleName = "日志 RAM 缓存" };
        readonly ComboBox textEncoding = new ThemeCombo { AccessibleName = "编码方式" };
        public SerialSettings Settings { get; private set; }

        public SerialSettingsDialog(SerialSettings current)
        {
            Text = "串口设置";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 10);
            BackColor = Palette.Canvas;
            ForeColor = Palette.Ink;
            ClientSize = new Size(520, 528);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 14), ColumnCount = 2, RowCount = 9 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 7; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            presetsText.Text = string.Join(", ", current.BaudPresets);
            presetsText.AccessibleName = "波特率预设，以逗号分隔";
            var presetInput = new ThemeInput(presetsText) { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
            AddRow(layout, 0, "波特率预设", presetInput);

            parity.Items.AddRange(new object[] { "无 (None)", "奇 (Odd)", "偶 (Even)", "标记 (Mark)", "空格 (Space)" });
            parity.SelectedIndex = (int)current.Parity;
            dataBits.Items.AddRange(new object[] { 5, 6, 7, 8 });
            dataBits.SelectedItem = current.DataBits;
            stopBits.Items.AddRange(new object[] { "1", "1.5", "2" });
            stopBits.SelectedIndex = current.StopBits == StopBits.One ? 0 : current.StopBits == StopBits.OnePointFive ? 1 : 2;
            handshake.Items.AddRange(new object[] { "无 (None)", "软件 (XON/XOFF)", "硬件 (RTS/CTS)", "硬件 + 软件" });
            handshake.SelectedIndex = (int)current.Handshake;
            AddRow(layout, 1, "奇偶校验", parity);
            AddRow(layout, 2, "数据位", dataBits);
            AddRow(layout, 3, "停止位", stopBits);
            AddRow(layout, 4, "流控", handshake);
            foreach (int mb in SerialSettings.LogRamChoices) logRam.Items.Add(mb + " MB" + (mb == 16 ? "（默认）" : ""));
            logRam.SelectedIndex = Array.IndexOf(SerialSettings.LogRamChoices, current.LogRamMB);
            AddRow(layout, 5, "日志 RAM", logRam);
            textEncoding.Items.AddRange(new object[] { "UTF-8", "GBK" });
            textEncoding.SelectedItem = current.TextEncodingName;
            AddRow(layout, 6, "编码方式", textEncoding);
            var cacheNote = new Label { Dock = DockStyle.Fill, Text = "RAM 为文本与 HEX 缓存合计估算预算，各占一半；不含程序及界面内存，保存后立即生效。原始日志不受 RAM 限制。\n编码需与设备一致，用于文本收发及读取原始日志。\n连接期间修改编码，下次连接生效。", AutoSize = false };
            layout.Controls.Add(cacheNote, 0, 7);
            layout.SetColumnSpan(cacheNote, 2);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 7, 0, 0) };
            var save = new LogButton("保存", true) { Width = 84 };
            var cancel = new LogButton("取消", false) { Width = 84, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            layout.Controls.Add(buttons, 0, 8);
            layout.SetColumnSpan(buttons, 2);
            Controls.Add(layout);
            MonitorWindow.ApplyColors(this, Palette.Canvas);

            save.Click += (s, e) => SaveSettings();
            AcceptButton = save;
            CancelButton = cancel;
        }

        static void AddRow(TableLayoutPanel layout, int row, string label, Control control)
        {
            layout.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Palette.Muted,
                Margin = new Padding(0, 0, 8, 0)
            }, 0, row);
            control.Dock = DockStyle.None;
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            control.Margin = new Padding(0, 4, 0, 4);
            layout.Controls.Add(control, 1, row);
        }

        void SaveSettings()
        {
            List<int> rates = SerialSettings.ParseRates(presetsText.Text);
            if (rates.Count == 0)
            {
                MessageBox.Show(this, "请输入至少一个有效的正整数波特率，用逗号分隔。", "波特率预设无效",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Settings = new SerialSettings
            {
                BaudPresets = rates,
                Parity = (Parity)parity.SelectedIndex,
                DataBits = (int)dataBits.SelectedItem,
                StopBits = stopBits.SelectedIndex == 0 ? StopBits.One : stopBits.SelectedIndex == 1 ? StopBits.OnePointFive : StopBits.Two,
                Handshake = (Handshake)handshake.SelectedIndex,
                LogRamMB = SerialSettings.LogRamChoices[logRam.SelectedIndex],
                TextEncodingName = (string)textEncoding.SelectedItem
            };
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    sealed class LogOutput : RichTextBox
    {
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        static extern IntPtr SendPoint(IntPtr hwnd, uint message, IntPtr wParam, ref Point point);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool RedrawWindow(IntPtr hwnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

        public event EventHandler ScrolledAway;
        bool updating;

        public void AppendLogText(string text, bool followTail = true)
        {
            if (text.Length == 0) return;
            UpdateLog(text, false, followTail);
        }

        public void ReplaceLogText(string text, bool followTail = true)
        {
            UpdateLog(text, true, followTail);
        }

        public void AppendPreservingView(string text)
        {
            AppendLogText(text, false);
        }

        void UpdateLog(string text, bool replace, bool followTail)
        {
            int selectionStart = SelectionStart;
            int selectionLength = SelectionLength;
            Point scrollPosition = Point.Empty;
            IntPtr hwnd = Handle;
            if (!followTail) SendPoint(hwnd, 0x4DD, IntPtr.Zero, ref scrollPosition);
            // WM_SETREDRAW affects native visibility; do not send it to a hidden control.
            bool suspendRedraw = Visible;
            updating = true;
            if (suspendRedraw) SendMessage(hwnd, 0x000B, IntPtr.Zero, IntPtr.Zero);
            try
            {
                int start = replace ? 0 : TextLength;
                if (replace) Text = text;
                else
                {
                    Select(start, 0);
                    SelectedText = text;
                }
                // Format only the new range, while selection highlights are not painted.
                Select(start, TextLength - start);
                SelectionFont = Font;
                SelectionColor = ForeColor;
                if (followTail)
                {
                    Select(TextLength, 0);
                    ScrollToCaret();
                }
                else
                {
                    int restoredStart = Math.Min(selectionStart, TextLength);
                    Select(restoredStart, Math.Min(selectionLength, TextLength - restoredStart));
                    SendPoint(hwnd, 0x4DE, IntPtr.Zero, ref scrollPosition);
                }
                ClearUndo();
            }
            finally
            {
                if (suspendRedraw)
                {
                    SendMessage(hwnd, 0x000B, new IntPtr(1), IntPtr.Zero);
                    // Repaint content and scrollbars together, without erasing the background
                    // or forcing a synchronous paint between serial batches.
                    RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0080 | 0x0400);
                }
                updating = false;
            }
        }

        public void CheckScrollPosition()
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((MethodInvoker)delegate
            {
                if (!IsDisposed && !IsAtBottom)
                {
                    EventHandler handler = ScrolledAway;
                    if (handler != null) handler(this, EventArgs.Empty);
                }
            });
        }

        bool IsAtBottom
        {
            get
            {
                if (TextLength == 0) return true;
                int bottomChar = GetCharIndexFromPosition(new Point(Math.Max(2, ClientSize.Width - 4), Math.Max(0, ClientSize.Height - 2)));
                int visibleLine = GetLineFromCharIndex(bottomChar);
                int lastLine = GetLineFromCharIndex(TextLength);
                return visibleLine >= lastLine - 1;
            }
        }

        protected override void WndProc(ref Message message)
        {
            bool scrollInput = message.Msg == 0x0115 || message.Msg == 0x020A;
            if (message.Msg == 0x0100)
            {
                int key = message.WParam.ToInt32();
                scrollInput = key == 0x21 || key == 0x22 || key == 0x23 || key == 0x24 || key == 0x26 || key == 0x28;
            }

            base.WndProc(ref message);
            if (scrollInput && !updating) CheckScrollPosition();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            CheckScrollPosition();
        }
    }

    sealed class LogLine
    {
        public readonly string Text;
        public readonly DateTime Timestamp;
        public readonly string Direction;

        public LogLine(string text, DateTime timestamp, string direction)
        {
            Text = text;
            Timestamp = timestamp;
            Direction = direction;
        }
    }

    sealed class LogHistory
    {
        const int LineLimit = 8000;
        long budget = 8 * 1048576L;
        static readonly Regex Ansi = new Regex(@"\x1B\[[0-?]*[ -/]*[@-~]");
        readonly Queue<LogLine> lines = new Queue<LogLine>();
        readonly StringBuilder pending = new StringBuilder();
        bool truncated;
        long estimatedBytes;
        public long EstimatedBytes { get { return estimatedBytes; } }

        // Conservative allowance for a LogLine, its UTF-16 string, alignment and
        // queue references. This is a cache estimate, not a process working-set cap.
        static long Cost(LogLine line) { return 96L + ((line.Text.Length * 2L + 7) / 8 * 8); }

        public void SetBudget(long bytes)
        {
            if (bytes <= 0) throw new ArgumentOutOfRangeException("bytes");
            budget = bytes;
            Trim(line => { });
            lines.TrimExcess();
        }

        void Trim(Action<LogLine> evicted)
        {
            while (estimatedBytes > budget && lines.Count > 0)
            {
                LogLine removed = lines.Dequeue();
                estimatedBytes -= Cost(removed);
                evicted(removed);
            }
        }

        public int Count { get { return lines.Count; } }
        public IEnumerable<LogLine> Lines { get { return lines; } }

        public int Accept(string chunk, string direction, Action<LogLine> completed, Action<LogLine> evicted)
        {
            int count = 0;
            foreach (char ch in chunk)
            {
                if (ch == '\r' || ch == '\n')
                {
                    if (pending.Length == 0) continue;
                    string text = Ansi.Replace(pending.ToString(), "");
                    if (truncated) text += " …（单行已截断）";
                    pending.Clear();
                    truncated = false;
                    if (text.Length == 0) continue;
                    Add(new LogLine(text, DateTime.Now, direction), completed, evicted);
                    count++;
                }
                else if (ch != '\0')
                {
                    if (pending.Length < LineLimit) pending.Append(ch);
                    else truncated = true;
                }
            }
            return count;
        }

        public void Add(LogLine line, Action<LogLine> added, Action<LogLine> evicted)
        {
            lines.Enqueue(line);
            estimatedBytes += Cost(line);
            added(line);
            Trim(evicted);
        }

        public void Clear()
        {
            lines.Clear();
            lines.TrimExcess();
            pending.Clear();
            truncated = false;
            estimatedBytes = 0;
        }

        public static bool Matches(string line, string filter)
        {
            return line.IndexOf(filter, StringComparison.Ordinal) >= 0;
        }
    }

    static class Palette
    {
        public static Color Canvas { get { return Color.FromArgb(243, 246, 252); } }
        public static Color Surface { get { return Color.White; } }
        public static Color Input { get { return Color.FromArgb(248, 250, 254); } }
        public static Color Ink { get { return Color.FromArgb(23, 37, 62); } }
        public static Color Muted { get { return Color.FromArgb(85, 104, 133); } }
        public static Color Edge { get { return Color.FromArgb(210, 220, 237); } }
        public static Color Hover { get { return Color.FromArgb(235, 241, 255); } }
        public static Color Disabled { get { return Color.FromArgb(224, 230, 240); } }
        public static Color Error { get { return Color.FromArgb(177, 54, 53); } }
    }

    sealed class KeywordInput : ThemeInput
    {
        readonly TextBox editor;
        readonly Button arrow;
        readonly List<KeywordRule> rules;
        readonly Action changed;
        readonly ContextMenuStrip menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = false };

        public KeywordInput(TextBox editor, string placeholder, List<KeywordRule> rules, Action changed) : base(editor, placeholder)
        {
            this.editor = editor; this.rules = rules; this.changed = changed;
            Padding = new Padding(10, 0, 42, 0);
            arrow = new CommandInput.HistoryArrow { AccessibleName = editor.AccessibleName + "关键词列表" };
            arrow.Click += (s, e) => ShowRules();
            Controls.Add(arrow);
            editor.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    KeywordRules.Sync(rules, editor.Text);
                    ApplyRules();
                }
                else if (e.KeyCode == Keys.F4 || (e.Alt && e.KeyCode == Keys.Down))
                {
                    e.SuppressKeyPress = true;
                    ShowRules();
                }
            };
            menu.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Delete) return;
                var item = menu.Items.OfType<CommandInput.HistoryMenuItem>().FirstOrDefault(row => row.Selected);
                if (item != null) { e.SuppressKeyPress = true; item.DeleteEntry(); }
            };
        }

        void ApplyRules()
        {
            editor.Text = KeywordRules.Expression(rules);
            changed();
        }

        void ShowRules()
        {
            KeywordRules.Sync(rules, editor.Text);
            while (menu.Items.Count > 0) menu.Items[0].Dispose();
            menu.Font = Font; menu.BackColor = Palette.Surface; menu.ForeColor = Palette.Ink;
            int width = Math.Min(800, Math.Max(300, Width));
            menu.MinimumSize = new Size(width + menu.Padding.Horizontal, 0);
            foreach (KeywordRule rule in rules)
            {
                KeywordRule entry = rule;
                var item = new CommandInput.HistoryMenuItem(entry.Text,
                    () => { menu.Close(); entry.Enabled = !entry.Enabled; ApplyRules(); },
                    () => { menu.Close(); rules.Remove(entry); ApplyRules(); })
                {
                    AutoSize = false, Size = new Size(width, Font.Height + 16),
                    ShowCheckbox = true, Checked = entry.Enabled, ToolTipText = entry.Text,
                    AccessibleDescription = "点击勾选或取消启用；右侧垃圾桶删除关键词。"
                };
                menu.Items.Add(item);
            }
            if (menu.Items.Count == 0) menu.Items.Add(new ToolStripMenuItem("输入关键词，可以用|分割") { Enabled = false });
            menu.Show(this, new Point(0, Height));
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (arrow != null) arrow.SetBounds(Width - Padding.Right + 4, 4, Math.Max(20, Padding.Right - 12), Math.Max(1, Height - 8));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) menu.Dispose();
            base.Dispose(disposing);
        }
    }

    sealed class CommandInput : ThemeInput
    {
        readonly Button arrow;
        readonly ContextMenuStrip menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = false };
        readonly Func<IEnumerable<SentCommand>> commands;
        readonly Action<SentCommand> selected;
        readonly Action<SentCommand> deleted;

        public CommandInput(TextBox editor, Func<IEnumerable<SentCommand>> commands, Action<SentCommand> selected, Action<SentCommand> deleted)
            : base(editor, "输入命令，或从右侧下拉选择历史")
        {
            this.commands = commands;
            this.selected = selected;
            this.deleted = deleted;
            Padding = new Padding(10, 0, 42, 0);
            arrow = new HistoryArrow { AccessibleName = "发送历史", TabStop = true };
            arrow.Click += (s, e) => ShowHistory();
            Controls.Add(arrow);
            menu.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Delete) return;
                var item = menu.Items.OfType<HistoryMenuItem>().FirstOrDefault(candidate => candidate.Selected);
                if (item == null) return;
                e.SuppressKeyPress = true;
                item.DeleteEntry();
                menu.Close();
            };
            editor.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F4 || (e.Alt && e.KeyCode == Keys.Down))
                {
                    e.SuppressKeyPress = true;
                    ShowHistory();
                }
            };
        }

        void ShowHistory()
        {
            while (menu.Items.Count > 0) menu.Items[0].Dispose();
            menu.Font = Font;
            menu.BackColor = Palette.Surface;
            menu.ForeColor = Palette.Ink;
            int width = Math.Min(800, Math.Max(300, Width));
            menu.MinimumSize = new Size(width + menu.Padding.Horizontal, 0);
            foreach (SentCommand command in commands())
            {
                SentCommand entry = command;
                string preview = entry.Text.Replace("\r", "\\r").Replace("\n", "\\n");
                if (preview.Length > 90) preview = preview.Substring(0, 90) + "…";
                var item = new HistoryMenuItem("[" + (entry.Hex ? "HEX" : "文本") + "]  " + preview,
                    () => { menu.Close(); selected(entry); }, () => { menu.Close(); deleted(entry); })
                {
                    AutoSize = false, Size = new Size(width, Font.Height + 16),
                    TextAlign = ContentAlignment.MiddleLeft, ToolTipText = entry.Text
                };
                menu.Items.Add(item);
            }
            if (menu.Items.Count == 0) menu.Items.Add(new ToolStripMenuItem("暂无发送历史，成功发送后自动记录") { Enabled = false });
            menu.Show(this, new Point(0, Height));
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (arrow != null) arrow.SetBounds(Width - Padding.Right + 4, 4,
                Math.Max(20, Padding.Right - 12), Math.Max(1, Height - 8));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) menu.Dispose();
            base.Dispose(disposing);
        }

        internal sealed class HistoryMenuItem : ToolStripMenuItem
        {
            public bool ShowCheckbox;
            readonly Action selectEntry, deleteEntry;
            bool deletePressed, deleteHovered;
            internal Rectangle DeleteBounds { get { return new Rectangle(Width - 38, 0, 38, Height); } }

            public HistoryMenuItem(string text, Action selectEntry, Action deleteEntry) : base(text)
            {
                this.selectEntry = selectEntry;
                this.deleteEntry = deleteEntry;
                AccessibleName = text;
                AccessibleDescription = "选择以填入发送框；点击右侧垃圾桶或按 Delete 删除此条历史。";
            }

            internal void DeleteEntry() { deleteEntry(); }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                deletePressed = e.Button == MouseButtons.Left && DeleteBounds.Contains(e.Location);
                base.OnMouseDown(e);
            }

            protected override void OnClick(EventArgs e)
            {
                try { if (deletePressed) DeleteEntry(); else selectEntry(); }
                finally { deletePressed = false; }
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                bool hovered = DeleteBounds.Contains(e.Location);
                if (deleteHovered != hovered) { deleteHovered = hovered; Invalidate(); }
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                deleteHovered = false;
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Selected ? Palette.Hover : Palette.Surface);
                int left = ShowCheckbox ? 34 : 10;
                if (ShowCheckbox)
                {
                    int checkY = (Height - 18) / 2;
                    LogStyle.Paint(e.Graphics, new Rectangle(9, checkY, 18, 18), 5,
                        Checked ? MonitorWindow.Blue : Palette.Input, Checked ? MonitorWindow.Blue : Palette.Edge);
                    if (Checked) using (var pen = new Pen(Color.White, 2))
                        e.Graphics.DrawLines(pen, new[] { new Point(13, checkY + 8), new Point(16, checkY + 11), new Point(22, checkY + 5) });
                }
                TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(left, 0, Math.Max(1, Width - left - 42), Height), Palette.Ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                Rectangle hit = DeleteBounds;
                if (deleteHovered)
                    using (var brush = new SolidBrush(Color.FromArgb(255, 232, 232))) e.Graphics.FillRectangle(brush, hit);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float x = hit.Left + hit.Width / 2f, y = Height / 2f;
                using (var pen = new Pen(deleteHovered ? Palette.Error : Palette.Muted, 1.4f))
                {
                    e.Graphics.DrawLine(pen, x - 7, y - 5, x + 7, y - 5);
                    e.Graphics.DrawLines(pen, new[] { new PointF(x - 3, y - 5), new PointF(x - 3, y - 8), new PointF(x + 3, y - 8), new PointF(x + 3, y - 5) });
                    e.Graphics.DrawLines(pen, new[] { new PointF(x - 5, y - 3), new PointF(x - 4, y + 7), new PointF(x + 4, y + 7), new PointF(x + 5, y - 3) });
                    e.Graphics.DrawLine(pen, x - 2, y - 2, x - 2, y + 4);
                    e.Graphics.DrawLine(pen, x + 2, y - 2, x + 2, y + 4);
                }
            }
        }

        internal sealed class HistoryArrow : Button
        {
            public HistoryArrow()
            {
                FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(ClientRectangle.Contains(PointToClient(Cursor.Position)) ? Palette.Hover : Palette.Input);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int x = Width / 2, y = Height / 2;
                using (var pen = new Pen(Palette.Muted, 1.5f))
                    e.Graphics.DrawLines(pen, new[] { new Point(x - 4, y - 2), new Point(x, y + 2), new Point(x + 4, y - 2) });
                if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -2, -2));
            }
        }
    }

    class ThemeInput : Panel
    {
        readonly TextBox editor;
        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, string lParam);

        public ThemeInput(TextBox text) : this(text, null) { }

        public ThemeInput(TextBox text, string placeholder)
        {
            editor = text;
            Size = new Size(220, 36);
            Padding = new Padding(10, 0, 10, 0);
            DoubleBuffered = true;
            ResizeRedraw = true;
            text.Dock = DockStyle.None;
            // The native borderless single-line preferred height can be smaller
            // than the rendered CJK font, clipping Latin descenders and IME text.
            text.AutoSize = false;
            text.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            if (!string.IsNullOrEmpty(placeholder))
                text.HandleCreated += (s, e) => SendMessage(text.Handle, 0x1501, new IntPtr(1), placeholder);
            Controls.Add(text);
            text.GotFocus += (s, e) => Invalidate();
            text.LostFocus += (s, e) => Invalidate();
            text.FontChanged += (s, e) => PerformLayout();
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (editor == null) return;
            int height = Math.Max(editor.PreferredHeight,
                TextRenderer.MeasureText("国Agjpqy", editor.Font).Height + 2);
            editor.SetBounds(Padding.Left, Math.Max(0, (ClientSize.Height - height) / 2),
                Math.Max(0, ClientSize.Width - Padding.Horizontal), height);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Palette.Surface : Parent.BackColor);
            LogStyle.Paint(e.Graphics, ClientRectangle, 12, Palette.Input, ContainsFocus ? MonitorWindow.Blue : Palette.Edge);
        }
    }

    sealed class ThemeCombo : ComboBox
    {
        public ThemeCombo()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = 28;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Palette.Surface : Parent.BackColor);
            LogStyle.Paint(e.Graphics, ClientRectangle, 12, Palette.Input, Focused ? MonitorWindow.Blue : Palette.Edge);
            // Editable combos have a native edit child for text, caret and selection.
            if (DropDownStyle == ComboBoxStyle.DropDownList)
                TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(8, 0, Width - 30, Height), Enabled ? Palette.Ink : Palette.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            int x = Width - 16, y = Height / 2;
            using (var pen = new Pen(Palette.Muted, 1.5f))
                e.Graphics.DrawLines(pen, new[] { new Point(x - 4, y - 2), new Point(x, y + 2), new Point(x + 4, y - 2) });
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(selected ? Palette.Hover : Palette.Input)) e.Graphics.FillRectangle(brush, e.Bounds);
            if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, Rectangle.Inflate(e.Bounds, -6, 0), Palette.Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            e.DrawFocusRectangle();
        }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    sealed class ThemeCheck : CheckBox
    {
        public ThemeCheck() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); Cursor = Cursors.Hand; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            int y = (Height - 17) / 2;
            LogStyle.Paint(e.Graphics, new Rectangle(1, y, 18, 18), 5, Checked ? MonitorWindow.Blue : Palette.Input, Checked ? MonitorWindow.Blue : Palette.Edge);
            if (Checked) using (var pen = new Pen(Color.White, 2)) e.Graphics.DrawLines(pen, new[] { new Point(5, y + 8), new Point(8, y + 11), new Point(14, y + 5) });
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(25, 0, Width - 25, Height), Palette.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -1, -2), Palette.Ink, BackColor);
        }
    }

    static class LogStyle
    {
        public static GraphicsPath Rounded(Rectangle rect, int diameter)
        {
            var shape = new GraphicsPath();
            int d = Math.Min(diameter, Math.Min(rect.Width, rect.Height));
            if (d < 2) return shape;
            shape.AddArc(rect.X, rect.Y, d, d, 180, 90);
            shape.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            shape.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            shape.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            shape.CloseFigure();
            return shape;
        }

        public static void Paint(Graphics g, Rectangle bounds, int diameter, Color fill, Color edge)
        {
            // Keep the entire stroke inside the client area, including its antialiased edge.
            if (bounds.Width < 4 || bounds.Height < 4) return;
            bounds.Width--;
            bounds.Height--;
            bounds.Inflate(-1, -1);
            GraphicsState state = g.Save();
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.None;
                using (var shape = Rounded(bounds, diameter))
                using (var brush = new SolidBrush(fill))
                using (var border = new Pen(edge, 1f))
                {
                    g.FillPath(brush, shape);
                    g.DrawPath(border, shape);
                }
            }
            finally { g.Restore(state); }
        }
    }

    sealed class LogCard : Panel
    {
        public LogCard() { DoubleBuffered = true; ResizeRedraw = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Palette.Canvas : Parent.BackColor);
            LogStyle.Paint(e.Graphics, ClientRectangle, 20, Palette.Surface, MonitorWindow.Edge);
        }
    }

    sealed class LogButton : Button
    {
        readonly bool primary;

        public LogButton(string label, bool primary)
        {
            Text = label;
            this.primary = primary;
            Size = new Size(primary ? 108 : 92, 36);
            Margin = new Padding(3, 0, 3, 0);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            bool hover = Enabled && ClientRectangle.Contains(PointToClient(Cursor.Position));
            e.Graphics.Clear(Parent == null ? Palette.Surface : Parent.BackColor);
            Color fill = primary ? (Enabled ? (hover ? Color.FromArgb(65, 110, 245) : MonitorWindow.Blue) : Palette.Disabled)
                : (hover ? Palette.Hover : Palette.Surface);
            LogStyle.Paint(e.Graphics, ClientRectangle, 12, fill, primary ? fill : MonitorWindow.Edge);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, !Enabled ? Palette.Muted : primary ? Color.White : Palette.Ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
        }
    }
}
