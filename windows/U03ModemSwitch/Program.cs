// SPDX-License-Identifier: MIT
using System;
using System.Windows.Forms;

namespace U03ModemSwitch
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
