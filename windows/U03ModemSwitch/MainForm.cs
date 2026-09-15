// SPDX-License-Identifier: MIT
using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace U03ModemSwitch
{
    internal sealed class MainForm : Form
    {
        private readonly U03Controller controller = new U03Controller();
        private readonly Label statusLabel;
        private readonly TextBox logBox;
        private readonly Button refreshButton;
        private readonly Button modemButton;
        private readonly Button rndisButton;
        private readonly Button copyButton;
        private readonly ProgressBar progressBar;
        private bool busy;

        internal MainForm()
        {
            Text = "au ZTE U03 Modem Mode Switch";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 560);
            MinimumSize = new Size(776, 599);
            BackColor = Color.FromArgb(245, 247, 250);
            Font = new Font("Yu Gothic UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;

            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 92,
                BackColor = Color.FromArgb(24, 39, 67)
            };
            Controls.Add(header);

            var title = new Label
            {
                AutoSize = true,
                Text = "Speed USB STICK U03",
                ForeColor = Color.White,
                Font = new Font("Yu Gothic UI", 21F, FontStyle.Bold),
                Location = new Point(25, 14)
            };
            header.Controls.Add(title);

            var subtitle = new Label
            {
                AutoSize = true,
                Text = "RNDIS / Web UI  ⇄  USB Modem  — Persistent Mode Switch",
                ForeColor = Color.FromArgb(191, 205, 225),
                Font = new Font("Segoe UI", 9.5F),
                Location = new Point(28, 57)
            };
            header.Controls.Add(subtitle);

            var statusCaption = new Label
            {
                AutoSize = true,
                Text = "現在の状態",
                ForeColor = Color.FromArgb(70, 78, 91),
                Location = new Point(24, 111)
            };
            Controls.Add(statusCaption);

            statusLabel = new Label
            {
                Text = "U03を確認しています…",
                Font = new Font("Yu Gothic UI", 14F, FontStyle.Bold),
                ForeColor = Color.FromArgb(45, 55, 72),
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(24, 137),
                Size = new Size(712, 52),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Padding = new Padding(12, 0, 0, 0)
            };
            Controls.Add(statusLabel);

            refreshButton = MakeButton("状態を再確認", Color.FromArgb(230, 234, 240), Color.FromArgb(35, 45, 61));
            refreshButton.Location = new Point(24, 207);
            refreshButton.Size = new Size(158, 44);
            refreshButton.Click += async (_, __) => await RunStatusAsync();
            Controls.Add(refreshButton);

            modemButton = MakeButton("モデムモードへ", Color.FromArgb(28, 105, 212), Color.White);
            modemButton.Location = new Point(194, 207);
            modemButton.Size = new Size(196, 44);
            modemButton.Click += async (_, __) => await SwitchToModemAsync();
            Controls.Add(modemButton);

            rndisButton = MakeButton("RNDISへ戻す", Color.FromArgb(74, 85, 104), Color.White);
            rndisButton.Location = new Point(402, 207);
            rndisButton.Size = new Size(196, 44);
            rndisButton.Click += async (_, __) => await SwitchToRndisAsync();
            Controls.Add(rndisButton);

            copyButton = MakeButton("ログをコピー", Color.FromArgb(230, 234, 240), Color.FromArgb(35, 45, 61));
            copyButton.Location = new Point(610, 207);
            copyButton.Size = new Size(126, 44);
            copyButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            copyButton.Click += (_, __) => CopyLog();
            Controls.Add(copyButton);

            progressBar = new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 25,
                Location = new Point(24, 264),
                Size = new Size(712, 7),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Visible = false
            };
            Controls.Add(progressBar);

            logBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = false,
                Font = new Font("Consolas", 9.2F),
                BackColor = Color.FromArgb(17, 24, 39),
                ForeColor = Color.FromArgb(220, 229, 241),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(24, 283),
                Size = new Size(712, 218),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(logBox);

            var notice = new Label
            {
                Text = "対象: au U03 / ZTE MF871    非公式ツール — 実行前にSIM・APNと対象機種を確認してください",
                ForeColor = Color.FromArgb(104, 113, 128),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(24, 516),
                Size = new Size(712, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(notice);

            var tips = new ToolTip();
            tips.SetToolTip(modemButton, "19d2:1483から永続モデムモード19d2:1481へ切り替えます");
            tips.SetToolTip(rndisButton, "ATコマンドで元のRNDIS/Web UIモードへ戻します");

            Shown += async (_, __) => await RunStatusAsync();
            FormClosing += OnFormClosing;
        }

        private static Button MakeButton(string text, Color background, Color foreground)
        {
            var button = new Button
            {
                Text = text,
                BackColor = background,
                ForeColor = foreground,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderSize = 0;
            return button;
        }

        private async Task RunStatusAsync()
        {
            await RunOperationAsync("STATUS", report => controller.GetStatus(report), false);
        }

        private async Task SwitchToModemAsync()
        {
            var answer = MessageBox.Show(
                this,
                "U03のUSB構成を永続的なモデムモードへ変更し、端末を再起動します。\n\n" +
                "処理中はU03を抜かないでください。続行しますか？",
                "モデムモードへ切替",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return;

            await RunOperationAsync("SWITCH TO MODEM", report => controller.SwitchToModem(report), true);
        }

        private async Task SwitchToRndisAsync()
        {
            var answer = MessageBox.Show(
                this,
                "U03をRNDIS/Web UIモードへ戻して再起動します。\n\n" +
                "モデムのCOMポートドライバーが必要です。続行しますか？",
                "RNDISへ戻す",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return;

            await RunOperationAsync("RESTORE RNDIS", report => controller.SwitchToRndis(report), true);
        }

        private async Task RunOperationAsync(
            string caption,
            Func<Action<string>, string> operation,
            bool showSuccess)
        {
            if (busy)
                return;

            SetBusy(true);
            AppendLog(string.Empty);
            AppendLog("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + caption);
            IProgress<string> progress = new Progress<string>(AppendLog);

            try
            {
                string result = await Task.Run(() => operation(message => progress.Report(message)));
                SetStatusFromResult(result);
                if (showSuccess)
                {
                    MessageBox.Show(
                        this,
                        "切替処理が完了しました。\n\n" + statusLabel.Text,
                        "U03 Modem Mode Switch",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                statusLabel.Text = "処理に失敗しました";
                statusLabel.ForeColor = Color.Firebrick;
                AppendLog("ERROR: " + ex.Message);
                MessageBox.Show(
                    this,
                    ex.Message + "\n\n画面下部のログも確認してください。",
                    "U03 Modem Mode Switch",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool value)
        {
            busy = value;
            refreshButton.Enabled = !value;
            modemButton.Enabled = !value;
            rndisButton.Enabled = !value;
            progressBar.Visible = value;
            if (value)
            {
                statusLabel.Text = "処理中… USBを抜かないでください";
                statusLabel.ForeColor = Color.DarkOrange;
            }
        }

        private void SetStatusFromResult(string result)
        {
            if (result.IndexOf("19d2:1481", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                statusLabel.Text = "モデムモード（19d2:1481）";
                statusLabel.ForeColor = Color.DarkGreen;
            }
            else if (result.IndexOf("19d2:1483", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                statusLabel.Text = "RNDIS / Web UI（19d2:1483）";
                statusLabel.ForeColor = Color.DarkBlue;
            }
            else if (result.IndexOf("19d2:1484", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                statusLabel.Text = "仮想CD-ROM（19d2:1484）";
                statusLabel.ForeColor = Color.DarkOrange;
            }
            else if (result.IndexOf("19d2:0016", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                statusLabel.Text = "診断 / 復旧モード（19d2:0016）";
                statusLabel.ForeColor = Color.Firebrick;
            }
            else
            {
                statusLabel.Text = "完了（ログを確認してください）";
                statusLabel.ForeColor = Color.FromArgb(45, 55, 72);
            }
        }

        private void AppendLog(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                logBox.AppendText(Environment.NewLine);
                return;
            }

            logBox.AppendText(line + Environment.NewLine);
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }

        private void CopyLog()
        {
            if (logBox.TextLength == 0)
                return;
            Clipboard.SetText(logBox.Text);
            AppendLog("[ログをクリップボードへコピーしました]");
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!busy)
                return;
            e.Cancel = true;
            MessageBox.Show(
                this,
                "切替処理が終わるまでこの画面を閉じないでください。",
                "U03 Modem Mode Switch",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}
