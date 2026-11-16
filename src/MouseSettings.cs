using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OmniDesk
{
    // Which mouse buttons switch desktops, stored per account. Left and right click are never offered:
    // taking those over would make the mouse unusable.
    static class MouseSettings
    {
        public const int None = 0, Previous = 1, Next = 2;

        // Index into the action array; the order is also the order shown in the settings window.
        public const int Middle = 0, Side1 = 1, Side2 = 2, TiltLeft = 3, TiltRight = 4;

        public static readonly string[] ButtonNames =
        {
            "Middle button (wheel click)", "Side button 1 (Back)", "Side button 2 (Forward)", "Wheel tilt left", "Wheel tilt right"
        };

        public static readonly string[] ActionNames = { "Do nothing", "Previous desktop", "Next desktop" };

        static readonly string[] ValueNames = { "MouseMiddle", "MouseSide1", "MouseSide2", "MouseTiltLeft", "MouseTiltRight" };
        static readonly int[] Defaults = { None, Previous, Next, None, None };

        const string Key = @"Software\OmniDesk";

        public static bool Enabled
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Key))
                        return k == null || Convert.ToInt32(k.GetValue("MouseEnabled", 1)) != 0;
                }
                catch { return true; }
            }
            set
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key))
                        k.SetValue("MouseEnabled", value ? 1 : 0, RegistryValueKind.DWord);
                }
                catch { }
            }
        }

        public static int[] LoadActions()
        {
            var a = (int[])Defaults.Clone();
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    if (k != null)
                        for (int i = 0; i < a.Length; i++)
                        {
                            int v = Convert.ToInt32(k.GetValue(ValueNames[i], Defaults[i]));
                            a[i] = v >= None && v <= Next ? v : Defaults[i];
                        }
                }
            }
            catch { }
            return a;
        }

        public static void SaveActions(int[] a)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key))
                    for (int i = 0; i < a.Length; i++)
                        k.SetValue(ValueNames[i], a[i], RegistryValueKind.DWord);
            }
            catch { }
        }

        public static bool AnyMapped(int[] a)
        {
            foreach (int v in a) if (v != None) return true;
            return false;
        }
    }

    // "Choose mouse buttons..." window: one row per button, plus a helper that names a button when you press it.
    sealed class MouseSettingsForm : Form
    {
        readonly ComboBox[] _combos = new ComboBox[5];
        readonly Label _status;
        readonly Button _detectButton;
        readonly Action<Action<int>> _startDetect;
        readonly Action _stopDetect;

        public int[] Actions
        {
            get
            {
                var a = new int[_combos.Length];
                for (int i = 0; i < a.Length; i++) a[i] = _combos[i].SelectedIndex;
                return a;
            }
        }

        public MouseSettingsForm(int[] current, Action<Action<int>> startDetect, Action stopDetect)
        {
            _startDetect = startDetect;
            _stopDetect = stopDetect;

            Text = "OmniDesk: mouse buttons";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(14);
            KeyPreview = true;

            var table = new TableLayoutPanel();
            table.AutoSize = true;
            table.ColumnCount = 2;
            table.Dock = DockStyle.Top;

            var intro = new Label();
            intro.Text = "Choose what each mouse button does. Left and right click are not available.";
            intro.AutoSize = true;
            intro.MaximumSize = new Size(420, 0);
            intro.Margin = new Padding(0, 0, 0, 10);
            table.Controls.Add(intro, 0, 0);
            table.SetColumnSpan(intro, 2);

            for (int i = 0; i < _combos.Length; i++)
            {
                var label = new Label();
                label.Text = MouseSettings.ButtonNames[i];
                label.AutoSize = true;
                label.Anchor = AnchorStyles.Left;
                label.Margin = new Padding(0, 6, 16, 6);

                var combo = new ComboBox();
                combo.DropDownStyle = ComboBoxStyle.DropDownList;
                combo.Items.AddRange(MouseSettings.ActionNames);
                combo.SelectedIndex = current[i];
                combo.Width = 170;
                combo.Margin = new Padding(0, 3, 0, 3);
                _combos[i] = combo;

                table.Controls.Add(label, 0, i + 1);
                table.Controls.Add(combo, 1, i + 1);
            }

            _detectButton = new Button();
            _detectButton.Text = "Detect a button...";
            _detectButton.AutoSize = true;
            _detectButton.Margin = new Padding(0, 12, 8, 0);
            _detectButton.Click += delegate { BeginDetect(); };

            _status = new Label();
            _status.Text = "Press Detect, then the button you want to find.";
            _status.AutoSize = true;
            _status.MaximumSize = new Size(300, 0);
            _status.Anchor = AnchorStyles.Left;
            _status.Margin = new Padding(0, 14, 0, 0);

            var detectRow = new FlowLayoutPanel();
            detectRow.AutoSize = true;
            detectRow.Controls.Add(_detectButton);
            detectRow.Controls.Add(_status);
            table.Controls.Add(detectRow, 0, 6);
            table.SetColumnSpan(detectRow, 2);

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true;
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 14, 0, 0);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            table.Controls.Add(buttons, 0, 7);
            table.SetColumnSpan(buttons, 2);

            Controls.Add(table);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        void BeginDetect()
        {
            _detectButton.Enabled = false;
            _status.Text = "Press the mouse button now...";
            _startDetect(delegate(int id)
            {
                if (IsDisposed) return;
                _detectButton.Enabled = true;
                _status.Text = "That was: " + MouseSettings.ButtonNames[id] + ". Choose its action on the right.";
                _combos[id].Focus();
            });
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _stopDetect();
            base.OnFormClosed(e);
        }
    }
}
