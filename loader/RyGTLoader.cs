// RyGT loader: sets up Gran Turismo Sport from the player's own dump, firmware modules and fonts,
// then starts it. Built with build.cmd (C# for .NET Framework 4.x, which Windows includes).
// SPDX-License-Identifier: GPL-2.0-only

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyCompany("RyGT-Team")]
[assembly: System.Reflection.AssemblyCopyright("Copyright © 2026 RyGT-Team")]
[assembly: System.Reflection.AssemblyProduct("RyGTS")]
[assembly: System.Reflection.AssemblyTitle("RyGTS Launcher")]
[assembly: System.Reflection.AssemblyDescription("Gran Turismo Sport launcher for Windows")]

namespace RyGT
{
    static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

        // Returns a one-byte BOOLEAN, not a BOOL.
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool CreateSymbolicLink(string link, string target, int flags);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr window, string appName, string idList);

        [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software,
            uint flags, IntPtr featureLevels, uint featureLevelsCount, uint sdkVersion,
            out IntPtr device, out int featureLevel, out IntPtr immediateContext);
    }

    /// Wine (Linux, Steam Proton): its kernel32 has wine_get_unix_file_name. Wine before 11 cannot
    /// make Windows links (CreateSymbolicLink does nothing there and mklink /J fails), so under Wine
    /// the game's files and folders are linked with Unix symbolic links, which Wine follows like the
    /// files themselves (and reports as reparse points when they are folders).
    static class Wine
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern IntPtr wine_get_unix_file_name(string dosPath);

        [DllImport("kernel32.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true,
                   BestFitMapping = false)]
        static extern IntPtr wine_get_dos_file_name(string unixPath);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetProcessHeap();

        [DllImport("kernel32.dll")]
        static extern bool HeapFree(IntPtr heap, uint flags, IntPtr memory);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct StartupInfo
        {
            public int cb;
            public string reserved, desktop, title;
            public int x, y, width, height, columns, rows, fill, flags;
            public short show, reserved2;
            public IntPtr reserved3, input, output, error;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ProcessInformation
        {
            public IntPtr process, thread;
            public int processId, threadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory,
            ref StartupInfo startup, out ProcessInformation information);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        static readonly bool detected = DetectWine();

        public static bool Detected { get { return detected; } }

        static bool DetectWine()
        {
            try
            {
                IntPtr kernel = GetModuleHandle("kernel32.dll");
                return kernel != IntPtr.Zero && GetProcAddress(kernel, "wine_get_unix_file_name") != IntPtr.Zero;
            }
            catch { return false; }
        }

        /// The Unix path of a Windows path (whose last part need not exist yet).
        public static string UnixPath(string path)
        {
            IntPtr unix = wine_get_unix_file_name(Path.GetFullPath(path));
            if (unix == IntPtr.Zero) throw new Exception("Wine has no Unix path for " + path + ".");
            try
            {
                int length = 0;
                while (Marshal.ReadByte(unix, length) != 0) length++;
                var bytes = new byte[length];
                Marshal.Copy(unix, bytes, 0, length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally { HeapFree(GetProcessHeap(), 0, unix); }
        }

        static string DosPath(string unixPath)
        {
            IntPtr dos = wine_get_dos_file_name(unixPath);
            if (dos == IntPtr.Zero) throw new Exception("Wine has no Windows path for " + unixPath + ".");
            try { return Marshal.PtrToStringUni(dos); }
            finally { HeapFree(GetProcessHeap(), 0, dos); }
        }

        static string Quote(string unixPath)
        {
            return "'" + unixPath.Replace("'", "'\\''") + "'";
        }

        /// Makes each link (key) a Unix symbolic link to its target (value), replacing an earlier
        /// symbolic link but never a real file or folder, with one /bin/sh script. Wine starts Unix
        /// programs detached, so the script's last line writes a file to wait for.
        public static void Link(IEnumerable<KeyValuePair<string, string>> links)
        {
            string name = "rygt-link-" + Guid.NewGuid().ToString("N");
            string script = Path.Combine(Path.GetTempPath(), name + ".sh");
            string done = Path.Combine(Path.GetTempPath(), name + ".done");
            var text = new StringBuilder();
            text.Append("#!/bin/sh\n# Written by RyGTS.exe: links the game's files under Wine.\n");
            text.Append("PATH=/usr/local/bin:/usr/bin:/bin\n");
            text.Append("link() { if [ -L \"$2\" ] || [ ! -e \"$2\" ]; then ln -sfn \"$1\" \"$2\"; fi; }\n");
            foreach (var pair in links)
            {
                // The link's folder exists; its last part may not (Wine still gives the Unix path).
                string folder = UnixPath(Path.GetDirectoryName(Path.GetFullPath(pair.Key)));
                text.Append("link " + Quote(UnixPath(pair.Value)) + " " +
                            Quote(folder.TrimEnd('/') + "/" + Path.GetFileName(pair.Key)) + "\n");
            }
            text.Append("echo done > " + Quote(UnixPath(done)) + "\n");
            File.WriteAllText(script, text.ToString(), new UTF8Encoding(false));
            try
            {
                var startup = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)) };
                ProcessInformation information;
                var command = new StringBuilder("\"" + DosPath("/bin/sh") + "\" \"" + UnixPath(script) + "\"");
                const uint CreateNoWindow = 0x08000000;
                if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false, CreateNoWindow, IntPtr.Zero, null,
                                   ref startup, out information))
                    throw new Exception("Could not start /bin/sh to link the game's files (error " +
                                        Marshal.GetLastWin32Error() + ").");
                if (information.thread != IntPtr.Zero) CloseHandle(information.thread);
                if (information.process != IntPtr.Zero) CloseHandle(information.process);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                while (!File.Exists(done) && DateTime.UtcNow < deadline) Thread.Sleep(50);
                if (!File.Exists(done)) throw new Exception("Linking the game's files with /bin/sh did not finish.");
            }
            finally
            {
                try { File.Delete(script); } catch { }
                try { File.Delete(done); } catch { }
            }
        }
    }

    /// The launcher's look (after the Epic Games launcher): a charcoal window with rounded panels
    /// and cards, white text with grey secondary text, white main buttons and grey rounded ones.
    static class Dark
    {
        public static readonly Color Back = Color.FromArgb(17, 18, 22);       // the window
        public static readonly Color Panel = Color.FromArgb(22, 24, 29);      // the settings pages
        public static readonly Color Sidebar = Color.FromArgb(13, 14, 18);    // the settings' category list
        public static readonly Color Card = Color.FromArgb(28, 30, 36);       // cards on a page
        public static readonly Color Field = Color.FromArgb(32, 35, 42);
        public static readonly Color Button = Color.FromArgb(32, 35, 42);
        public static readonly Color ButtonHover = Color.FromArgb(44, 48, 56);
        public static readonly Color ButtonDown = Color.FromArgb(26, 28, 34);
        public static readonly Color Hover = Color.FromArgb(30, 33, 40);      // list rows under the mouse
        public static readonly Color Line = Color.FromArgb(38, 41, 48);       // between settings rows
        public static readonly Color Disabled = Color.FromArgb(96, 101, 112);
        public static readonly Color Accent = Color.FromArgb(178, 24, 36);
        public static readonly Color AccentHover = Color.FromArgb(206, 40, 52);
        public static readonly Color AccentText = Color.FromArgb(240, 92, 76);
        public static readonly Color Heading = Color.FromArgb(128, 134, 146);
        public static readonly Color Border = Color.FromArgb(44, 47, 55);
        public static readonly Color Text = Color.FromArgb(236, 238, 242);
        public static readonly Color DimText = Color.FromArgb(132, 138, 150);
        public const int Radius = 4;

        static readonly bool HasBahnschrift = FontFamily.Families.Any(f => f.Name == "Bahnschrift SemiBold");
        static readonly Dictionary<float, Font> caps = new Dictionary<float, Font>();

        /// The capitals font of headings and buttons: Bahnschrift SemiBold (Windows 10 and later).
        public static Font Caps(float size)
        {
            Font font;
            if (!caps.TryGetValue(size, out font))
                caps[size] = font = HasBahnschrift ? new Font("Bahnschrift SemiBold", size) : new Font("Segoe UI Semibold", size);
            return font;
        }

        /// The text with a hair space between its letters (spaced capitals).
        public static string Spaced(string text)
        {
            return string.Join(((char)0x200A).ToString(), text.Select(c => c.ToString()).ToArray());
        }

        public static void Apply(Control control)
        {
            // Other controls take their parent's color (a card's, the window's) unless they set one.
            if (control is Form) control.BackColor = Back;
            if (control.Tag as string != "heading") control.ForeColor = Text;
            var button = control as Button;
            if (button != null && !(button is GlowButton)) Round(button);
            var box = control as TextBox;
            if (box != null)
            {
                box.BackColor = Field;
                if (box.BorderStyle == BorderStyle.Fixed3D) box.BorderStyle = BorderStyle.FixedSingle;
                box.HandleCreated += delegate { Native.SetWindowTheme(box.Handle, "DarkMode_Explorer", null); };
            }
            var number = control as NumericUpDown;
            if (number != null)
            {
                number.BackColor = Field;
                number.BorderStyle = BorderStyle.None;
                // The up and down buttons, drawn over in the field's colors.
                var spin = number.Controls.Count > 0 ? number.Controls[0] : null;
                if (spin != null && !(spin is TextBoxBase)) spin.Paint += (sender, e) => PaintSpin(spin, e.Graphics);
            }
            var check = control as CheckBox;
            if (check != null && !(check is DarkCheckBox)) check.FlatStyle = FlatStyle.Flat;
            var combo = control as ComboBox;
            if (combo != null) DarkCombo(combo);
            var scroll = control as ScrollableControl;
            if (scroll != null && scroll.AutoScroll)
                scroll.HandleCreated += delegate { Native.SetWindowTheme(scroll.Handle, "DarkMode_Explorer", null); };
            foreach (Control child in control.Controls) Apply(child);
        }

        /// A dark title bar (Windows 10 2004 and later; ignored before).
        public static void TitleBar(Form form)
        {
            int on = 1;
            if (Native.DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0)
                Native.DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
        }

        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRounded(Graphics g, Color color, RectangleF r, float radius)
        {
            using (var path = RoundedRect(r, radius))
            using (var brush = new SolidBrush(color))
                g.FillPath(brush, path);
        }

        /// Copies the part of `picture` being painted, unscaled; `offset` is where the painted control
        /// sits in the picture.
        public static void CopyArea(Bitmap picture, Point offset, Graphics g)
        {
            var area = Rectangle.Intersect(Rectangle.Ceiling(g.ClipBounds), new Rectangle(-offset.X, -offset.Y, picture.Width, picture.Height));
            if (area.Width <= 0 || area.Height <= 0) return;
            var mode = g.CompositingMode;
            var interpolation = g.InterpolationMode;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(picture, area, new Rectangle(area.X + offset.X, area.Y + offset.Y, area.Width, area.Height), GraphicsUnit.Pixel);
            g.CompositingMode = mode;
            g.InterpolationMode = interpolation;
        }

        /// Paints what lies under `control`: the banner picture, or the nearest opaque parent's color.
        public static void PaintUnder(Control control, Graphics g)
        {
            // See-through cards between the control and what is under them, innermost first.
            var glass = new List<Card>();
            for (var parent = control.Parent; parent != null; parent = parent.Parent)
            {
                var card = parent as Card;
                if (card != null) { card.DrawUnder(control, g); FillGlass(glass, control, g); return; } // PERF-GTS-436
                var backdrop = parent as IBackdrop;
                if (backdrop != null && backdrop.DrawBackdrop(control, g, false)) { FillGlass(glass, control, g); return; }
                if (parent.BackColor.A == 255)
                {
                    using (var back = new SolidBrush(parent.BackColor)) g.FillRectangle(back, control.ClientRectangle);
                    FillGlass(glass, control, g);
                    return;
                }
            }
            g.Clear(Back);
            FillGlass(glass, control, g);
        }

        static void FillGlass(List<Card> glass, Control control, Graphics g)
        {
            for (int i = glass.Count - 1; i >= 0; --i)
            {
                using (var fill = new SolidBrush(glass[i].Fill)) g.FillRectangle(fill, control.ClientRectangle);
            }
        }

        /// Gives the controls that take their parent's color a transparent one, so the window's
        /// picture shows through them (the cards, fields, buttons and lists paint their own).
        public static void SeeThrough(Control control)
        {
            foreach (Control child in control.Controls)
            {
                bool own = child is Card || child is TextBoxBase || child is ListControl || child is ButtonBase ||
                           child is TrackBar || child is UpDownBase || child is DarkBar ||
                           child is Slider || child is ToggleRow ||
                           child is DataGridView; // FIX-GTS-434: cannot be see-through (ArgumentException)
                if (!own && !TypeDescriptor.GetProperties(child)["BackColor"].ShouldSerializeValue(child))
                    child.BackColor = Color.Transparent;
                if (!(child is TextBoxBase) && !(child is ListControl) && !(child is UpDownBase) && !(child is DataGridView)) SeeThrough(child);
            }
        }

        /// Draws a button as a rounded rectangle: white with dark text for the main action (Tag
        /// "accent"), grey otherwise; Tag "nav" is a top bar tab and "nav-on" the open one.
        static void Round(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.UseVisualStyleBackColor = false;
            button.Cursor = Cursors.Hand;
            if (button.Padding == Padding.Empty) button.Padding = new Padding(18, 7, 18, 7);
            if (button.Font.Name == "Segoe UI" && !(button.Tag is string && (string)button.Tag == "icon")) button.Font = Caps(9f);
            bool hover = false, down = false;
            button.MouseEnter += delegate { hover = true; button.Invalidate(); };
            button.MouseLeave += delegate { hover = false; down = false; button.Invalidate(); };
            button.MouseDown += delegate { down = true; button.Invalidate(); };
            button.MouseUp += delegate { down = false; button.Invalidate(); };
            button.Paint += (sender, e) => PaintButton(button, e.Graphics, hover, down);
        }

        static void PaintButton(Button button, Graphics g, bool hover, bool down)
        {
            PaintUnder(button, g);
            string kind = button.Tag as string;
            bool accent = kind == "accent", icon = kind == "icon";
            Color fill = Color.Empty, edge, text = Text;
            if (!button.Enabled)
            {
                edge = Color.FromArgb(52, 55, 62);
                text = Disabled;
            }
            else if (accent)
            {
                fill = down ? Color.FromArgb(200, 203, 210) : hover ? Color.White : Color.FromArgb(232, 234, 238);
                edge = fill;
                text = Back;
            }
            else
            {
                fill = down ? Color.FromArgb(10, 255, 255, 255) : hover ? Color.FromArgb(22, 255, 255, 255) : Color.Empty;
                edge = hover ? Color.FromArgb(150, 156, 168) : Color.FromArgb(72, 77, 88);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, button.Width - 1f, button.Height - 1f);
            if (!fill.IsEmpty) FillRounded(g, fill, r, 3);
            using (var path = RoundedRect(r, 3))
            using (var pen = new Pen(edge))
                g.DrawPath(pen, path);
            string label = icon ? button.Text : button.Text.ToUpperInvariant();
            TextRenderer.DrawText(g, label, button.Font, button.ClientRectangle, text,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        static void PaintSpin(Control spin, Graphics g)
        {
            using (var back = new SolidBrush(Button)) g.FillRectangle(back, spin.ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = spin.Width / 2f, half = spin.Height / 2f;
            using (var pen = new Pen(spin.Enabled ? Color.FromArgb(200, 200, 200) : Color.FromArgb(100, 100, 100), 1.5f))
            {
                g.DrawLines(pen, new[] { new PointF(x - 3.5f, half / 2 + 2), new PointF(x, half / 2 - 1.5f), new PointF(x + 3.5f, half / 2 + 2) });
                g.DrawLines(pen, new[] { new PointF(x - 3.5f, half + half / 2 - 2), new PointF(x, half + half / 2 + 1.5f), new PointF(x + 3.5f, half + half / 2 - 2) });
            }
        }

        /// A drop-down list drawn dark: the closed box and the open list both in the field color.
        static void DarkCombo(ComboBox combo)
        {
            combo.BackColor = Field;
            if (combo.DrawMode != DrawMode.Normal) return;
            combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.ItemHeight = 18;
            combo.DrawItem += (sender, e) =>
            {
                bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
                bool selected = (e.State & DrawItemState.Selected) != 0 && !edit;
                using (var back = new SolidBrush(selected ? ButtonHover : Field)) e.Graphics.FillRectangle(back, e.Bounds);
                if (e.Index < 0) return;
                var bounds = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, combo.GetItemText(combo.Items[e.Index]), combo.Font, bounds,
                                      combo.Enabled ? Text : Color.FromArgb(112, 112, 112),
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                      TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            };
            combo.HandleCreated += delegate { Native.SetWindowTheme(combo.Handle, "DarkMode_CFD", null); };
        }
    }

    /// A window that paints a picture behind its controls (the launcher: launcher\background.png).
    interface IBackdrop
    {
        /// The picture as it lies under `control`; `raw` without the darkening. False without one.
        bool DrawBackdrop(Control control, Graphics g, bool raw);
    }

    /// A check box drawn in the loader's colors: a dark rounded square, red with a white tick when checked.
    class DarkCheckBox : CheckBox
    {
        public bool BoxRight; // the box at the row's right edge, the text at its left

        public DarkCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var text = TextRenderer.MeasureText(Text, Font);
            return new Size(text.Width + 26, Math.Max(text.Height, 18) + 4);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Dark.PaintUnder(this, e.Graphics);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            OnPaintBackground(e);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int size = 16;
            var box = new RectangleF(BoxRight ? Width - size - 1.5f : 1.5f, (Height - size) / 2 + 0.5f, size - 1, size - 1);
            bool hover = ClientRectangle.Contains(PointToClient(Cursor.Position));
            Dark.FillRounded(g, !Enabled ? Color.FromArgb(36, 36, 36) : Checked ? (hover ? Dark.AccentHover : Dark.Accent) : Dark.Field, box, 4);
            if (!Checked)
            {
                using (var path = Dark.RoundedRect(box, 4))
                using (var border = new Pen(!Enabled ? Color.FromArgb(58, 58, 58) : hover ? Color.FromArgb(140, 140, 140) : Color.FromArgb(88, 88, 88)))
                    g.DrawPath(border, path);
            }
            else
            {
                using (var tick = new Pen(Enabled ? Color.White : Color.FromArgb(140, 140, 140), 2f))
                {
                    g.DrawLines(tick, new[]
                    {
                        new PointF(box.Left + 3.5f, box.Top + 8f),
                        new PointF(box.Left + 6.5f, box.Top + 11f),
                        new PointF(box.Left + 11.5f, box.Top + 4.5f)
                    });
                }
            }
            TextRenderer.DrawText(g, Text, Font, new Point(BoxRight ? 0 : size + 8, (Height - TextRenderer.MeasureText(Text, Font).Height) / 2),
                                  Enabled ? ForeColor : Color.FromArgb(112, 112, 112), TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
    }

    /// A drop-down list with a rounded dark frame and arrow, drawn over the flat combo box's
    /// white border once Windows has painted it (its items are drawn by Dark.Apply).
    class DarkComboBox : ComboBox
    {
        bool hover;

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        // Windows redraws parts of the box for these without a full paint; paint the frame again.
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0xF || !IsHandleCreated) return; // WM_PAINT
            using (var g = Graphics.FromHwnd(Handle))
            {
                var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
                using (var outside = new Region(ClientRectangle))
                using (var path = Dark.RoundedRect(r, 6))
                {
                    outside.Exclude(path);
                    g.SetClip(outside, CombineMode.Replace);
                    Dark.PaintUnder(this, g);
                    g.ResetClip();
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    // The flat style's inner white line, then the arrow's area.
                    using (var inner = new Pen(Dark.Field)) g.DrawRectangle(inner, 1, 1, Width - 3, Height - 3);
                    var arrow = new RectangleF(Width - 22, 2, 20, Height - 4);
                    using (var field = new SolidBrush(Dark.Field)) g.FillRectangle(field, arrow);
                    using (var pen = new Pen(!Enabled ? Color.FromArgb(52, 52, 52) : Focused || DroppedDown ? Color.FromArgb(120, 120, 120) :
                                             hover ? Color.FromArgb(96, 96, 96) : Color.FromArgb(62, 62, 62)))
                        g.DrawPath(pen, path);
                    float x = arrow.X + arrow.Width / 2 - 1, y = Height / 2f;
                    using (var pen = new Pen(Enabled ? Color.FromArgb(200, 200, 200) : Color.FromArgb(100, 100, 100), 1.5f))
                        g.DrawLines(pen, new[] { new PointF(x - 4, y - 2), new PointF(x, y + 2), new PointF(x + 4, y - 2) });
                }
            }
        }
    }

    /// A rounded panel with a thin border; the controls on it take its color.
    class Card : TableLayoutPanel
    {
        public float Radius = Dark.Radius;
        public Color Edge = Dark.Border;
        /// The card's color; a translucent one lets the window's picture show through it.
        public readonly Color Fill;

        public Card(Color fill)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Fill = fill;
            BackColor = fill.A == 255 ? fill : Color.Transparent;
            ColumnCount = 1;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        }

        // PERF-GTS-436: WinForms paints a see-through label or row by painting its parent card under
        // it first, so a card was drawn again (picture, glass, rounded border) for every row on it,
        // several times per scroll step. The card is drawn once into this picture, kept until it
        // moves in the window or changes size; the repaints copy it.
        Bitmap look;
        Point lookAt;

        Bitmap Look()
        {
            var form = FindForm();
            var at = form != null ? form.PointToClient(PointToScreen(Point.Empty)) : Point.Empty;
            if (look != null && look.Size == Size && lookAt == at) return look;
            if (look != null) look.Dispose();
            look = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(look))
            {
                Dark.PaintUnder(this, g);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
                using (var path = Dark.RoundedRect(r, Radius))
                {
                    using (var fill = new SolidBrush(Fill)) g.FillPath(fill, path);
                    if (!Edge.IsEmpty) using (var pen = new Pen(Edge)) g.DrawPath(pen, path);
                }
            }
            lookAt = at;
            return look;
        }

        /// The card as it lies under `control` (the card itself or a control on it).
        public void DrawUnder(Control control, Graphics g)
        {
            var offset = control == this ? Point.Empty : PointToClient(control.PointToScreen(Point.Empty));
            Dark.CopyArea(Look(), offset, g);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            DrawUnder(this, e.Graphics);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && look != null) { look.Dispose(); look = null; }
            base.Dispose(disposing);
        }
    }

    /// A setting that is on or off: its text on the left, a switch on the right, a line under it.
    class ToggleRow : CheckBox
    {
        bool hover;
        float knob = -1; // 0 off .. 1 on, while it slides
        int version;

        public ToggleRow()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            AutoSize = false;
            Dock = DockStyle.Fill;
            Height = 40;
            Margin = new Padding(0);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            return new Size(TextRenderer.MeasureText(Text, Font).Width + 70, 40);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Dark.PaintUnder(this, e.Graphics);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            OnPaintBackground(e);
            if (hover && Enabled)
                using (var lit = new SolidBrush(Color.FromArgb(8, 255, 255, 255))) g.FillRectangle(lit, ClientRectangle);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width - 56, Height - 1), Enabled ? Dark.Text : Dark.Disabled,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new RectangleF(Width - 40, (Height - 1 - 18) / 2f, 36, 18);
            float on = knob < 0 ? (Checked ? 1 : 0) : knob;
            Color trackColor = !Enabled ? Color.FromArgb(36, 39, 46) : Anim.Mix(Color.FromArgb(52, 56, 64), Color.FromArgb(232, 234, 238), on);
            Dark.FillRounded(g, trackColor, track, 9);
            const float size = 12;
            float x = track.X + 3 + (track.Width - size - 6) * on;
            Color knobColor = !Enabled ? Color.FromArgb(70, 74, 84) : Anim.Mix(Color.FromArgb(150, 155, 166), Dark.Back, on);
            using (var fill = new SolidBrush(knobColor)) g.FillEllipse(fill, x, track.Y + 3, size, size);
            using (var line = new Pen(Dark.Line)) g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            if (!Visible || !IsHandleCreated) { knob = -1; return; }
            int mine = ++version;
            float from = knob < 0 ? (Checked ? 0 : 1) : knob, to = Checked ? 1 : 0;
            int start = Environment.TickCount;
            Anim.Run(() =>
            {
                if (mine != version || IsDisposed) return false;
                float t = Math.Min(1, (Environment.TickCount - start) / 140f);
                knob = from + (to - from) * Anim.Ease(t);
                Invalidate();
                if (t >= 1) knob = -1;
                return t < 1;
            });
        }
    }

    /// A setting with a control (a drop-down, a slider, a button): its name on the left, the control
    /// on the right, a line under it. The name greys out with the control.
    class SettingRow : TableLayoutPanel
    {
        readonly Label label;
        readonly Control control;

        /// The row's line goes elsewhere (under a hint that belongs to the setting).
        public bool NoLine;

        public SettingRow(string text, Control control)
        {
            this.control = control;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            ColumnCount = 2;
            RowCount = 1;
            Dock = DockStyle.Fill;
            Margin = new Padding(0);
            Padding = new Padding(0, 0, 0, 1);
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            label = new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0), BackColor = Color.Transparent, Tag = "heading" };
            Controls.Add(label, 0, 0);
            control.Anchor = AnchorStyles.Right;
            control.Margin = new Padding(12, 0, 0, 0);
            Controls.Add(control, 1, 0);
            control.EnabledChanged += delegate { ShowEnabled(); };
            ShowEnabled();
            // A table sizes its rows from this height (a new one is 100 high).
            Height = GetPreferredSize(Size.Empty).Height;
            control.SizeChanged += delegate { Height = GetPreferredSize(Size.Empty).Height; };
        }

        void ShowEnabled()
        {
            label.ForeColor = control.Enabled ? Dark.Text : Dark.Disabled;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            // From the label and the control (a table offered more height would take all of it).
            var text = label.GetPreferredSize(Size.Empty);
            var right = control.AutoSize ? control.GetPreferredSize(Size.Empty) : control.Size;
            return new Size(text.Width + control.Margin.Horizontal + right.Width, Math.Max(40, Math.Max(text.Height, right.Height) + 12));
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Dark.PaintUnder(this, e.Graphics);
            if (NoLine) return;
            using (var line = new Pen(Dark.Line)) e.Graphics.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }
    }

    /// A thin slider: a line filled up to the value, a round knob, and the value on its right.
    class Slider : Control
    {
        int minimum, maximum = 100, value;
        bool dragging;
        public int TickFrequency = 10, SmallChange = 1, LargeChange = 10;
        /// Shown after the value ("%").
        public string Suffix = "";
        public event EventHandler ValueChanged;

        public Slider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(280, 28);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public int Minimum { get { return minimum; } set { minimum = value; Value = this.value; Invalidate(); } }
        public int Maximum { get { return maximum; } set { maximum = value; Value = this.value; Invalidate(); } }

        public int Value
        {
            get { return value; }
            set
            {
                int v = Math.Max(minimum, Math.Min(maximum, value));
                if (v == this.value) return;
                this.value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        Rectangle Track { get { return new Rectangle(8, Height / 2 - 1, Math.Max(10, Width - 8 - 64), 2); } }

        void SetFromX(int x)
        {
            var track = Track;
            float amount = Math.Max(0, Math.Min(1, (x - track.X) / (float)track.Width));
            Value = minimum + (int)Math.Round(amount * (maximum - minimum));
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); dragging = true; Capture = true; SetFromX(e.X); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) SetFromX(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) Value -= SmallChange;
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) Value += SmallChange;
            else if (e.KeyCode == Keys.PageDown) Value -= LargeChange;
            else if (e.KeyCode == Keys.PageUp) Value += LargeChange;
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Dark.PaintUnder(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = Track;
            float amount = maximum > minimum ? (value - minimum) / (float)(maximum - minimum) : 0;
            float x = track.X + track.Width * amount;
            using (var rest = new SolidBrush(Color.FromArgb(62, 66, 76))) g.FillRectangle(rest, track);
            using (var done = new SolidBrush(Enabled ? Color.FromArgb(220, 223, 230) : Dark.Disabled))
                g.FillRectangle(done, track.X, track.Y, x - track.X, track.Height);
            float knob = Focused ? 14 : 12;
            using (var fill = new SolidBrush(Enabled ? Color.White : Dark.Disabled)) g.FillEllipse(fill, x - knob / 2, track.Y + 1 - knob / 2, knob, knob);
            TextRenderer.DrawText(g, value + Suffix, Dark.Caps(9f), new Rectangle(track.Right + 8, 0, Width - track.Right - 8, Height),
                                  Enabled ? Dark.Text : Dark.Disabled, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// A category in the settings' sidebar: grey text, white with a red bar on its left when open.
    class SidebarItem : Control
    {
        bool active, hover;

        public SidebarItem(string text)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Text = text;
            Height = 38;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI", 10.5f);
            TabStop = true;
        }

        public bool Active { get { return active; } set { active = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var back = new SolidBrush(Dark.Sidebar)) g.FillRectangle(back, ClientRectangle);
            if (active)
                using (var bar = new SolidBrush(Dark.Accent)) g.FillRectangle(bar, 16, 6, 3, Height - 12);
            Color text = active ? Color.White : hover ? Color.FromArgb(206, 210, 218) : Dark.DimText;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(30, 0, Width - 34, Height), text,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// The launcher's animations: one timer (about 60 times a second) that calls each running
    /// animation's step until the step says it is done.
    static class Anim
    {
        static readonly List<Func<bool>> running = new List<Func<bool>>();
        static System.Windows.Forms.Timer timer;

        /// Runs `step` every tick while it returns true.
        public static void Run(Func<bool> step)
        {
            running.Add(step);
            if (timer == null)
            {
                timer = new System.Windows.Forms.Timer { Interval = 15 };
                timer.Tick += delegate
                {
                    for (int i = running.Count - 1; i >= 0; --i)
                    {
                        bool more;
                        try { more = running[i](); } catch { more = false; }
                        if (!more) running.RemoveAt(i);
                    }
                    if (running.Count == 0) timer.Stop();
                };
            }
            if (!timer.Enabled) timer.Start();
        }

        /// Ease-out (fast start, gentle end) of 0..1.
        public static float Ease(float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return 1 - (1 - t) * (1 - t) * (1 - t);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
    }

    /// A home view button: outlined, with a light sweeping across it now and then; under the mouse
    /// (or with the keyboard focus) it fills white, and the home view draws a glow around it.
    class GlowButton : Button
    {
        float hot;
        int version;
        bool over, pressing;
        /// The text is a Segoe MDL2 Assets glyph (the settings button), not a word in capitals.
        public bool Glyph;
        static readonly int sweepStart = Environment.TickCount;

        public GlowButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
        }

        /// 0..1: how far it has filled white.
        public float Hot { get { return hot; } }

        void Animate()
        {
            int mine = ++version;
            float from = hot, to = (over || (Focused && ShowFocusCues)) && Enabled ? 1 : 0;
            int start = Environment.TickCount;
            Anim.Run(() =>
            {
                if (mine != version || IsDisposed) return false;
                float t = Math.Min(1, (Environment.TickCount - start) / 170f);
                hot = from + (to - from) * Anim.Ease(t);
                Invalidate();
                if (Parent != null) Parent.Invalidate(Rectangle.Inflate(Bounds, 16, 16), false);
                return t < 1;
            });
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); over = true; Animate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); over = false; pressing = false; Animate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressing = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressing = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Animate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Animate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Animate(); }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Dark.PaintUnder(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            float h = Enabled ? hot : 0;
            Color fill = !Enabled ? Color.FromArgb(90, 40, 43, 50)
                       : Anim.Mix(Color.FromArgb(120, 10, 11, 14), pressing ? Color.FromArgb(214, 217, 224) : Color.White, h);
            Color edge = !Enabled ? Color.FromArgb(52, 55, 62) : Anim.Mix(Color.FromArgb(214, 218, 226), Color.White, h);
            Color text = !Enabled ? Dark.Disabled : Anim.Mix(Color.White, Dark.Back, h);
            using (var path = Dark.RoundedRect(r, 3))
            {
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                // The sweeping light: 1.1 s across, every 3.5 s, while not filled.
                double phase = ((Environment.TickCount - sweepStart) % 3500) / 1100.0;
                if (Enabled && h < 0.99f && phase < 1)
                {
                    float band = 70, x = r.X - band + (float)phase * (r.Width + band);
                    var bandRect = new RectangleF(x, r.Y, band, r.Height);
                    using (var light = new LinearGradientBrush(bandRect, Color.Transparent, Color.Transparent, 0f))
                    {
                        var blend = new ColorBlend
                        {
                            Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb((int)(70 * (1 - h)), 255, 255, 255), Color.FromArgb(0, 255, 255, 255) },
                            Positions = new[] { 0f, 0.5f, 1f }
                        };
                        light.InterpolationColors = blend;
                        var clip = g.Clip;
                        g.SetClip(path, CombineMode.Intersect);
                        g.FillRectangle(light, bandRect);
                        g.Clip = clip;
                    }
                }
                using (var pen = new Pen(edge)) g.DrawPath(pen, path);
            }
            string label = Glyph ? Text : Dark.Spaced(Text.ToUpperInvariant());
            TextRenderer.DrawText(g, label, Font, ClientRectangle, text,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// The launcher's home view, drawn in one piece: the pictures (drifting slowly sideways and
    /// fading from one to the next every 10 seconds), darkened toward the bottom left, the logo
    /// or the title, the caption, the status and the version, with glows around its buttons.
    class HomeView : Control, IBackdrop
    {
        readonly List<Image> pictures;
        readonly GlowButton[] buttons;
        public Image Logo;
        public string Title = "", Accent = "", Caption = "", Status = "", Version = "";
        readonly Dictionary<int, Bitmap> scaled = new Dictionary<int, Bitmap>();
        Size scaledFor;
        Bitmap overlay, frame;
        int current, next = -1, fadeFrom, shownSince;
        readonly int started = Environment.TickCount;
        readonly System.Windows.Forms.Timer ticker;
        const int ShowMs = 10000, FadeMs = 1400;
        const int Left_ = 56, Bottom_ = 46, Gap = 10;

        public HomeView(List<Image> pictures, params GlowButton[] buttons)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            this.pictures = pictures;
            this.buttons = buttons;
            BackColor = Dark.Back;
            foreach (var button in buttons) Controls.Add(button);
            shownSince = Environment.TickCount;
            ticker = new System.Windows.Forms.Timer { Interval = 33 };
            ticker.Tick += delegate { Tick(); };
            VisibleChanged += delegate { if (Visible) ticker.Start(); else ticker.Stop(); };
            ticker.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { ticker.Dispose(); foreach (var b in scaled.Values) b.Dispose(); if (overlay != null) overlay.Dispose(); if (frame != null) frame.Dispose(); }
            base.Dispose(disposing);
        }

        void Tick()
        {
            var form = FindForm();
            if (!Visible || form == null || form.WindowState == FormWindowState.Minimized) return;
            if (pictures.Count > 1 && next < 0 && Environment.TickCount - shownSince > ShowMs)
            {
                next = (current + 1) % pictures.Count;
                fadeFrom = Environment.TickCount;
            }
            Compose();
            Invalidate(true);
        }

        Bitmap Scaled(int index)
        {
            Bitmap cached;
            if (scaled.TryGetValue(index, out cached)) return cached;
            var image = pictures[index];
            // Covering the view with 6% to spare, for the drift.
            float scale = 1.06f * Math.Max(Width / (float)image.Width, Height / (float)image.Height);
            int width = (int)Math.Ceiling(image.Width * scale), height = (int)Math.Ceiling(image.Height * scale);
            cached = new Bitmap(width, height);
            using (var g = Graphics.FromImage(cached))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, 0, 0, width, height);
            }
            scaled[index] = cached;
            return cached;
        }

        Bitmap Overlay()
        {
            if (overlay != null) return overlay;
            overlay = new Bitmap(Width, Height);
            var r = new Rectangle(0, 0, Width, Height);
            using (var g = Graphics.FromImage(overlay))
            {
                using (var dim = new SolidBrush(Color.FromArgb(40, 6, 7, 9))) g.FillRectangle(dim, r);
                using (var left = new LinearGradientBrush(r, Color.FromArgb(215, 8, 9, 12), Color.FromArgb(0, 8, 9, 12), 0f))
                {
                    left.Blend = new Blend { Factors = new[] { 0f, 0.35f, 1f, 1f }, Positions = new[] { 0f, 0.3f, 0.65f, 1f } };
                    g.FillRectangle(left, r);
                }
                using (var bottom = new LinearGradientBrush(r, Color.FromArgb(0, 8, 9, 12), Color.FromArgb(225, 8, 9, 12), 90f))
                {
                    bottom.Blend = new Blend { Factors = new[] { 0f, 0f, 1f }, Positions = new[] { 0f, 0.45f, 1f } };
                    g.FillRectangle(bottom, r);
                }
            }
            return overlay;
        }

        void DrawPicture(Graphics g, int index, float alpha, float drift)
        {
            var image = Scaled(index);
            int x = -(int)Math.Round((image.Width - Width) * drift);
            int y = (Height - image.Height) / 12;
            if (alpha >= 1) { g.DrawImageUnscaled(image, x, y); return; }
            using (var attributes = new System.Drawing.Imaging.ImageAttributes())
            {
                attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha });
                g.DrawImage(image, new Rectangle(x, y, image.Width, image.Height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            }
        }

        /// This moment's picture (with the darkening) into `frame`.
        void Compose()
        {
            if (Width <= 0 || Height <= 0) return;
            if (scaledFor != Size)
            {
                foreach (var b in scaled.Values) b.Dispose();
                scaled.Clear();
                if (overlay != null) { overlay.Dispose(); overlay = null; }
                if (frame != null) { frame.Dispose(); frame = null; }
                scaledFor = Size;
            }
            if (frame == null) frame = new Bitmap(Width, Height);
            using (var g = Graphics.FromImage(frame))
            {
                if (pictures.Count == 0)
                {
                    using (var glow = new LinearGradientBrush(ClientRectangle, Color.FromArgb(14, 15, 19), Color.FromArgb(52, 14, 20), 0f))
                        g.FillRectangle(glow, ClientRectangle);
                    return;
                }
                // Side to side and back every 48 seconds, slowest at the ends.
                float drift = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * ((Environment.TickCount - started) / 48000.0)));
                DrawPicture(g, current, 1, drift);
                if (next >= 0)
                {
                    float t = (Environment.TickCount - fadeFrom) / (float)FadeMs;
                    DrawPicture(g, next, Math.Min(1, t), drift);
                    if (t >= 1) { current = next; next = -1; shownSince = Environment.TickCount; }
                }
                g.DrawImageUnscaled(Overlay(), 0, 0);
            }
        }

        public bool DrawBackdrop(Control control, Graphics g, bool raw)
        {
            if (frame == null || frame.Size != Size) Compose();
            if (frame == null) return false;
            var at = control == this ? Point.Empty : control.Parent == this ? control.Location : PointToClient(control.PointToScreen(Point.Empty));
            g.DrawImageUnscaled(frame, -at.X, -at.Y);
            return true;
        }

        int ButtonsTop { get { return Height - Bottom_ - 30 - buttons[0].Height; } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int x = Left_;
            foreach (var button in buttons)
            {
                button.Location = new Point(x, ButtonsTop);
                x += button.Width + Gap;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            DrawBackdrop(this, g, false);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // The glow around a button under the mouse, outside the button itself.
            foreach (var button in buttons)
            {
                if (button.Hot <= 0.01f || !button.Enabled) continue;
                var r = new RectangleF(button.Left, button.Top, button.Width, button.Height);
                for (int i = 14; i >= 1; i -= 1)
                {
                    int alpha = (int)(button.Hot * 34 * Math.Pow(1 - i / 15.0, 2));
                    using (var path = Dark.RoundedRect(RectangleF.Inflate(r, i, i), 3 + i))
                    using (var pen = new Pen(Color.FromArgb(alpha, 255, 255, 255), 2f))
                        g.DrawPath(pen, path);
                }
            }
            int top = ButtonsTop;
            // The caption above the buttons, the logo (or title) above it.
            var captionFont = Dark.Caps(8.5f);
            var captionSize = TextRenderer.MeasureText(Caption, captionFont);
            int captionY = top - 22 - captionSize.Height;
            TextRenderer.DrawText(g, Caption, captionFont, new Point(Left_ + 3, captionY), Color.FromArgb(190, 194, 204), TextFormatFlags.NoPrefix);
            if (Logo != null)
            {
                int w = 330, h = w * Logo.Height / Logo.Width;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(Logo, new Rectangle(Left_, captionY - 12 - h, w, h));
            }
            else
            {
                var font = Dark.Caps(34f);
                var size = TextRenderer.MeasureText(Title + " ", font);
                int y = captionY - size.Height;
                TextRenderer.DrawText(g, Title + " ", font, new Point(Left_, y), Color.White, TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, Accent, font, new Point(Left_ + size.Width - 8, y), Dark.AccentText, TextFormatFlags.NoPrefix);
            }
            // The status under the buttons, the version at the bottom right.
            TextRenderer.DrawText(g, Status, Font, new Rectangle(Left_ + 2, top + buttons[0].Height + 12, Math.Max(100, Width - Left_ - 260), 40),
                                  Color.FromArgb(200, 204, 212), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
            var versionFont = Dark.Caps(9f);
            var versionSize = TextRenderer.MeasureText(Version, versionFont);
            TextRenderer.DrawText(g, Version, versionFont, new Point(Width - versionSize.Width - 30, Height - versionSize.Height - 24),
                                  Color.FromArgb(200, 204, 212), TextFormatFlags.NoPrefix);
        }
    }

    /// Covers the window while it changes views: the old view darkens, then the new one appears.
    class Curtain : Control
    {
        Bitmap picture;
        float dark;

        public Curtain()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Black;
        }

        public void Show(Bitmap image, float darkness)
        {
            picture = image;
            dark = darkness;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (picture != null) g.DrawImageUnscaled(picture, 0, 0);
            else g.Clear(Dark.Back);
            using (var shade = new SolidBrush(Color.FromArgb((int)(255 * Math.Max(0, Math.Min(1, dark))), 6, 7, 9)))
                g.FillRectangle(shade, ClientRectangle);
        }
    }

    /// A level meter in the launcher's colors (the live input of the wheel window): a dark rounded
    /// track, filled red up to Value.
    class DarkBar : Control
    {
        int minimum, maximum = 100, value;

        public DarkBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
        }

        public int Minimum { get { return minimum; } set { minimum = value; Invalidate(); } }
        public int Maximum { get { return maximum; } set { maximum = value; Invalidate(); } }

        public int Value
        {
            get { return value; }
            set { if (value != this.value) { this.value = value; Invalidate(); } }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Dark.PaintUnder(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new RectangleF(0, 0, Width - 0.5f, Height - 0.5f);
            Dark.FillRounded(g, Dark.Field, track, Height / 2f);
            int range = maximum - minimum;
            float amount = range > 0 ? Math.Max(0, Math.Min(1, (value - minimum) / (float)range)) : 0;
            if (amount > 0)
                Dark.FillRounded(g, Dark.Accent, new RectangleF(0, 0, Math.Max(Height, (Width - 0.5f) * amount), Height - 0.5f), Height / 2f);
        }
    }

    sealed class SetupDumpException : Exception
    {
        public SetupDumpException(string message) : base(message) { }
    }

    /// Unwraps an unencrypted (fake-signed) PS4 SELF into a plain ELF.
    static class Self
    {
        public static bool IsElf(byte[] d)
        {
            return d.Length >= 4 && d[0] == 0x7f && d[1] == (byte)'E' && d[2] == (byte)'L' && d[3] == (byte)'F';
        }

        public static byte[] ToElf(byte[] d, string name)
        {
            if (IsElf(d)) return d;
            if (d.Length < 32 || d[0] != 0x4f || d[1] != 0x15 || d[2] != 0x3d || d[3] != 0x1d)
                throw new SetupDumpException(name + " is neither a SELF nor an ELF file.\n\n" +
                    "Re-dump your game/update and the PS4 sys_modules folder. Select the new dump folders, then run Set up again.");
            int count = BitConverter.ToUInt16(d, 24);
            var entries = new Dictionary<long, long[]>();
            for (int i = 0; i < count; i++)
            {
                long props = (long)BitConverter.ToUInt64(d, 32 + i * 32);
                long offset = (long)BitConverter.ToUInt64(d, 40 + i * 32);
                long size = (long)BitConverter.ToUInt64(d, 48 + i * 32);
                if ((props & 0xA) != 0)
                    throw new Exception(name + " has encrypted or compressed segments. The dump must be decrypted.");
                if ((props & 0x800) != 0) entries[(props >> 20) & 0xFFF] = new[] { offset, size };
            }
            int elf = 32 + count * 32;
            long phoff = (long)BitConverter.ToUInt64(d, elf + 0x20);
            int phnum = BitConverter.ToUInt16(d, elf + 0x38);
            if (phoff != 0x40) throw new Exception(name + ": unexpected ELF header layout.");
            int headerLength = 0x40 + phnum * 0x38;
            long outSize = headerLength;
            var headers = new List<long[]>();
            for (int i = 0; i < phnum; i++)
            {
                int b = elf + 0x40 + i * 0x38;
                long type = BitConverter.ToUInt32(d, b);
                long offset = (long)BitConverter.ToUInt64(d, b + 8);
                long fileSize = (long)BitConverter.ToUInt64(d, b + 0x20);
                headers.Add(new[] { type, offset, fileSize });
                outSize = Math.Max(outSize, offset + fileSize);
            }
            var output = new byte[outSize];
            Array.Copy(d, elf, output, 0, headerLength);
            long selfSize = (long)BitConverter.ToUInt64(d, 16);
            for (int i = 0; i < headers.Count; i++)
            {
                long type = headers[i][0], offset = headers[i][1], fileSize = headers[i][2];
                if (fileSize == 0) continue;
                long[] entry;
                if (entries.TryGetValue(i, out entry))
                    Array.Copy(d, entry[0], output, offset, Math.Min(entry[1], fileSize));
                else if (type == 0x6fffff01 && selfSize + fileSize <= d.Length)
                    Array.Copy(d, selfSize, output, offset, fileSize); // PT_SCE_VERSION follows the body
            }
            return output;
        }

        /// Rewrites PT_SCE_RELRO (0x61000010) as a read-write PT_LOAD so the converter maps it and
        /// allows relocations into it.
        public static byte[] RelroToLoad(byte[] elf)
        {
            long phoff = (long)BitConverter.ToUInt64(elf, 0x20);
            int phnum = BitConverter.ToUInt16(elf, 0x38);
            for (int i = 0; i < phnum; i++)
            {
                int header = (int)phoff + i * 0x38;
                if (BitConverter.ToUInt32(elf, header) != 0x61000010) continue;
                BitConverter.GetBytes(1u).CopyTo(elf, header);     // PT_LOAD
                BitConverter.GetBytes(6u).CopyTo(elf, header + 4); // read, write
            }
            return elf;
        }
    }

    /// Finishes the converted GTSport.exe: the launcher's icon (RyGTS.exe's embedded copy of ryGT.ico),
    /// so Explorer and shortcuts show the game's art instead of a blank program icon, and the Windows
    /// (GUI) subsystem, so a double-click opens only the game window, without a console.
    static class GameExe
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr BeginUpdateResource(string file, bool deleteExisting);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool UpdateResource(IntPtr update, IntPtr type, IntPtr name, ushort language, byte[] data, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool EndUpdateResource(IntPtr update, bool discard);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

        [DllImport("kernel32.dll")]
        static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

        [DllImport("kernel32.dll")]
        static extern bool FreeLibrary(IntPtr module);

        const int RtIcon = 3, RtGroupIcon = 14;
        const ushort GuiSubsystem = 2;

        public static bool IsPrepared(string exe)
        {
            return (Wine.Detected || HasIcon(exe)) && ReadSubsystem(exe) == GuiSubsystem;
        }

        public static void Prepare(string exe)
        {
            // (Not the icon under Wine: it is only for Windows' Explorer, and this keeps Set up from
            // depending on Wine rewriting the resources of a 100 MB program.)
            if (!Wine.Detected && !HasIcon(exe)) ApplyIcon(exe);
            if (ReadSubsystem(exe) != GuiSubsystem) WriteSubsystem(exe, GuiSubsystem);
        }

        public static void ApplyVersionInfo(string exe)
        {
            if (Wine.Detected) return;
            if (HasResource(exe, RtVersion) &&
                !String.Equals(FileVersionInfo.GetVersionInfo(exe).CompanyName, "RyGT", StringComparison.Ordinal)) return;
            byte[] data = VersionInfo(new[] {
                new[] { "CompanyName", "RyGTS" },
                new[] { "FileDescription", "Gran Turismo Sport" },
                new[] { "FileVersion", "1.69" },
                new[] { "InternalName", "GTSport" },
                new[] { "OriginalFilename", "GTSport.exe" },
                new[] { "ProductName", "Gran Turismo Sport" },
                new[] { "ProductVersion", "1.69" },
            });
            IntPtr update = BeginUpdateResource(exe, false);
            if (update == IntPtr.Zero) throw new Exception("Could not open " + exe + " (error " + Marshal.GetLastWin32Error() + ").");
            bool ok = UpdateResource(update, (IntPtr)RtVersion, (IntPtr)1, 0x0409, data, (uint)data.Length);
            if (!EndUpdateResource(update, !ok) || !ok)
                throw new Exception("Could not write the version information into " + exe + " (error " + Marshal.GetLastWin32Error() + ").");
        }

        const int RtVersion = 16;

        static bool HasResource(string exe, int type)
        {
            IntPtr module = LoadLibraryEx(exe, IntPtr.Zero, 0x2 | 0x20);
            if (module == IntPtr.Zero) return false;
            try { return FindResource(module, (IntPtr)1, (IntPtr)type) != IntPtr.Zero; }
            finally { FreeLibrary(module); }
        }

        static byte[] VersionInfo(string[][] strings)
        {
            var fixedInfo = new MemoryStream();
            var w = new BinaryWriter(fixedInfo);
            w.Write(0xFEEF04BDu); w.Write(0x00010000u);
            w.Write(0x00010045u); w.Write(0u);
            w.Write(0x00010045u); w.Write(0u);
            w.Write(0x3Fu); w.Write(0u); w.Write(0x40004u);
            w.Write(1u); w.Write(0u); w.Write(0u); w.Write(0u);
            var table = new List<byte[]>();
            foreach (var s in strings)
                table.Add(Block(s[0], Encoding.Unicode.GetBytes(s[1] + "\0"), (s[1].Length + 1), 1, null));
            byte[] stringTable = Block("040904b0", null, 0, 1, table);
            byte[] stringFileInfo = Block("StringFileInfo", null, 0, 1, new List<byte[]> { stringTable });
            byte[] translation = Block("Translation", new byte[] { 0x09, 0x04, 0xb0, 0x04 }, 4, 0, null);
            byte[] varFileInfo = Block("VarFileInfo", null, 0, 1, new List<byte[]> { translation });
            return Block("VS_VERSION_INFO", fixedInfo.ToArray(), 52, 0,
                         new List<byte[]> { stringFileInfo, varFileInfo });
        }

        static byte[] Block(string key, byte[] value, int valueLength, ushort type, List<byte[]> children)
        {
            var block = new MemoryStream();
            var w = new BinaryWriter(block);
            w.Write((ushort)0); w.Write((ushort)valueLength); w.Write(type);
            w.Write(Encoding.Unicode.GetBytes(key + "\0"));
            while (block.Length % 4 != 0) w.Write((byte)0);
            if (value != null) w.Write(value);
            if (children != null)
                foreach (var child in children)
                {
                    while (block.Length % 4 != 0) w.Write((byte)0);
                    w.Write(child);
                }
            byte[] bytes = block.ToArray();
            BitConverter.GetBytes((ushort)bytes.Length).CopyTo(bytes, 0);
            return bytes;
        }

        // The subsystem field of the PE optional header: PE header offset at 0x3c, then the "PE\0\0"
        // signature (4 bytes), the file header (20 bytes) and 68 bytes into the optional header.
        static long SubsystemOffset(BinaryReader reader)
        {
            reader.BaseStream.Position = 0x3c;
            return reader.ReadInt32() + 4 + 20 + 68;
        }

        static ushort ReadSubsystem(string exe)
        {
            using (var reader = new BinaryReader(File.OpenRead(exe)))
            {
                reader.BaseStream.Position = SubsystemOffset(reader);
                return reader.ReadUInt16();
            }
        }

        static void WriteSubsystem(string exe, ushort subsystem)
        {
            using (var stream = new FileStream(exe, FileMode.Open, FileAccess.ReadWrite))
            {
                long offset = SubsystemOffset(new BinaryReader(stream));
                stream.Position = offset;
                stream.Write(BitConverter.GetBytes(subsystem), 0, 2);
            }
        }

        static bool HasIcon(string exe)
        {
            IntPtr module = LoadLibraryEx(exe, IntPtr.Zero, 0x2 | 0x20); // as a data file and image resource
            if (module == IntPtr.Zero) return false;
            try { return FindResource(module, (IntPtr)1, (IntPtr)RtGroupIcon) != IntPtr.Zero; }
            finally { FreeLibrary(module); }
        }

        /// Adds every image of the .ico as RT_ICON 1..n and the icon group as RT_GROUP_ICON 1.
        static void ApplyIcon(string exe)
        {
            byte[] ico;
            using (var stream = typeof(GameExe).Assembly.GetManifestResourceStream("ryGT.ico"))
            {
                if (stream == null) throw new Exception("RyGTS.exe has no embedded icon.");
                ico = new byte[stream.Length];
                stream.Read(ico, 0, ico.Length);
            }
            int count = BitConverter.ToUInt16(ico, 4);
            IntPtr update = BeginUpdateResource(exe, false);
            if (update == IntPtr.Zero) throw new Exception("Could not open " + exe + " (error " + Marshal.GetLastWin32Error() + ").");
            bool ok = false;
            try
            {
                var group = new MemoryStream();
                var writer = new BinaryWriter(group);
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)count);
                for (int i = 0; i < count; i++)
                {
                    int entry = 6 + 16 * i;
                    int size = BitConverter.ToInt32(ico, entry + 8), offset = BitConverter.ToInt32(ico, entry + 12);
                    var image = new byte[size];
                    Array.Copy(ico, offset, image, 0, size);
                    if (!UpdateResource(update, (IntPtr)RtIcon, (IntPtr)(i + 1), 0, image, (uint)size))
                        throw new Exception("Could not add the icon (error " + Marshal.GetLastWin32Error() + ").");
                    writer.Write(ico, entry, 12);   // width, height, colors, reserved, planes, bit count, size
                    writer.Write((ushort)(i + 1)); // the RT_ICON id instead of the file offset
                }
                var data = group.ToArray();
                if (!UpdateResource(update, (IntPtr)RtGroupIcon, (IntPtr)1, 0, data, (uint)data.Length))
                    throw new Exception("Could not add the icon group (error " + Marshal.GetLastWin32Error() + ").");
                ok = true;
            }
            finally
            {
                if (!EndUpdateResource(update, !ok) && ok)
                    throw new Exception("Could not write the icon into " + exe + " (error " + Marshal.GetLastWin32Error() + ").");
            }
        }
    }

    static class AmdSoftware
    {
        // AMD Software can mark a manually added game hidden again; every start clears that.
        static void Unhide(string list, string exe)
        {
            // AMD Software rewrites the list when it closes; leave it alone while it is open.
            if (Process.GetProcessesByName("RadeonSoftware").Length != 0) return;
            string text = File.ReadAllText(list);
            int at = text.IndexOf(exe.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase);
            if (at < 0) return;
            int entry = text.LastIndexOf("\"FSRUpgradeAvailable\"", at, StringComparison.Ordinal);
            int hidden = entry < 0 ? -1 : text.IndexOf("\"hidden\"", entry, StringComparison.Ordinal);
            if (hidden < 0) return;
            int value = text.IndexOf('"', text.IndexOf(':', hidden)) + 1;
            if (String.CompareOrdinal(text, value, "TRUE", 0, 4) != 0) return;
            File.Copy(list, list + ".rygt-unhide-backup", true);
            File.WriteAllText(list, text.Substring(0, value) + "FALSE" + text.Substring(value + 4), new UTF8Encoding(false));
        }

        public static void EnsureGameEntry(string root, string exe)
        {
            try
            {
                if (Wine.Detected || Environment.GetEnvironmentVariable("RYGT_AMD_SOFTWARE") == "0") return;
                string list = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                           "AMD", "CN", "gmdb.blb");
                string script = Path.Combine(root, "tools", "amd", "Add-To-AMD-Software.ps1");
                string marker = Path.Combine(root, "user", "amd-software-entry.txt");
                if (!File.Exists(list) || !File.Exists(script) || !File.Exists(exe)) return;
                Unhide(list, exe);
                if (File.Exists(marker) && File.ReadAllText(marker).Trim() == exe) return;
                string escaped = exe.Replace("\\", "\\\\");
                if (File.ReadAllText(list).IndexOf(escaped, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    File.WriteAllText(marker, exe);
                    return;
                }
                var start = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -GameExe \"" + exe + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using (var process = Process.Start(start))
                {
                    string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(30000)) return;
                    if (process.ExitCode == 0) File.WriteAllText(marker, exe);
                    File.AppendAllText(Path.Combine(root, "logs", "setup.log"),
                                       "AMD Software game list: " + output.Trim() + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    // A local build record, not an authenticity signature. Written only after setup succeeds.
    static class GameBuildIntegrity
    {
        public static string Hash(string exe)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(exe))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        public static bool Matches(string exe)
        {
            string stamp = exe + ".sha256";
            if (!File.Exists(exe) || !File.Exists(stamp)) return false;
            string recorded = File.ReadAllText(stamp).Trim();
            return recorded.Length == 64 && recorded.All(Uri.IsHexDigit) &&
                   String.Equals(recorded, Hash(exe), StringComparison.OrdinalIgnoreCase);
        }

        public static void RequireStopped(string exe)
        {
            if (!File.Exists(exe)) return;
            try { using (File.Open(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
            catch (IOException e) { throw new IOException("Close the game before rebuilding its executable.", e); }
        }

        public static void Record(string exe)
        {
            File.WriteAllText(exe + ".sha256", Hash(exe) + Environment.NewLine);
        }

        public static bool Ensure(string exe, Action rebuild)
        {
            if (Matches(exe)) return false;
            RequireStopped(exe);
            rebuild();
            if (!Matches(exe)) throw new IOException("The rebuilt game did not match its recorded SHA-256; it will not be started.");
            return true;
        }
    }

    class Settings
    {
        public string Game = "", Update = "", Modules = "", Fonts = "";
        public string StdoutLevel = "Warning";
        public bool HddStreaming = false;
        public bool ShowAdvanced = true; // FEAT-GTS-438: the Advanced tab and its quick access entry
        public bool PromptObsolete = true; // FEAT-GTS-469: offer to remove obsolete config entries
        public List<EnvVar> Env = new List<EnvVar>(); // FEAT-GTS-433
        readonly string path;

        public Settings(string path)
        {
            this.path = path;
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                if (line.Substring(0, eq).Trim() == "env")
                {
                    var entry = CustomEnvironment.Parse(line.Substring(eq + 1)); // the value as written, not trimmed
                    if (entry != null) Env.Add(entry);
                    continue;
                }
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                if (key == "game") Game = value;
                else if (key == "update") Update = value;
                else if (key == "modules") Modules = value;
                else if (key == "fonts") Fonts = value;
                else if (key == "stdout_level") StdoutLevel = LogFiles.Levels[LogFiles.LevelIndex(value)];
                else if (key == "hdd_streaming") HddStreaming = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (key == "show_advanced") ShowAdvanced = !(value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase));
                else if (key == "prompt_obsolete") PromptObsolete = !(value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase));
            }
        }

        public void Save()
        {
            var lines = new List<string> { "game=" + Game, "update=" + Update, "modules=" + Modules, "fonts=" + Fonts, "stdout_level=" + StdoutLevel, "hdd_streaming=" + (HddStreaming ? "1" : "0"), "show_advanced=" + (ShowAdvanced ? "1" : "0"), "prompt_obsolete=" + (PromptObsolete ? "1" : "0") };
            foreach (var entry in Env) lines.Add("env=" + CustomEnvironment.Format(entry));
            File.WriteAllLines(path, lines);
        }
    }

    class LoaderForm : Form, IBackdrop
    {
        static readonly string[] FirmwareModules = { "libSceFont", "libSceFontFt", "libSceFreeTypeOt", "libSceJson2", "libSceNgs2", "libSceBluetoothHid" };
        // Used when present: the JPEG decoder (Scapes thumbnails and backgrounds) and PNG encoder (photos).
        // Both are plain software modules needing only libkernel and libSceLibcInternal.
        // Other firmware modules GT Sport imports stay RyGTS's stubs in libs: some, such as
        // libSceOttvCapture, need modules nothing provides and make GTSport.exe fail at startup.
        static readonly string[] OptionalFirmwareModules = { "libSceJpegDec", "libScePngEnc" };

        readonly string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        readonly Settings settings;
        readonly TextBox game, update, modules, fonts, log;
        readonly Button setup, play, addDlss;
        readonly Label status;
        readonly ToolTip shaderObjectTip = new ToolTip();
        volatile bool busy;
        Action<bool> showAdvanced; // FEAT-GTS-438
        // Home: the picture over the whole window with the title, Play and the settings button.
        // Settings: a sidebar of categories and their pages.
        readonly HomeView home;
        readonly Panel settingsView, pagesHost, sidebar;
        readonly Curtain curtain;
        readonly Dictionary<string, Panel> pages = new Dictionary<string, Panel>();
        readonly Dictionary<string, SidebarItem> pageItems = new Dictionary<string, SidebarItem>();
        readonly string[] pageNames = { "Game", "Display", "Graphics", "Shaders", "Controls", "Advanced", "About" };
        string lastPage = "Game";
        // Pages left out of the sidebar (Advanced when Settings > About turns it off). Control.Visible
        // cannot tell: it is false for every item while the settings view is hidden.
        readonly HashSet<string> hiddenPages = new HashSet<string>();

        public LoaderForm()
        {
            settings = new Settings(Path.Combine(root, "RyGT.ini"));
            Text = "RyGTS - Gran Turismo Sport";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(1280, 760);
            MinimumSize = new Size(1040, 640);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            BackColor = Dark.Back;
            // The launcher pictures (launcher\background*.png or .jpg, else GT Sport's own pic1.png)
            // fill the home view, one after another when there are several.
            pictures = LoadBackgrounds();
            ResizeRedraw = true;

            settingsView = new Panel { Dock = DockStyle.Fill, BackColor = Dark.Panel, Visible = false };

            // ---- Home: the pictures, the logo (launcher\logo.png) or the title, Play and the settings button
            play = new GlowButton { Text = "Play", Size = new Size(236, 46), Font = Dark.Caps(11f) };
            play.Click += delegate { Play(); };
            var gear = new GlowButton { Text = ((char)0xE713).ToString(), Glyph = true, Size = new Size(46, 46), Font = new Font("Segoe MDL2 Assets", 13f) };
            gear.Click += delegate { ShowSettings(lastPage); };
            var logoImage = LoadLogo();
            home = new HomeView(pictures, (GlowButton)play, gear)
            {
                Dock = DockStyle.Fill, Logo = logoImage, Title = "GRAN TURISMO", Accent = "SPORT",
                Caption = Dark.Spaced("CUSA03220   " + (char)183 + "   UPDATE 1.69   " + (char)183 + "   RYGTS FOR WINDOWS"),
                Version = "RyGTS " + LauncherVersion.Value + "  " + (char)183 + "  GT Sport 1.69 " + (char)183 + " CUSA03220"
            };
            curtain = new Curtain { Dock = DockStyle.Fill, Visible = false };
            Controls.Add(curtain);
            Controls.Add(settingsView);
            Controls.Add(home);
            // The status shows on the home view and on the Game page.
            status = new Label { AutoSize = true };
            status.TextChanged += delegate { home.Status = status.Text; home.Invalidate(false); };

            // ---- Settings: the sidebar
            sidebar = new Panel { Dock = DockStyle.Left, Width = 224, BackColor = Dark.Sidebar };
            pagesHost = new Panel { Dock = DockStyle.Fill, BackColor = Dark.Panel };
            settingsView.Controls.Add(pagesHost);
            settingsView.Controls.Add(sidebar);
            var back = new Label
            {
                Text = (char)0x2039 + "   " + Dark.Spaced("BACK"), AutoSize = true, Font = Dark.Caps(9f), ForeColor = Dark.DimText,
                Tag = "heading", Cursor = Cursors.Hand, Location = new Point(28, 30)
            };
            back.Click += delegate { ShowHome(); };
            back.MouseEnter += delegate { back.ForeColor = Dark.Text; };
            back.MouseLeave += delegate { back.ForeColor = Dark.DimText; };
            sidebar.Controls.Add(back);
            if (logoImage != null)
            {
                sidebar.Controls.Add(new PictureBox
                {
                    Image = logoImage, SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(26, 70),
                    Size = new Size(150, Math.Min(64, 150 * logoImage.Height / logoImage.Width))
                });
            }
            else
            {
                var logo = new PictureBox { Size = new Size(34, 34), SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(28, 78) };
                try { logo.Image = new Icon(Icon, 48, 48).ToBitmap(); } catch { }
                sidebar.Controls.Add(logo);
                sidebar.Controls.Add(new Label { Text = "GRAN TURISMO", AutoSize = true, Font = Dark.Caps(12f), Location = new Point(70, 76) });
                sidebar.Controls.Add(new Label { Text = "SPORT", AutoSize = true, Font = Dark.Caps(12f), ForeColor = Dark.AccentText, Tag = "heading", Location = new Point(70, 95) });
            }
            var sideVersion = new Label { Text = "RyGTS for Windows", AutoSize = true, ForeColor = Color.FromArgb(96, 102, 114), Tag = "heading" };
            sidebar.Controls.Add(sideVersion);
            sidebar.Layout += delegate { sideVersion.Location = new Point(28, sidebar.ClientSize.Height - sideVersion.Height - 24); };
            foreach (var name in pageNames)
            {
                string page = name;
                var item = new SidebarItem(page) { Width = 224 };
                item.Click += delegate { ShowSettings(page); };
                sidebar.Controls.Add(item);
                pageItems[page] = item;
            }
            PlaceSidebarItems();

            // ---- Game: the files, Set up and its log
            var gamePage = NewPage("Game", "Game",
                "Your own decrypted GT Sport dump (CUSA03220), its 1.69 update if it is separate, and your PS4 firmware's " +
                "system modules. PS4 fonts are optional. Set up converts the game once; Play starts it.");
            var files = Section(gamePage, "Files");
            game = AddRow(files, "Game (CUSA03220)", settings.Game);
            update = AddRow(files, "Update 1.69 (if separate)", settings.Update);
            modules = AddRow(files, "System modules", settings.Modules);
            fonts = AddRow(files, "Fonts (optional)", settings.Fonts);
            files.Controls.Add(Hint("Without PS4 dumped fonts, some in-game UI fonts may not draw or appear correctly."));
            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 16, 0, 0) };
            setup = new Button { Text = "Set up", AutoSize = true, Tag = "accent", Margin = new Padding(0, 0, 10, 0) };
            setup.Click += delegate { StartSetup(); };
            var open = new Button { Text = "Open folder", AutoSize = true, Margin = new Padding(0) };
            open.Click += delegate { OpenFolder(); };
            actions.Controls.Add(setup);
            actions.Controls.Add(open);
            gamePage.Controls.Add(actions);
            var gameStatus = new Label { AutoSize = true, MaximumSize = new Size(860, 0), ForeColor = Dark.DimText, Tag = "heading", Margin = new Padding(1, 12, 0, 0) };
            gamePage.Controls.Add(gameStatus);
            status.TextChanged += delegate { gameStatus.Text = status.Text; };
            var logSection = Section(gamePage, "Set up log");
            var logBox = new Card(Dark.Field) { Dock = DockStyle.Fill, Height = 190, Radius = 3, Edge = Color.Empty, Padding = new Padding(10, 8, 4, 8), Margin = new Padding(0, 6, 0, 0) };
            logBox.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            log = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 8.5f), Margin = new Padding(0)
            };
            logBox.Controls.Add(log);
            logSection.Controls.Add(logBox);

            // Display, Graphics and Shaders: saved as they change; the game reads them when it starts.
            var displayPage = NewPage("Display", "Display", "How the game's window looks and which GPU draws it. Changes apply the next time you play.");
            var graphicsPage = NewPage("Graphics", "Graphics", "Upscaling, frame generation, sharpening and effects. Changes apply the next time you play.");
            var shadersPage = NewPage("Shaders", "Shaders", "Shaders are built once per car and track; these settings decide when that happens.");
            var aboutPage = NewPage("About", "About", "RyGTS for Windows " + (char)183 + " Gran Turismo Sport (CUSA03220) " + (char)183 + " update 1.69");
            aboutPage.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(860, 0), ForeColor = Dark.DimText, Tag = "heading", Margin = new Padding(1, 0, 0, 6),
                Text = "RyGTS converts your own copy of Gran Turismo Sport to run on Windows. " +
                       "It is a fan project, not affiliated with Sony Interactive Entertainment or Polyphony Digital; " +
                       "Gran Turismo is their trademark. You need your own copy of the game."
            });
            var folders = Section(aboutPage, "Folders");
            folders.Controls.Add(OpenRow("The RyGTS folder", OpenFolder, "Open folder"));
            folders.Controls.Add(OpenRow("Logs (stdout.log, stderr.log, setup.log)", delegate { OpenSubfolder("logs"); }, "Open log folder"));
            folders.Controls.Add(OpenRow("Shaders", delegate { OpenSubfolder("shaders"); }, "Open folder"));

            var display = Section(displayPage, "Screen");
            AddVulkanDeviceSetting(display);
            AddScreenSettings(display);
            AddShapeSetting(display);
            AddOption(display, "Fast flips (improves frame pacing)", "GPU", "fast_flips", false);
            AddOption(display, "Show an FPS counter", "General", "show_fps_counter", false);
            AddOption(display, "Show the splash screen while the game starts", "General", "show_splash", false);
            AddOption(display, "Show the game on Discord (Rich Presence)", "General", "discord_rpc_enabled", true);
            // The game's opening movie (when its own option plays it); off: skipped at once.
            AddOption(display, "Play the opening movie", "General", "opening_movie", true);
            AddMemorySetting(display);

            // Graphics.
            var graphics = Section(graphicsPage, "Graphics");
            AddRendererSetting(graphics);
            AddExperimentalRenderScale(graphics);
            // Sparks, tire smoke and other effects are always on; an older launcher could have
            // turned them off in the config.
            if (!ReadSetting("GPU", "particle_effects", true))
            {
                try { WriteSetting("GPU", "particle_effects", true); } catch { }
            }
            // AMD FSR scales the game's 1920x1080 picture to the window or screen size: FSR 3.1 through
            // amd_fidelityfx_vk.dll (next to GTSport.exe, part of the release), or shadGT's FSR 1 pass.
            // AMD: FSR upscaling (FSR 3.1 through amd_fidelityfx_vk.dll, or shadGT's FSR 1) and
            // FSR 3.1 frame generation (any GPU; independent of the upscaling choice).
            var fsr = AddOption(graphics, "Upscale with AMD FSR or Intel XeSS", "GPU", "fsr_enabled", false);
            bool fsr3Installed = File.Exists(Path.Combine(root, "amd_fidelityfx_vk.dll"));
            var fsr3 = AddOption(graphics, fsr3Installed
                ? "Use the newest FSR (off: FSR 1)"
                : "Use the newest FSR (needs amd_fidelityfx_vk.dll)", "GPU", "fsr3_enabled", true);
            var rcas = AddOption(graphics, "FSR sharpening (RCAS)", "GPU", "rcas_enabled", true);
            var framegen = AddOption(graphics, fsr3Installed
                ? "FSR frame generation: about 2x the frames (any GPU)"
                : "FSR frame generation (needs amd_fidelityfx_vk.dll)", "GPU", "framegen_enabled", false);
            // FSR-200: FSR reconstructs the race scene from the game's depth, motion vectors and
            // camera jitter (the default renderer's temporal scene capture, like temporal DLSS)
            // instead of scaling the finished picture.
            var fsrTemporal = AddOption(graphics, "Temporal upscaling: replaces the game's anti-aliasing",
                "GPU", "fsr_temporal", true);
            // FSR-201: the upscaler: the FSR version inside amd_fidelityfx_vk.dll (FSR 4 is DirectX 12
            // SDK 2.4.0 Vulkan DLLs; without them the renderer uses FSR 3.1), or
            // XESS-200: Intel XeSS through libxess.dll next to GTSport.exe (temporal only; from Intel's
            // XeSS SDK), on any GPU with the Vulkan features it needs.
            bool xessInstalled = File.Exists(Path.Combine(root, "libxess.dll"));
            var versionRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(22, 2, 0, 2), WrapContents = false };
            var versionLabel = new Label { Text = "Upscaler", AutoSize = false, Width = 174, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) };
            var version = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, FlatStyle = FlatStyle.Flat };
            var versionValues = new[] { "fsr3", "fsr4", "xess" };
            version.Items.AddRange(new object[] { "AMD FSR 3", "AMD FSR 4",
                xessInstalled ? "Intel XeSS" : "Intel XeSS (needs libxess.dll)" });
            version.SelectedIndex = Math.Max(0, Array.IndexOf(versionValues, ReadStringSetting("GPU", "fsr_upscaler", "fsr3")));
            version.SelectedIndexChanged += delegate
            {
                try { WriteSetting("GPU", "fsr_upscaler", versionValues[version.SelectedIndex]); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            versionRow.Controls.Add(versionLabel);
            versionRow.Controls.Add(version);
            graphics.Controls.Add(versionRow);
            // XESS-201: fetches Intel's official SDK on request; only the upscaler and its licence
            // are kept. Intel's licence allows redistributing it unmodified.
            var xessDownload = new Button { Text = xessInstalled ? "Reinstall XeSS" : "Download XeSS (77 MB)", AutoSize = true, Margin = new Padding(22, 2, 0, 4) };
            xessDownload.Click += delegate
            {
                if (MessageBox.Show(this, "Download Intel XeSS SDK 3.0.2 from github.com/intel/xess and install libxess.dll next to GTSport.exe? It uses Intel's Simplified Software License.",
                                    "Intel XeSS", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                xessDownload.Enabled = false;
                xessDownload.Text = "Downloading...";
                new System.Threading.Thread(delegate ()
                {
                    string error = null;
                    string zip = Path.Combine(Path.GetTempPath(), "RyGT-XeSS-3.0.2.zip");
                    try
                    {
                        System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
                        using (var web = new System.Net.WebClient())
                            web.DownloadFile("https://github.com/intel/xess/releases/download/v3.0.2/XeSS_SDK_3.0.2.zip", zip);
                        using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
                        {
                            System.IO.Compression.ZipFileExtensions.ExtractToFile(archive.GetEntry("bin/libxess.dll"), Path.Combine(root, "libxess.dll"), true);
                            System.IO.Compression.ZipFileExtensions.ExtractToFile(archive.GetEntry("LICENSE.txt"), Path.Combine(root, "libxess-LICENSE.txt"), true);
                            System.IO.Compression.ZipFileExtensions.ExtractToFile(archive.GetEntry("third-party-programs.txt"), Path.Combine(root, "libxess-third-party-programs.txt"), true);
                        }
                    }
                    catch (Exception e) { error = e.Message; }
                    try { File.Delete(zip); } catch { }
                    BeginInvoke((Action)delegate
                    {
                        xessDownload.Enabled = true;
                        if (error != null)
                        {
                            xessDownload.Text = "Download XeSS (77 MB)";
                            MessageBox.Show(this, "Could not install XeSS: " + error);
                            return;
                        }
                        xessDownload.Text = "Reinstall XeSS";
                        version.Items[2] = "Intel XeSS";
                    });
                }) { IsBackground = true }.Start();
            };
            graphics.Controls.Add(xessDownload);
            // FSR-212: AMD FSR 4 for Vulkan comes with AMD's FSR SDK 2.4 (FSR Upscaling 4.1.2, with
            // the INT8 model for RX 6000 and RX 7000). Fetches AMD's signed loader and upscaler DLLs
            // on request and installs them next to GTSport.exe only when AMD's signature is on them.
            bool fsr4Installed = File.Exists(Path.Combine(root, "amd_fidelityfx_loader_vk.dll")) &&
                                 File.Exists(Path.Combine(root, "amd_fidelityfx_upscaler_vk.dll"));
            const string fsr4Label = "Download AMD FSR 4 (35 MB)";
            var fsr4Download = new Button { Text = fsr4Installed ? "Reinstall AMD FSR 4" : fsr4Label, AutoSize = true, Margin = new Padding(22, 2, 0, 4) };
            fsr4Download.Click += delegate
            {
                if (MessageBox.Show(this, "Download AMD FSR SDK 2.4.0 (FSR 4.1.2) from github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK and install amd_fidelityfx_loader_vk.dll and amd_fidelityfx_upscaler_vk.dll next to GTSport.exe? They use AMD's license terms. FSR 4 runs on Radeon RX 6000, RX 7000 and RX 9000 cards.",
                                    "AMD FSR 4", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                fsr4Download.Enabled = false;
                fsr4Download.Text = "Downloading...";
                new System.Threading.Thread(delegate ()
                {
                    string error = null;
                    const string sdk = "https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/v2.4.0/";
                    string temp = Path.Combine(Path.GetTempPath(), "RyGT-FSR4-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        Directory.CreateDirectory(temp);
                        System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
                        var names = new[] { "amd_fidelityfx_loader_vk.dll", "amd_fidelityfx_upscaler_vk.dll" };
                        using (var web = new System.Net.WebClient())
                        {
                            foreach (var name in names)
                                web.DownloadFile(sdk + "Kits/FidelityFX/signedbin/" + name, Path.Combine(temp, name));
                            web.DownloadFile(sdk + "Kits/FidelityFX/docs/license.md", Path.Combine(temp, "amd_fidelityfx_sdk-LICENSE.md"));
                        }
                        foreach (var name in names)
                        {
                            string subject;
                            try { subject = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(Path.Combine(temp, name)).Subject; }
                            catch { subject = ""; }
                            if (subject.IndexOf("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) < 0)
                                throw new Exception(name + " is not signed by AMD; nothing was installed.");
                        }
                        foreach (var name in names.Concat(new[] { "amd_fidelityfx_sdk-LICENSE.md" }))
                            File.Copy(Path.Combine(temp, name), Path.Combine(root, name), true);
                    }
                    catch (Exception e) { error = e.Message; }
                    try { Directory.Delete(temp, true); } catch { }
                    BeginInvoke((Action)delegate
                    {
                        fsr4Download.Enabled = true;
                        if (error != null)
                        {
                            fsr4Download.Text = fsr4Label;
                            MessageBox.Show(this, "Could not install AMD FSR 4: " + error);
                            return;
                        }
                        fsr4Download.Text = "Reinstall AMD FSR 4";
                    });
                }) { IsBackground = true }.Start();
            };
            graphics.Controls.Add(fsr4Download);
            // FSR-209: the upscaler's preset (GPU.upscale_ratio). GT Sport always renders the race at
            // 1920 x 1080, so a preset sets the output size as a multiple of it, which the picture
            // is then scaled to the window from (supersampling when the window is smaller). Each
            // upscaler lists its own presets; "Match the window" (0) keeps the window's size.
            var presetRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(22, 2, 0, 2), WrapContents = false };
            var presetLabel = new Label { Text = "Output resolution", AutoSize = false, Width = 174, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) };
            var preset = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, FlatStyle = FlatStyle.Flat };
            // The output size each preset gives from the race's 1920 x 1080, then the upscaler's own
            // name for that scale.
            var fsrPresets = new[] {
                new[] { "Same as the window / screen", "0" },
                new[] { "1920 x 1080 - anti-aliasing only (Native AA)", "1" },
                new[] { "2880 x 1620 - sharper (Quality, 1.5x)", "1.5" },
                new[] { "3264 x 1836 - sharper (Balanced, 1.7x)", "1.7" },
                new[] { "3840 x 2160 / 4K - sharpest (Performance, 2x)", "2" },
                new[] { "5760 x 3240 - very heavy (Ultra Performance, 3x)", "3" } };
            var xessPresets = new[] {
                new[] { "Same as the window / screen", "0" },
                new[] { "1920 x 1080 - anti-aliasing only (Native AA)", "1" },
                new[] { "2496 x 1404 - sharper (Ultra Quality Plus, 1.3x)", "1.3" },
                new[] { "2880 x 1620 - sharper (Ultra Quality, 1.5x)", "1.5" },
                new[] { "3264 x 1836 - sharper (Quality, 1.7x)", "1.7" },
                new[] { "3840 x 2160 / 4K - sharpest (Balanced, 2x)", "2" },
                new[] { "4416 x 2484 - very heavy (Performance, 2.3x)", "2.3" },
                new[] { "5760 x 3240 - very heavy (Ultra Performance, 3x)", "3" } };
            string[][] shownPresets = fsrPresets;
            bool loadingPresets = false;
            Action fillPresets = delegate
            {
                loadingPresets = true;
                shownPresets = versionValues[Math.Max(0, version.SelectedIndex)] == "xess" ? xessPresets : fsrPresets;
                string saved = ReadStringSetting("GPU", "upscale_ratio", "0");
                preset.Items.Clear();
                int selected = 0;
                for (int i = 0; i < shownPresets.Length; i++)
                {
                    preset.Items.Add(shownPresets[i][0]);
                    if (shownPresets[i][1] == saved) selected = i;
                }
                preset.SelectedIndex = selected;
                loadingPresets = false;
            };
            fillPresets();
            preset.SelectedIndexChanged += delegate
            {
                if (loadingPresets || preset.SelectedIndex < 0) return;
                try { WriteSetting("GPU", "upscale_ratio", shownPresets[preset.SelectedIndex][1]); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            version.SelectedIndexChanged += delegate { fillPresets(); };
            presetRow.Controls.Add(presetLabel);
            presetRow.Controls.Add(preset);
            graphics.Controls.Add(presetRow);
            var presetNote = new Label
            {
                Text = "GT Sport always renders races at 1920 x 1080. The upscaler rebuilds that picture at the " +
                       "output resolution, then it is fitted to your screen. On a 1080p screen, anything above " +
                       "1920 x 1080 is supersampling: smoother edges and finer detail, but more GPU work.",
                AutoSize = true, MaximumSize = new Size(420, 0), BackColor = Color.Transparent, ForeColor = Dark.DimText,
                Tag = "heading", Margin = new Padding(22, 2, 0, 6)
            };
            graphics.Controls.Add(presetNote);
            // Under "Use FSR 3.1.4", before sharpening and frame generation.
            graphics.Controls.SetChildIndex(fsrTemporal, graphics.Controls.GetChildIndex(fsr3) + 1);
            graphics.Controls.SetChildIndex(versionRow, graphics.Controls.GetChildIndex(fsrTemporal) + 1);
            graphics.Controls.SetChildIndex(presetRow, graphics.Controls.GetChildIndex(versionRow) + 1);
            graphics.Controls.SetChildIndex(presetNote, graphics.Controls.GetChildIndex(presetRow) + 1);
            foreach (var sub in new[] { fsr3, fsrTemporal, rcas, framegen }) sub.Margin = new Padding(22, 2, 0, 2);
            Action refreshFsr = delegate
            {
                fsr3.Enabled = fsr.Checked && fsr3Installed;
                fsrTemporal.Enabled = version.Enabled = preset.Enabled = fsr.Checked && fsr3Installed && fsr3.Checked;
                rcas.Enabled = fsr.Checked;
            };
            refreshFsr();
            framegen.Enabled = fsr3Installed;
            fsr.CheckedChanged += delegate { refreshFsr(); };
            fsr3.CheckedChanged += delegate { refreshFsr(); };
            // NVIDIA (the dlss folder): DLSS upscaling through ryGT_dlss.dll and nvngx_dlss.dll, and
            // DLSS frame generation through Streamline (sl.*.dll and nvngx_dlssg.dll; RTX 40 and
            // newer, Windows' hardware-accelerated GPU scheduling on). One frame generation at a time.
            // The release does not include NVIDIA's files: the player adds their own copies with the
            // button below (NVIDIA's DLSS SDK and Streamline SDK, or another game's copies).
            string dlssDir = Path.Combine(root, "dlss");
            var dlss = AddOption(graphics, "", "GPU", "dlss_enabled", false);
            // DLSS's input is the game's own picture, at most 1920 x 1080 (#61).
            graphics.Controls.Add(new Label
            {
                Text = "Internal resolution: up to 1920 x 1080.\nAt 4K output, DLSS corresponds to Performance mode.",
                AutoSize = true, MaximumSize = new Size(330, 0), BackColor = Color.Transparent, ForeColor = Dark.DimText,
                Tag = "heading", Margin = new Padding(22, 2, 0, 6)
            });
            var dlssg = AddOption(graphics, "", "GPU", "dlssg_enabled", false);
            dlssg.Margin = new Padding(22, 2, 0, 2);
            Action refreshDlss = delegate
            {
                bool dlssInstalled = File.Exists(Path.Combine(dlssDir, "ryGT_dlss.dll")) &&
                                     File.Exists(Path.Combine(dlssDir, "nvngx_dlss.dll"));
                bool dlssgInstalled = File.Exists(Path.Combine(dlssDir, "sl.interposer.dll")) &&
                                      File.Exists(Path.Combine(dlssDir, "nvngx_dlssg.dll"));
                dlss.Text = dlssInstalled ? "Upscale with NVIDIA DLSS (RTX cards; instead of FSR)"
                                          : "Upscale with NVIDIA DLSS (add the DLSS files first)";
                dlss.Enabled = dlssInstalled;
                dlssg.Text = dlssgInstalled ? "DLSS frame generation: about 2x the frames (RTX 40+)"
                                            : "DLSS frame generation (add the DLSS files first)";
                dlssg.Enabled = dlssgInstalled;
            };
            refreshDlss();
            addDlss = new Button { Text = "Add DLSS files...", AutoSize = true, Margin = new Padding(22, 2, 0, 4) };
            addDlss.Click += delegate
            {
                // The game keeps the DLSS files open while it runs (#61).
                var games = Process.GetProcessesByName("GTSport");
                bool gameRunning = games.Length != 0;
                foreach (var process in games) process.Dispose();
                if (gameRunning)
                {
                    MessageBox.Show(this, "Close GT Sport before adding or replacing the DLSS files.");
                    return;
                }
                using (var dialog = new FolderBrowserDialog
                {
                    Description = "Pick the folder with your NVIDIA DLSS files: nvngx_dlss.dll for upscaling; " +
                                  "for frame generation also nvngx_dlssg.dll and Streamline's sl.*.dll and " +
                                  "NvLowLatencyVk.dll (from NVIDIA's DLSS and Streamline SDKs)."
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    var names = new[] { "nvngx_dlss.dll", "nvngx_dlssg.dll", "sl.interposer.dll", "sl.common.dll",
                                        "sl.dlss_g.dll", "sl.reflex.dll", "sl.pcl.dll", "NvLowLatencyVk.dll" };
                    int copied = 0;
                    try
                    {
                        Directory.CreateDirectory(dlssDir);
                        foreach (var name in names)
                        {
                            // Found anywhere under the picked folder (the SDKs keep them in bin\x64).
                            var found = Directory.GetFiles(dialog.SelectedPath, name, SearchOption.AllDirectories);
                            if (found.Length == 0) continue;
                            // The upscaler is checked to be NVIDIA's 64-bit DLSS library, and the one
                            // it replaces is kept as a .bak (#61).
                            if (name == "nvngx_dlss.dll") InstallDlssLibrary(found[0], dlssDir);
                            else File.Copy(found[0], Path.Combine(dlssDir, name), true);
                            ++copied;
                        }
                    }
                    catch (Exception e)
                    {
                        MessageBox.Show(this, "Could not copy the DLSS files: " + e.Message);
                    }
                    refreshDlss();
                    MessageBox.Show(this, copied == 0
                        ? "No DLSS files were found in that folder."
                        : "Copied " + copied + " DLSS file(s) into the dlss folder.");
                }
            };
            graphics.Controls.Add(addDlss);
            var dlssSharpen = AddDlssSharpening(graphics, dlss);
            // DLSS and FSR both upscale, so one at a time; DLSS runs in Performance mode (#61).
            fsr.CheckedChanged += delegate { if (fsr.Checked && dlss.Checked) dlss.Checked = false; };
            Action updateDlss = delegate
            {
                if (!dlss.Checked) return;
                if (fsr.Checked) fsr.Checked = false;
                try
                {
                    if (ReadStringSetting("GPU", "dlss_quality", "performance") != "performance")
                        WriteSetting("GPU", "dlss_quality", "performance");
                }
                catch (Exception e) { MessageBox.Show(this, "Could not save the DLSS setting: " + e.Message); }
            };
            dlss.CheckedChanged += delegate { updateDlss(); };
            updateDlss();
            // DLSS-210: fetches NVIDIA's official DLSS and Streamline files into the dlss folder on
            // request; nothing NVIDIA ships with the build.
            var nvidiaDownload = new Button { Text = "Download NVIDIA DLSS and Streamline (about 340 MB)", AutoSize = true, Margin = new Padding(22, 2, 0, 4) };
            nvidiaDownload.Click += delegate
            {
                var games = Process.GetProcessesByName("GTSport");
                bool gameRunning = games.Length != 0;
                foreach (var process in games) process.Dispose();
                if (gameRunning)
                {
                    MessageBox.Show(this, "Close GT Sport before adding or replacing the DLSS files.");
                    return;
                }
                if (MessageBox.Show(this, "Download DLSS 310.9.16 and Streamline 2.14.16 from NVIDIA's GitHub pages into the dlss folder? They use NVIDIA's license terms. DLSS needs an NVIDIA RTX card.",
                                    "NVIDIA DLSS", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                nvidiaDownload.Enabled = false;
                nvidiaDownload.Text = "Downloading...";
                new System.Threading.Thread(delegate ()
                {
                    string error = null;
                    string temp = Path.Combine(Path.GetTempPath(), "RyGT-NVIDIA");
                    try
                    {
                        Directory.CreateDirectory(temp);
                        Directory.CreateDirectory(dlssDir);
                        System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
                        const string dlssUrl = "https://github.com/NVIDIA/DLSS/raw/v310.9.16/";
                        string zip = Path.Combine(temp, "streamline.zip");
                        using (var web = new System.Net.WebClient())
                        {
                            foreach (var name in new[] { "nvngx_dlss.dll", "nvngx_dlssg.dll" })
                                web.DownloadFile(dlssUrl + "lib/Windows_x86_64/rel/" + name, Path.Combine(dlssDir, name));
                            web.DownloadFile(dlssUrl + "LICENSE.txt", Path.Combine(dlssDir, "NVIDIA-DLSS-LICENSE.txt"));
                            web.DownloadFile("https://github.com/NVIDIA-RTX/Streamline/releases/download/v2.14.16/streamline-sdk-v2.14.16.zip", zip);
                        }
                        var wanted = new[] { "sl.interposer.dll", "sl.common.dll", "sl.dlss_g.dll", "sl.reflex.dll", "sl.pcl.dll", "NvLowLatencyVk.dll" };
                        var missing = new List<string>(wanted);
                        using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
                            foreach (var entry in archive.Entries)
                            {
                                if (!entry.FullName.Replace('/', Path.DirectorySeparatorChar).EndsWith(Path.Combine("bin", "x64", entry.Name), StringComparison.OrdinalIgnoreCase)) continue;
                                if (!wanted.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) continue;
                                System.IO.Compression.ZipFileExtensions.ExtractToFile(entry, Path.Combine(dlssDir, entry.Name), true);
                                missing.RemoveAll(name => String.Equals(name, entry.Name, StringComparison.OrdinalIgnoreCase));
                            }
                        if (missing.Count != 0) error = "Not in the Streamline package: " + String.Join(", ", missing);
                    }
                    catch (Exception e) { error = e.Message; }
                    try { Directory.Delete(temp, true); } catch { }
                    BeginInvoke((Action)delegate
                    {
                        nvidiaDownload.Enabled = true;
                        nvidiaDownload.Text = "Download NVIDIA DLSS and Streamline (about 340 MB)";
                        refreshDlss();
                        if (error != null) MessageBox.Show(this, "Could not install the NVIDIA files: " + error);
                    });
                }) { IsBackground = true }.Start();
            };
            graphics.Controls.Add(nvidiaDownload);
            graphics.Controls.SetChildIndex(nvidiaDownload, graphics.Controls.GetChildIndex(addDlss) + 1);
            framegen.CheckedChanged += delegate { if (framegen.Checked && dlssg.Checked) dlssg.Checked = false; };
            dlssg.CheckedChanged += delegate { if (dlssg.Checked && framegen.Checked) framegen.Checked = false; };
            // GTS-007: in the rain the game renders the scene a second time for the wet track's
            // reflection (about a quarter of a rainy frame's draws). Off: about +30% fps in rain.
            var wet = AddOption(graphics, "Wet-road reflections in the rain (off: much faster in rain)", "GPU",
                                "wet_reflections", true);
            // GTS-008: the rear-view mirror is redrawn every second frame (its picture is kept).
            var mirror = AddOption(graphics, "Rear-view mirror at half rate (a little faster)", "GPU",
                                   "mirror_half_rate", false);
            // The half-rate mirror is off while temporal DLSS is on, to keep its motion data (#61).
            Action updateMirror = delegate
            {
                mirror.Enabled = !dlss.Checked;
                mirror.Text = dlss.Checked ? "Half-rate rear-view mirror (disabled by DLSS)"
                                          : "Rear-view mirror at half rate (a little faster)";
            };
            dlss.CheckedChanged += delegate { updateMirror(); };
            updateMirror();
            // The Graphics tab: one card per subject. The older controls stay hidden and hold the
            // settings' logic; the unified choices below drive them.
            Control rendererRow = graphics.Controls[1];
            var sceneBox = graphics.Controls.OfType<CheckBox>().First(c => c.Text.StartsWith("Scene"));
            var sceneCombo = graphics.Controls.OfType<DarkComboBox>().First();
            graphics.Visible = false;
            bool syncing = false;
            Func<TableLayoutPanel, string, Control, FlowLayoutPanel> row = delegate (TableLayoutPanel card, string name, Control control)
            {
                var line = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, WrapContents = false, Margin = new Padding(0, 3, 0, 3) };
                line.Controls.Add(new Label { Text = name, AutoSize = false, Width = 190, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
                line.Controls.Add(control);
                card.Controls.Add(line);
                return line;
            };
            Func<DarkComboBox> combo = () => new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380, FlatStyle = FlatStyle.Flat };

            display.Controls.Add(rendererRow);
            display.Controls.SetChildIndex(rendererRow, display.Controls.GetChildIndex(display.Controls.OfType<FlowLayoutPanel>().First()) + 1); // under the Vulkan device

            var upscalerCard = Section(graphicsPage, "Upscaler");
            var upscaler = combo();
            upscaler.Items.AddRange(new object[] { "Off", "AMD FSR 4", "AMD FSR 3", "AMD FSR 1", "NVIDIA DLSS", "Intel XeSS" });
            upscaler.SelectedIndex = dlss.Checked ? 4 : !fsr.Checked ? 0 : !fsr3.Checked ? 3 : version.SelectedIndex == 1 ? 1 : version.SelectedIndex == 2 ? 5 : 2;
            upscaler.SelectedIndexChanged += delegate
            {
                if (syncing) return;
                int pick = upscaler.SelectedIndex;
                if ((pick == 4 && !dlss.Enabled) || (pick == 5 && !File.Exists(Path.Combine(root, "libxess.dll"))))
                {
                    MessageBox.Show(this, "Download its files first, under Libraries.");
                    syncing = true;
                    upscaler.SelectedIndex = 0;
                    syncing = false;
                    pick = 0;
                }
                dlss.Checked = pick == 4;
                fsr.Checked = pick != 0 && pick != 4;
                if (pick != 0 && pick != 4)
                {
                    fsr3.Checked = pick != 3;
                    if (pick != 3) version.SelectedIndex = pick == 1 ? 1 : pick == 5 ? 2 : 0;
                }
            };
            row(upscalerCard, "Upscaler", upscaler);
            // Below 100% lowers the scene (render scale); above it the upscaler's output grows.
            var qualities = new[] {
                new[] { "Ultra Performance - 33% (640 x 360)", "0", "" },
                new[] { "Performance - 50% (960 x 540)", "1", "" },
                new[] { "Balanced - 59% (1136 x 639)", "2", "" },
                new[] { "Quality - 67% (1280 x 720)", "3", "" },
                new[] { "Ultra Quality - 77% (1472 x 828)", "4", "" },
                new[] { "Native - 100% (1920 x 1080)", "", "1" },
                new[] { "Supersampling - 150% (2880 x 1620)", "", "1.5" },
                new[] { "Supersampling - 170% (3264 x 1836)", "", "1.7" },
                new[] { "Supersampling - 200% (3840 x 2160)", "", "2" },
                new[] { "Supersampling - 300% (5760 x 3240)", "", "3" } };
            var quality = combo();
            foreach (var item in qualities) quality.Items.Add(item[0]);
            string savedRatio = ReadStringSetting("GPU", "upscale_ratio", "0");
            quality.SelectedIndex = sceneBox.Checked ? Math.Min(4, Math.Max(0, sceneCombo.SelectedIndex))
                : Math.Max(5, Array.FindIndex(qualities, item => item[2] == savedRatio));
            quality.SelectedIndexChanged += delegate
            {
                var item = qualities[quality.SelectedIndex];
                sceneBox.Checked = item[1] != "";
                if (item[1] != "") sceneCombo.SelectedIndex = int.Parse(item[1]);
                try { WriteSetting("GPU", "upscale_ratio", item[2] != "" ? item[2] : "0"); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            row(upscalerCard, "Quality / Supersampling", quality);
            // PERF-210: AMD Anti-Lag (VK_AMD_anti_lag; AMD GPUs whose driver has it). Frame
            // generation paces its own frames, so Anti-Lag is off while it is on.
            var antiLag = AddOption(upscalerCard, "", "GPU", "anti_lag", false);
            upscalerCard.Controls.Add(Hint("Sizes are for 1080p output. Scene scaling needs a game restart."));

            var frameCard = Section(graphicsPage, "Frame generation");
            var frames = combo();
            frames.Items.AddRange(new object[] { "Off", "AMD FSR - about 2x frames", "NVIDIA DLSS - about 2x frames (RTX 40+)" });
            frames.SelectedIndex = dlssg.Checked ? 2 : framegen.Checked ? 1 : 0;
            frames.SelectedIndexChanged += delegate
            {
                if (frames.SelectedIndex == 2 && !dlssg.Enabled)
                {
                    MessageBox.Show(this, "Download the NVIDIA files first, under Libraries.");
                    frames.SelectedIndex = 0;
                    return;
                }
                framegen.Checked = frames.SelectedIndex == 1;
                dlssg.Checked = frames.SelectedIndex == 2;
            };
            row(frameCard, "Frame generation", frames);
            Action updateAntiLag = delegate
            {
                bool generated = frames.SelectedIndex != 0;
                antiLag.Enabled = !generated;
                antiLag.Text = generated ? "AMD Anti-Lag (off while frame generation is on)"
                                         : "AMD Anti-Lag: lower input lag (AMD GPUs)";
            };
            frames.SelectedIndexChanged += delegate { updateAntiLag(); };
            updateAntiLag();

            var sharpenCard = Section(graphicsPage, "Sharpening");
            // FSR's attenuation is 0 (sharpest) to 2 in thousandths; 0% turns the pass off.
            int attenuation = ReadIntSetting("GPU", "rcas_attenuation", 250);
            var fsrSharpen = new Slider { Minimum = 0, Maximum = 100, Width = 330, Suffix = "%", AccessibleName = "FSR sharpening" };
            fsrSharpen.Value = rcas.Checked ? Math.Max(0, Math.Min(100, 100 - attenuation / 20)) : 0;
            fsrSharpen.ValueChanged += delegate
            {
                rcas.Checked = fsrSharpen.Value != 0;
                try { WriteSetting("GPU", "rcas_attenuation", (100 - fsrSharpen.Value) * 20); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            sharpenCard.Controls.Add(new SettingRow("FSR sharpening (0% off)", fsrSharpen));
            sharpenCard.Controls.Add(dlssSharpen);

            var librariesCard = Section(graphicsPage, "Libraries");
            librariesCard.Controls.Add(new SettingRow("Intel XeSS (any GPU)", xessDownload));
            librariesCard.Controls.Add(new SettingRow("AMD FSR 4 (Radeon RX 6000 and newer)", fsr4Download));
            librariesCard.Controls.Add(new SettingRow("NVIDIA DLSS and Streamline (RTX cards)", nvidiaDownload));
            librariesCard.Controls.Add(new SettingRow("Your own NVIDIA DLSS files", addDlss));
            librariesCard.Controls.Add(Hint("RyGTS does not include these libraries; the buttons fetch each vendor's own files from its GitHub page."));

            var performanceCard = Section(graphicsPage, "Performance");
            foreach (var box in new[] { wet, mirror })
            {
                box.Margin = new Padding(0);
                performanceCard.Controls.Add(box);
            }

            // The direct renderer (shaders read game memory in place; Tokyo rain 42.7 fps vs 32.1)
            // is the only renderer since 2026-10-16; the old renderer's stored shaders stand in
            // while its own are built (D10), so it has no option any more.

            var extras = Section(graphicsPage, "Enhancements");
            AddOption(extras, "Disable motion blur", "GPU", "disable_motion_blur", false);
            AddOption(extras, "PS4 Pro Mode (BROKEN)", "GPU", "ps4_pro", false);
            uiHiresBox = AddOption(extras, "Draw the UI at screen resolution", "GPU", "ui_hires", false);
            AddOption(extras, "HDR (experimental; needs Windows HDR on)", "GPU", "hdr_allowed", false);

            // Shaders: the stored ones are built while the splash screen shows; a new one pauses
            // the game until it is compiled (instead of a stutter and missing objects).
            var storage = Section(shadersPage, "Storage");
            var hddStreaming = new ToggleRow { Text = "HDD streaming read-ahead (experimental)", Checked = settings.HddStreaming };
            hddStreaming.CheckedChanged += delegate {
                settings.HddStreaming = hddStreaming.Checked;
                try { settings.Save(); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            storage.Controls.Add(hddStreaming);
            storage.Controls.Add(Hint("For game data on a hard drive. May help sequential loads; restart required."));

            // Shaders: the stored ones are built while the splash screen shows; a new one pauses
            // the game until it is compiled (instead of a stutter and missing objects).
            var shaders = Section(shadersPage, "Compiling");
            AddOption(shaders, "Precompile shaders on the splash screen", "GPU", "precompile_shaders", true);
            var shaderObjects = AddOption(shaders, "Shader objects (experimental)", "GPU", "shader_objects", false);
            shaderObjectTip.SetToolTip(shaderObjects, "Experimental: may cause rendering issues. Unavailable on AMD GPUs.");
            // FEAT-GTS-445 (from RyGT7's PERF-GT7-111/117 and PERF-GT7-108; RyGT-Team/rygt7#3): shader
            // pre-translation (RYGT_MANIFEST_RECORD / RYGT_MANIFEST_PRETRANSLATE) and the compile pack
            // (RYGT_COMPILE_PACK), both off unless checked.
            AddOption(shaders, "Rebuild translated shaders in the background after a renderer update",
                      "GPU", "shader_pretranslate", false);
            AddOption(shaders, "Compile the shaders already seen on this PC at start, after a driver update (needs shader objects; may pause races)",
                      "GPU", "compile_pack", false);
            // What the game does while a new shader compiles during play (GPU.compile_mode).
            var mode = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, FlatStyle = FlatStyle.Flat };
            mode.Items.AddRange(new object[] { "Pause the game", "Slow motion", "Keep going (objects pop in)" });
            mode.SelectedIndex = Math.Max(0, Math.Min(2, ReadIntSetting("GPU", "compile_mode", 1)));
            shaders.Controls.Add(new SettingRow("When new shaders compile", mode));
            // How long one pause may go on compiling the shaders of what comes next: longer pauses,
            // fewer of them (GPU.compile_batch_ms).
            var batch = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, FlatStyle = FlatStyle.Flat };
            var batchValues = new[] { 0, 100, 300, 1000 };
            batch.Items.AddRange(new object[] { "Off (only what is needed)", "Short (up to 0.1 s)", "Medium (up to 0.3 s)", "Long (up to 1 s)" });
            int current = ReadIntSetting("GPU", "compile_batch_ms", 0);
            int index = Array.IndexOf(batchValues, current);
            batch.SelectedIndex = index >= 0 ? index : 0;
            batch.SelectedIndexChanged += delegate
            {
                try { WriteSetting("GPU", "compile_batch_ms", batchValues[batch.SelectedIndex]); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            shaders.Controls.Add(new SettingRow("Compile batching (when pausing)", batch));
            batch.Enabled = mode.SelectedIndex == 0;
            mode.SelectedIndexChanged += delegate
            {
                batch.Enabled = mode.SelectedIndex == 0;
                try { WriteSetting("GPU", "compile_mode", mode.SelectedIndex); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };

            // ---- About > Logging, and whether the Advanced page shows
            var logging = Section(aboutPage, "Logging");
            var logLevel = new DarkComboBox { Width = 230, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            logLevel.Items.AddRange(LogFiles.Levels);
            logLevel.SelectedIndex = LogFiles.LevelIndex(settings.StdoutLevel);
            logLevel.SelectedIndexChanged += delegate
            {
                if (logLevel.SelectedIndex < 0) return;
                settings.StdoutLevel = LogFiles.Levels[logLevel.SelectedIndex];
                try { settings.Save(); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the logging level: " + e.Message); }
            };
            logging.Controls.Add(new SettingRow("stdout.log minimum level", logLevel));
            logging.Controls.Add(Hint("The selected level and below, from the next launch. All logs are in the logs folder; previous logs are kept with a timestamp."));
            // FEAT-GTS-438: the Advanced page (experimental renderer switches) only for those who want it.
            var advancedShown = new ToggleRow { Text = "Show the Advanced page (experimental renderer switches)", Checked = settings.ShowAdvanced };
            advancedShown.CheckedChanged += delegate
            {
                settings.ShowAdvanced = advancedShown.Checked;
                if (showAdvanced != null) showAdvanced(advancedShown.Checked);
                try { settings.Save(); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            logging.Controls.Add(advancedShown);
            logging.Controls.Add(Hint("Hiding it keeps the switches already set there; they still apply when the game starts."));

            // ---- Controls: which controller is which player (splitscreen) with a live preview, the
            // controller and keyboard bindings, and racing wheels and pedals (ryty-input.ini /
            // ryty-wheel.ini, read by libScePad).
            var controlsPage = NewPage("Controls", "Controls", "2 players for Arcade's Split Screen: pick each player's name and controller, and what each " +
                                                               "button does. In game, F3 or holding Share + Options (View + Menu) changes controllers.");
            var controls = Section(controlsPage, "Controllers and wheels");
            controls.Controls.Add(OpenRow("Controller ports: which controller plays as each player, with a live preview", OpenPorts));
            controls.Controls.Add(OpenRow("Button bindings: what each PS4 button is on your controller and keyboard", OpenBindings));
            controls.Controls.Add(OpenRow("Steering wheel: map your wheel and pedals; force feedback strength and feel", OpenWheel));
            var triggers = Section(controlsPage, "Adaptive triggers");
            var triggerStrength = new Slider
            {
                Minimum = 0, Maximum = 200, SmallChange = 5, LargeChange = 25, Width = 330, Suffix = "%",
                AccessibleName = "Trigger feedback strength"
            };
            triggerStrength.Value = Math.Max(0, Math.Min(200, ReadIntSetting("Input", "trigger_strength", 100)));
            triggerStrength.ValueChanged += delegate
            {
                try { WriteSetting("Input", "trigger_strength", triggerStrength.Value); }
                catch (Exception e) { MessageBox.Show(this, "Could not save trigger strength: " + e.Message); }
            };
            triggers.Controls.Add(new SettingRow("Trigger strength", triggerStrength));
            triggers.Controls.Add(Hint("0% off, 100% standard, 200% boosted. Updates while playing."));

            // ---- FEAT-GTS-437: Advanced, the experimental renderer and runtime switches (RYGT_* environment
            // variables) with a description on hover, and any other variable for GTSport.exe.
            var advancedHost = new Panel { Dock = DockStyle.Fill, BackColor = Dark.Panel, Visible = false, Padding = new Padding(46, 34, 46, 24) };
            var advancedPage = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            advancedHost.Controls.Add(advancedPage);
            pagesHost.Controls.Add(advancedHost);
            pages["Advanced"] = advancedHost;
            AdvancedPage.Build(advancedPage,
                PageTitle("Advanced", "Experimental renderer switches for testing and diagnosis. They can break rendering, slow the game " +
                                      "or write large logs; hover a switch for what it does. Saved as you change them; used the next time the game starts."),
                Dark.Card, root, settings.Env, list =>
                {
                    settings.Env = list;
                    try { settings.Save(); }
                    catch (Exception e) { MessageBox.Show(this, "Could not save the switches: " + e.Message); }
                });
            showAdvanced = delegate (bool on)
            {
                if (on) hiddenPages.Remove("Advanced"); else hiddenPages.Add("Advanced");
                pageItems["Advanced"].Visible = on;
                PlaceSidebarItems();
                if (!on && lastPage == "Advanced") ShowSettings("About", false); // hidden while open: back to About
            };
            showAdvanced(settings.ShowAdvanced);

            // The setting helpers build label + drop-down rows; they become full-width rows here.
            foreach (var pair in pages) if (pair.Key != "Advanced") Rowify(pair.Value); // the Advanced page lays out its own
            Dark.Apply(this);
            Dark.SeeThrough(home);
            log.BackColor = Dark.Field;
            log.ForeColor = Color.FromArgb(180, 186, 196);
            HandleCreated += delegate { Dark.TitleBar(this); };
            Shown += delegate
            {
                ActiveControl = play.Enabled ? (Control)play : gear;
                BeginInvoke((MethodInvoker)PromptObsoleteSettings); // once the window is up
            };
            RefreshState();
        }

        /// The sidebar's page list, top to bottom, leaving out hidden pages (Advanced when it is off).
        void PlaceSidebarItems()
        {
            int y = 150;
            foreach (var name in pageNames)
            {
                var item = pageItems[name];
                item.Location = new Point(0, y);
                if (!hiddenPages.Contains(name)) y += 40;
            }
        }

        /// Shows the home view (the picture, Play).
        public void ShowHome(bool animate = true)
        {
            if (home.Visible) return;
            Fade(settingsView, delegate { settingsView.Visible = false; home.Visible = true; }, home, animate);
        }

        /// Shows the settings at one of their pages (Game, Display, Graphics, Shaders, Controls, Advanced, About).
        public void ShowSettings(string page, bool animate = true)
        {
            if (!pages.ContainsKey(page) || hiddenPages.Contains(page)) page = "Game";
            lastPage = page;
            foreach (var pair in pages) pair.Value.Visible = pair.Key == page;
            foreach (var pair in pageItems) pair.Value.Active = pair.Key == page;
            if (settingsView.Visible) return;
            Fade(home, delegate { home.Visible = false; settingsView.Visible = true; }, settingsView, animate);
        }

        /// For --snapshot: 0 home, 1 the settings (Display), 2 the controls.
        public void ShowPage(int index)
        {
            if (index == 0) ShowHome(false);
            else ShowSettings(index == 2 ? "Controls" : "Display", false);
        }

        bool fading;

        /// Changes views behind the curtain: the old view darkens (120 ms), then the new one
        /// comes up out of the dark (220 ms).
        void Fade(Control from, Action change, Control to, bool animate)
        {
            if (!animate || fading || !IsHandleCreated || !Visible || !from.Visible || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                change();
                return;
            }
            fading = true;
            var before = new Bitmap(ClientSize.Width, ClientSize.Height);
            from.DrawToBitmap(before, new Rectangle(Point.Empty, from.Size));
            curtain.Show(before, 0);
            curtain.Visible = true;
            curtain.BringToFront();
            change();
            Bitmap after = null;
            int start = Environment.TickCount;
            const int Out = 120, In = 220;
            Anim.Run(() =>
            {
                int t = Environment.TickCount - start;
                if (t < Out)
                {
                    curtain.Show(before, t / (float)Out);
                    return true;
                }
                if (after == null)
                {
                    after = new Bitmap(ClientSize.Width, ClientSize.Height);
                    to.DrawToBitmap(after, new Rectangle(Point.Empty, to.Size));
                }
                float k = Anim.Ease((t - Out) / (float)In);
                curtain.Show(after, 1 - k);
                if (k < 1) return true;
                curtain.Visible = false;
                curtain.Show(null, 0);
                before.Dispose();
                after.Dispose();
                fading = false;
                return false;
            });
        }

        /// A settings page: a scrolling column with its title and the line under it.
        TableLayoutPanel NewPage(string name, string title, string subtitle)
        {
            var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Dark.Panel, Visible = false, Padding = new Padding(46, 34, 46, 30) };
            var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = new Padding(0) };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.Controls.Add(PageTitle(title, subtitle));
            page.Controls.Add(content);
            pagesHost.Controls.Add(page);
            pages[name] = page;
            return content;
        }

        /// A page's large title and the grey line under it.
        static Control PageTitle(string title, string subtitle)
        {
            var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
            panel.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI Light", 22f), Margin = new Padding(0, 0, 0, 2) });
            panel.Controls.Add(new Label { Text = subtitle, AutoSize = true, MaximumSize = new Size(900, 0), ForeColor = Dark.DimText, Tag = "heading", Margin = new Padding(2, 0, 0, 0) });
            return panel;
        }

        /// A group of settings under a small spaced capital heading with a line under it.
        TableLayoutPanel Section(TableLayoutPanel parent, string heading)
        {
            var panel = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 22, 0, 0) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var label = new Label
            {
                Text = Dark.Spaced(heading.ToUpperInvariant()), AutoSize = false, Dock = DockStyle.Fill, Height = 26, Font = Dark.Caps(8.5f),
                ForeColor = Dark.Heading, Tag = "heading", Margin = new Padding(0), TextAlign = ContentAlignment.TopLeft
            };
            label.Paint += (sender, e) => { using (var line = new Pen(Dark.Line)) e.Graphics.DrawLine(line, 0, label.Height - 1, label.Width, label.Height - 1); };
            panel.Controls.Add(label);
            parent.Controls.Add(panel);
            return panel;
        }

        static Label Hint(string text)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(860, 0), ForeColor = Dark.DimText, Tag = "heading", Margin = new Padding(0, 8, 0, 0) };
        }

        /// A row with a button that opens a window or a folder.
        static SettingRow OpenRow(string text, Action action, string button = "Open")
        {
            var open = new Button { Text = button, AutoSize = true };
            open.Click += delegate { action(); };
            return new SettingRow(text, open);
        }

        /// The label + drop-down rows the setting helpers build (in FlowLayoutPanels) become
        /// SettingRows, in the same place.
        static void Rowify(Control root)
        {
            foreach (var child in root.Controls.Cast<Control>().ToList())
            {
                var flow = child as FlowLayoutPanel;
                if (flow != null && flow.Controls.Count == 2 && flow.Controls[0] is Label && flow.Controls[1] is ListControl)
                {
                    var text = flow.Controls[0].Text;
                    var control = flow.Controls[1];
                    control.Width = Math.Max(control.Width, 260);
                    int index = root.Controls.GetChildIndex(flow);
                    flow.Controls.Clear();
                    root.Controls.Remove(flow);
                    var row = new SettingRow(text, control);
                    root.Controls.Add(row);
                    root.Controls.SetChildIndex(row, index);
                    continue;
                }
                Rowify(child);
            }
        }

        /// The logo, launcher\logo.png next to RyGTS.exe (null without it: the title is text).
        Image LoadLogo()
        {
            string path = Path.Combine(root, "launcher", "logo.png");
            try { return File.Exists(path) ? Image.FromStream(new MemoryStream(File.ReadAllBytes(path))) : null; }
            catch { return null; }
        }

        void OpenSubfolder(string name)
        {
            string path = Path.Combine(root, name);
            try { Directory.CreateDirectory(path); } catch { }
            Process.Start("explorer.exe", "\"" + path + "\"");
        }

        void OpenPorts()
        {
            using (var form = new PortsForm(ReadStringSetting("Input", "controller_ports", ""),
                                            ReadStringSetting("General", "player_names", "").Split(','),
                                            (ports, names) =>
                                            {
                                                WriteSetting("Input", "controller_ports", ports);
                                                // Read by the game's user service (libSceUserService).
                                                WriteSetting("General", "player_names",
                                                             string.Join(",", names.Select(n => n.Replace(",", ""))));
                                            }))
            {
                form.ShowDialog(this);
            }
        }

        void OpenBindings()
        {
            using (var form = new ControlsForm(root, Math.Max(1, ReadIntSetting("Input", "keyboard_player", 1)),
                                               player => WriteSetting("Input", "keyboard_player", player)))
            {
                form.ShowDialog(this);
            }
        }

        void OpenWheel()
        {
            using (var form = new WheelForm(root)) form.ShowDialog(this);
        }

        void OpenFolder()
        {
            Process.Start("explorer.exe", "\"" + root + "\"");
        }

        // ---- The pictures behind the home view

        readonly List<Image> pictures;
        List<Image> LoadBackgrounds()
        {
            var found = new List<Image>();
            string folder = Path.Combine(root, "launcher");
            try
            {
                if (Directory.Exists(folder))
                {
                    var paths = Directory.GetFiles(folder, "background*.*")
                        .Where(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                        .Where(p => Path.GetFileName(p).IndexOf("bak", StringComparison.OrdinalIgnoreCase) < 0)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
                    foreach (var path in paths)
                    {
                        var picture = LoadPicture(path);
                        if (picture != null) found.Add(picture);
                    }
                }
            }
            catch { }
            if (found.Count == 0)
            {
                foreach (var path in new[] { Path.Combine(root, "app0", "sce_sys", "pic1.png"), Path.Combine(settings.Game, "sce_sys", "pic1.png") })
                {
                    var picture = File.Exists(path) ? LoadPicture(path) : null;
                    if (picture != null) { found.Add(picture); break; }
                }
            }
            return found;
        }

        static Image LoadPicture(string path)
        {
            try
            {
                var picture = Image.FromStream(new MemoryStream(File.ReadAllBytes(path)));
                if (picture.Width <= 2560) return picture;
                // A window never needs more; a 4K picture would hold 33 MB of memory.
                using (picture)
                    return new Bitmap(picture, 2560, picture.Height * 2560 / picture.Width);
            }
            catch { return null; }
        }

        /// The home view's picture (for --snapshot and the controls over it).
        public bool DrawBackdrop(Control control, Graphics g, bool raw)
        {
            return home.Visible && home.DrawBackdrop(control, g, raw);
        }

        /// A file setting: its name, the folder in a field, and a Browse button.
        TextBox AddRow(TableLayoutPanel section, string label, string value)
        {
            var inner = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var field = new Card(Dark.Field) { AutoSize = false, Size = new Size(520, 30), Radius = 3, Edge = Color.Empty, Padding = new Padding(10, 7, 10, 4), Margin = new Padding(0, 0, 8, 0) };
            var box = new TextBox { Text = value, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Margin = new Padding(0) };
            field.Controls.Add(box);
            inner.Controls.Add(field, 0, 0);
            var browse = new Button { Text = "Browse", AutoSize = true, Margin = new Padding(0) };
            browse.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog { Description = label, SelectedPath = box.Text })
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
                }
            };
            inner.Controls.Add(browse, 1, 0);
            section.Controls.Add(new SettingRow(label, inner));
            return box;
        }

        string Exe { get { return Path.Combine(root, "GTSport.exe"); } }

        string ConfigPath { get { return Path.Combine(root, "user", "custom_configs", "CUSA03220.json"); } }

        /// A shadGT setting in a section of the GT Sport config (General: show_splash,
        /// show_fps_counter, discord_rpc_enabled; GPU: full_screen, particle_effects, fsr_enabled, fsr3_enabled, rcas_enabled, dlss_enabled,
        /// framegen_enabled, precompile_shaders, compile_freeze).
        bool ReadSetting(string sectionName, string key, bool fallback)
        {
            try
            {
                var config = LoadConfig();
                object found, value;
                var section = config.TryGetValue(sectionName, out found) ? found as Dictionary<string, object> : null;
                if (section != null && section.TryGetValue(key, out value) && value is bool) return (bool)value;
            }
            catch { }
            return fallback;
        }

        int ReadIntSetting(string sectionName, string key, int fallback)
        {
            try
            {
                var config = LoadConfig();
                object found, value;
                var section = config.TryGetValue(sectionName, out found) ? found as Dictionary<string, object> : null;
                if (section != null && section.TryGetValue(key, out value) && value is int) return (int)value;
            }
            catch { }
            return fallback;
        }

        void WriteSetting(string sectionName, string key, object on)
        {
            var config = LoadConfig();
            object found;
            var section = config.TryGetValue(sectionName, out found) ? found as Dictionary<string, object> : null;
            if (section == null) { section = new Dictionary<string, object>(); config[sectionName] = section; }
            section[key] = on;
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            File.WriteAllText(ConfigPath, new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(config),
                              new UTF8Encoding(false));
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        static int InstalledRamGb()
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
            if (!GlobalMemoryStatusEx(ref status)) return 32;
            return (int)Math.Round(status.TotalPhys / (1024.0 * 1024 * 1024));
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public ushort SpecVersion, DriverVersion, Size, DriverExtra;
            public uint Fields;
            public int PositionX, PositionY;
            public uint DisplayOrientation, DisplayFixedOutput;
            public short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public ushort LogPixels;
            public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
            public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettings(string device, int mode, ref DevMode devMode);

        class Descending<T> : IComparer<T> where T : IComparable<T>
        {
            public int Compare(T a, T b) { return b.CompareTo(a); }
        }

        /// The primary display's modes (resolution, largest first -> refresh rates in Hz, highest
        /// first) and its current mode.
        static SortedDictionary<long, SortedSet<int>> DisplayModes(out int currentWidth, out int currentHeight, out int currentHz)
        {
            var modes = new SortedDictionary<long, SortedSet<int>>(new Descending<long>());
            var mode = new DevMode { Size = (ushort)Marshal.SizeOf(typeof(DevMode)) };
            for (int i = 0; EnumDisplaySettings(null, i, ref mode); ++i)
            {
                if (mode.BitsPerPel < 24 || mode.PelsWidth < 1280 || mode.PelsHeight < 720) continue;
                long key = ((long)mode.PelsWidth << 32) | mode.PelsHeight;
                SortedSet<int> rates;
                if (!modes.TryGetValue(key, out rates)) modes[key] = rates = new SortedSet<int>(new Descending<int>());
                if (mode.DisplayFrequency > 1) rates.Add((int)mode.DisplayFrequency);
            }
            currentWidth = currentHeight = currentHz = 0;
            if (EnumDisplaySettings(null, -1, ref mode))
            {
                currentWidth = (int)mode.PelsWidth;
                currentHeight = (int)mode.PelsHeight;
                currentHz = (int)mode.DisplayFrequency;
            }
            return modes;
        }

        string ReadStringSetting(string sectionName, string key, string fallback)
        {
            try
            {
                var config = LoadConfig();
                object found, value;
                var section = config.TryGetValue(sectionName, out found) ? found as Dictionary<string, object> : null;
                if (section != null && section.TryGetValue(key, out value) && value is string) return (string)value;
            }
            catch { }
            return fallback;
        }

        static string ModeText(long key)
        {
            return string.Format("{0} x {1}", key >> 32, key & 0xffffffff);
        }

        // Screen: windowed, borderless fullscreen (the desktop's resolution and refresh rate), or
        // exclusive fullscreen at a chosen resolution and refresh rate (GPU.full_screen,
        // full_screen_mode, window_width/height, fullscreen_refresh_rate). The game draws at
        // 1920x1080 and runs at up to 60 fps; FSR or DLSS scale it to the screen, and a higher
        // refresh rate shows frame generation's extra frames.
        void SaveVulkanDevice(VulkanDevice device)
        {
            var config = LoadConfig();
            object found;
            var section = config.TryGetValue("Vulkan", out found) ? found as Dictionary<string, object> : null;
            if (section == null) { section = new Dictionary<string, object>(); config["Vulkan"] = section; }
            section["gpu_id"] = device.Index;
            // Extra identity fields are launcher-owned; the renderer consumes gpu_id.
            section["device_uuid"] = device.Index < 0 ? "" : device.Uuid;
            section["device_name"] = device.Index < 0 ? "" : device.Name;
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            File.WriteAllText(ConfigPath, new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(config),
                              new UTF8Encoding(false));
        }

        void ResolveVulkanDevice()
        {
            string uuid = ReadStringSetting("Vulkan", "device_uuid", "");
            int index = ReadIntSetting("Vulkan", "gpu_id", -1);
            if (uuid.Length == 0 && index < 0) return;
            var devices = VulkanDevices.Enumerate();
            var device = uuid.Length != 0
                ? devices.FirstOrDefault(d => string.Equals(d.Uuid, uuid, StringComparison.OrdinalIgnoreCase))
                : devices.FirstOrDefault(d => d.Index == index);
            if (device == null)
                throw new Exception("The selected Vulkan device is unavailable. Choose another device or Automatic in Settings > Display.");
            if (device.ApiVersion < ((1u << 22) | (3u << 12)))
                throw new Exception("The selected device does not support Vulkan 1.3. Choose another Vulkan device in Settings > Display.");
            if (device.Index != index || uuid.Length == 0) SaveVulkanDevice(device);
        }

        void AddVulkanDeviceSetting(TableLayoutPanel panel)
        {
            var row = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent,
                Margin = new Padding(0, 2, 0, 2), WrapContents = false };
            row.Controls.Add(new Label { Text = "Vulkan device", Width = 104, Height = 20,
                BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
            var combo = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 252, DropDownWidth = 500, FlatStyle = FlatStyle.Flat };
            var automatic = new VulkanDevice { Index = -1, Name = "Automatic", Uuid = "" };
            combo.Items.Add(automatic);
            string uuid = ReadStringSetting("Vulkan", "device_uuid", "");
            int index = ReadIntSetting("Vulkan", "gpu_id", -1);
            VulkanDevice selected = uuid.Length == 0 && index < 0 ? automatic : null;
            string error = null;
            try
            {
                foreach (var device in VulkanDevices.Enumerate())
                {
                    if (device.ApiVersion < ((1u << 22) | (3u << 12))) device.Name += " (below Vulkan 1.3)";
                    combo.Items.Add(device);
                    if (uuid.Length != 0 ? string.Equals(device.Uuid, uuid, StringComparison.OrdinalIgnoreCase)
                                         : device.Index == index) selected = device;
                }
            }
            catch (Exception e) { error = "Could not list Vulkan devices: " + e.Message; }
            if (selected == null)
            {
                selected = new VulkanDevice { Index = -2, Uuid = uuid,
                    Name = ReadStringSetting("Vulkan", "device_name", "GPU " + index) + " (unavailable)" };
                combo.Items.Add(selected);
            }
            combo.SelectedItem = selected;
            combo.SelectedIndexChanged += delegate
            {
                var device = combo.SelectedItem as VulkanDevice;
                if (device == null || device.Index == -2) return;
                try { SaveVulkanDevice(device); }
                catch (Exception e) { MessageBox.Show(this, "Could not save Vulkan device: " + e.Message); }
            };
            row.Controls.Add(combo);
            panel.Controls.Add(row);
            if (error != null) panel.Controls.Add(new Label { Text = error, AutoSize = true,
                MaximumSize = new Size(365, 0), BackColor = Color.Transparent, ForeColor = Color.Silver });
        }

        void AddScreenSettings(TableLayoutPanel panel)
        {
            int desktopWidth, desktopHeight, desktopHz;
            var modes = DisplayModes(out desktopWidth, out desktopHeight, out desktopHz);

            var screenRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2), WrapContents = false };
            var screen = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            screen.Items.AddRange(new object[] { "Windowed", "Borderless fullscreen", "Exclusive fullscreen" });
            bool full = ReadSetting("GPU", "full_screen", false);
            bool exclusive = ReadStringSetting("GPU", "full_screen_mode", "Windowed") == "Fullscreen";
            screen.SelectedIndex = !full ? 0 : exclusive ? 2 : 1;
            screenRow.Controls.Add(new Label { Text = "Screen", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
            screenRow.Controls.Add(screen);
            panel.Controls.Add(screenRow);

            var resolutionRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2), WrapContents = false };
            var resolution = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            var keys = modes.Keys.ToList();
            int width = ReadIntSetting("GPU", "window_width", 0), height = ReadIntSetting("GPU", "window_height", 0);
            if (width < 640 || height < 360)
            {
                width = desktopWidth > 0 ? Math.Min(1920, desktopWidth) : 1920;
                height = desktopHeight > 0 ? Math.Min(1080, desktopHeight) : 1080;
            }
            long wanted = ((long)width << 32) | (uint)height;
            if (!keys.Contains(wanted)) keys.Add(wanted);
            foreach (var key in keys) resolution.Items.Add(ModeText(key));
            resolution.SelectedIndex = keys.IndexOf(wanted);
            resolutionRow.Controls.Add(new Label { Text = "Resolution", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
            resolutionRow.Controls.Add(resolution);
            panel.Controls.Add(resolutionRow);

            var refreshRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2), WrapContents = false };
            var refresh = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            refreshRow.Controls.Add(new Label { Text = "Refresh rate", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
            refreshRow.Controls.Add(refresh);
            panel.Controls.Add(refreshRow);

            bool loading = true;
            Action<int> fillRates = delegate (int wantedHz)
            {
                refresh.Items.Clear();
                refresh.Items.Add(desktopHz > 0 ? string.Format("Desktop ({0} Hz)", desktopHz) : "Desktop");
                SortedSet<int> rates;
                if (modes.TryGetValue(keys[resolution.SelectedIndex], out rates))
                {
                    foreach (int hz in rates) refresh.Items.Add(hz + " Hz");
                }
                int index = refresh.Items.IndexOf(wantedHz + " Hz");
                refresh.SelectedIndex = wantedHz > 0 && index > 0 ? index : 0;
            };
            Action save = delegate
            {
                if (loading) return;
                try
                {
                    WriteSetting("GPU", "full_screen", screen.SelectedIndex != 0);
                    WriteSetting("GPU", "full_screen_mode", screen.SelectedIndex == 2 ? "Fullscreen" : "Borderless");
                    long key = keys[resolution.SelectedIndex];
                    WriteSetting("GPU", "window_width", (int)(key >> 32));
                    WriteSetting("GPU", "window_height", (int)(key & 0xffffffff));
                    WriteSetting("GPU", "fullscreen_refresh_rate",
                                 refresh.SelectedIndex > 0 ? int.Parse(((string)refresh.SelectedItem).Replace(" Hz", "")) : 0);
                }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            Action enable = delegate
            {
                // Borderless fullscreen always uses the desktop's mode.
                resolution.Enabled = screen.SelectedIndex != 1;
                refresh.Enabled = screen.SelectedIndex == 2;
            };
            fillRates(ReadIntSetting("GPU", "fullscreen_refresh_rate", 0));
            enable();
            screen.SelectedIndexChanged += delegate { enable(); save(); };
            resolution.SelectedIndexChanged += delegate
            {
                if (loading) return;
                loading = true;
                int hz = refresh.SelectedIndex > 0 ? int.Parse(((string)refresh.SelectedItem).Replace(" Hz", "")) : 0;
                fillRates(hz);
                loading = false;
                save();
            };
            refresh.SelectedIndexChanged += delegate { save(); };
            loading = false;
        }

        // System memory (GPU.system_ram_gb): how much the game may use for things it can do
        // without. Both profiles retain the shader store, startup precompilation and persistent
        // pipeline cache. The 16 GB profile keeps fewer freed GPU images for reuse.
        // Picked from installed RAM the first time; shader precompilation has its own checkbox.
        // Screen shape (GPU.aspect_ratio, GTS-009 in renderer patch 0051): GT Sport draws a 16:9
        // picture. "Fill the screen" widens the race camera to the shape of the game's window or
        // screen and stretches the picture over it (ultrawide without black bars; the HUD and menus
        // are stretched too). "True wide" (GPU.wide_scene, WIDE-001 in the renderer) renders the
        // scene at the screen's shape instead: nothing is stretched, the race HUD moves to the
        // screen's edges and the menus stay 16:9 in the middle. It needs the native renderer and
        // the UI at screen resolution, which it turns on. Read when the game starts.
        void AddShapeSetting(TableLayoutPanel panel)
        {
            var row = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2), WrapContents = false };
            var label = new Label { Text = "Screen shape", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) };
            var shape = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, FlatStyle = FlatStyle.Flat };
            shape.Items.AddRange(new object[] { "16:9 (the game's own)", "Fill the screen (stretched)", "True wide (ultrawide, 21:9 / 32:9)" });
            bool fill = ReadStringSetting("GPU", "aspect_ratio", "16:9") == "auto";
            shape.SelectedIndex = !fill ? 0 : ReadSetting("GPU", "wide_scene", false) ? 2 : 1;
            row.Controls.Add(label);
            row.Controls.Add(shape);
            panel.Controls.Add(row);

            // Wide scene height (GPU.wide_scene_height): the height the true wide scene renders at;
            // its width follows the screen's shape. Lower is faster.
            var heightRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2), WrapContents = false };
            var heightLabel = new Label { Text = "Wide scene height", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) };
            var height = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, FlatStyle = FlatStyle.Flat };
            var heights = new[] { 1080, 1200, 1440 };
            height.Items.AddRange(new object[] { "1080 (recommended)", "1200", "1440 (sharpest, slowest)" });
            int savedHeight = ReadIntSetting("GPU", "wide_scene_height", 0);
            if (savedHeight == 0) int.TryParse(ReadStringSetting("GPU", "wide_scene_height", "1080"), out savedHeight);
            height.SelectedIndex = Math.Max(0, Array.IndexOf(heights, savedHeight));
            heightRow.Controls.Add(heightLabel);
            heightRow.Controls.Add(height);
            panel.Controls.Add(heightRow);
            var tip = new Label { AutoSize = true, ForeColor = Dark.DimText, Tag = "heading", BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 4),
                Text = "Tip: 1080 runs best and still fills the whole screen width; 1200 and 1440 are sharper but\n" +
                       "cost fps in big races. True wide turns on the native renderer and the UI at screen resolution." };
            panel.Controls.Add(tip);

            Action showHeight = delegate
            {
                height.Enabled = shape.SelectedIndex == 2;
                heightLabel.Enabled = height.Enabled;
            };
            showHeight();
            shape.SelectedIndexChanged += delegate
            {
                try
                {
                    bool wide = shape.SelectedIndex == 2;
                    WriteSetting("GPU", "aspect_ratio", shape.SelectedIndex == 0 ? "16:9" : "auto");
                    WriteSetting("GPU", "wide_scene", wide);
                    if (wide)
                    {
                        WriteSetting("GPU", "wide_scene_height", heights[height.SelectedIndex]);
                        // What the true wide scene needs; the controls show it too.
                        if (uiHiresBox != null) uiHiresBox.Checked = true; else WriteSetting("GPU", "ui_hires", true);
                        if (rendererCombo != null) rendererCombo.SelectedIndex = 1; else WriteSetting("GPU", "native_renderer", true);
                    }
                }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
                showHeight();
            };
            height.SelectedIndexChanged += delegate
            {
                try { WriteSetting("GPU", "wide_scene_height", heights[height.SelectedIndex]); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
        }

        // The controls the true wide screen shape turns on (set when they are made).
        CheckBox uiHiresBox;
        ComboBox rendererCombo;

        void AddMemorySetting(TableLayoutPanel panel)
        {
            var row = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 2), WrapContents = false };
            var label = new Label { Text = "System memory (RAM)", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) };
            var ram = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            ram.Items.AddRange(new object[] { "16 GB (minimum)", "32 GB+ (recommended)" });
            int stored = ReadIntSetting("GPU", "system_ram_gb", 0);
            int gb = stored > 0 ? stored : InstalledRamGb();
            ram.SelectedIndex = gb >= 24 ? 1 : 0;
            Action apply = delegate
            {
                bool large = ram.SelectedIndex == 1;
                WriteSetting("GPU", "system_ram_gb", large ? 32 : 16);
                WriteSetting("Vulkan", "pipeline_cache_enabled", true);
                WriteSetting("GPU", "image_recycle_mb", large ? 512 : 128);
            };
            if (stored == 0 || !ReadSetting("Vulkan", "pipeline_cache_enabled", true))
            {
                try { apply(); } catch { }
            }
            ram.SelectedIndexChanged += delegate
            {
                try { apply(); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            row.Controls.Add(label);
            row.Controls.Add(ram);
            panel.Controls.Add(row);
        }

        // Renderer (GPU.native_renderer): the native renderer (the default; GT Sport's command
        // buffers recorded straight into Vulkan, faster in races) or the old renderer (shadGT's,
        // with the direct memory renderer). RYGT_NATIVE=1 or =0 overrides the choice.
        void AddRendererSetting(TableLayoutPanel panel)
        {
            var row = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 4), WrapContents = false };
            var label = new Label { Text = "Renderer", AutoSize = false, Width = 110, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) };
            var renderer = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, FlatStyle = FlatStyle.Flat };
            renderer.Items.AddRange(new object[] { "Old renderer", "Native renderer (default)" });
            renderer.SelectedIndex = ReadSetting("GPU", "native_renderer", true) ? 1 : 0;
            rendererCombo = renderer;
            renderer.SelectedIndexChanged += delegate
            {
                try { WriteSetting("GPU", "native_renderer", renderer.SelectedIndex == 1); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            row.Controls.Add(label);
            row.Controls.Add(renderer);
            panel.Controls.Add(row);
        }

        // FEAT-GTS-469: like RPCS3's "Remove obsolete settings?", offer to drop config entries nothing
        // reads any more (ObsoleteSettings.cs). "Don't show again" is kept in RyGT.ini (prompt_obsolete).
        void PromptObsoleteSettings()
        {
            if (!settings.PromptObsolete) return;
            Dictionary<string, object> config;
            try { config = LoadConfig(); } catch { return; } // an unreadable config is reported where it is used
            var found = ObsoleteSettings.Find(config);
            if (found.Count == 0) return;
            using (var dialog = new ObsoleteSettingsDialog(found))
            {
                var answer = dialog.ShowDialog(this);
                if (dialog.DontShowAgain)
                {
                    settings.PromptObsolete = false;
                    try { settings.Save(); } catch { }
                }
                if (answer != DialogResult.Yes) return;
            }
            try
            {
                ObsoleteSettings.Remove(config);
                File.WriteAllText(ConfigPath, new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(config),
                                  new UTF8Encoding(false));
            }
            catch (Exception e) { MessageBox.Show(this, "Could not remove the obsolete settings: " + e.Message); }
        }

        // The native renderer is the default, but the renderer itself still falls back to the old one
        // when GPU.native_renderer is not set, so write the launcher's default before the game starts.
        // A choice already saved (either renderer) is kept.
        void ApplyNativeRendererDefault()
        {
            bool unset = ReadSetting("GPU", "native_renderer", true) && !ReadSetting("GPU", "native_renderer", false);
            if (unset) WriteSetting("GPU", "native_renderer", true);
        }

        void AddExperimentalRenderScale(TableLayoutPanel panel)
        {
            var enabled = AddOption(panel, "Scene render scale (EXPERIMENTAL)",
                "GPU", "experimental_render_scale", false);
            var preset = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
            var values = new[] { "ultra_performance", "performance", "balanced", "quality", "ultra_quality", "dlaa" };
            preset.Items.AddRange(new object[] { "33% - Ultra Performance (1080p to 640 x 360)", "50% - Performance (1080p to 960 x 540)", "59% - Balanced (1080p to 1136 x 639)", "67% - Quality (1080p to 1280 x 720)", "77% - Ultra Quality (1080p to 1472 x 828)", "100% - Native (1920 x 1080)" });
            preset.SelectedIndex = Math.Max(0, Array.IndexOf(values,
                ReadStringSetting("GPU", "experimental_render_preset", "performance")));
            preset.Enabled = enabled.Checked;
            preset.SelectedIndexChanged += delegate {
                try { WriteSetting("GPU", "experimental_render_preset", values[preset.SelectedIndex]); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            enabled.CheckedChanged += delegate { preset.Enabled = enabled.Checked; };
            panel.Controls.Add(preset);
            var preview = new Label { AutoSize = true, ForeColor = Dark.DimText,
                Margin = new Padding(22, 2, 0, 4) };
            Action refreshPreview = delegate {
                try {
                    var output = RenderScaleOutputSize();
                    var scene = RenderResolution.Calculate(output.Width, output.Height, values[preset.SelectedIndex]);
                    preview.Text = "Scene: " + scene.Width + "x" + scene.Height + " -> output " +
                        output.Width + "x" + output.Height + ". Restart required.\n" +
                        "Works with or without an upscaler. Native renderer; restart required.\nExperimental; post-processing may differ. Off by default.";
                } catch (Exception e) { preview.Text = e.Message; }
            };
            preset.SelectedIndexChanged += delegate { refreshPreview(); };
            enabled.CheckedChanged += delegate { refreshPreview(); };
            refreshPreview();
            panel.Controls.Add(preview);
        }

        Size RenderScaleOutputSize()
        {
            int width = ReadIntSetting("GPU", "window_width", 1280);
            int height = ReadIntSetting("GPU", "window_height", 720);
            if (ReadSetting("GPU", "full_screen", false) &&
                ReadStringSetting("GPU", "full_screen_mode", "Borderless") != "Fullscreen")
            {
                var screen = Screen.FromControl(this).Bounds;
                width = screen.Width; height = screen.Height;
            }
            return new Size(width, height);
        }

        Size? experimentalSceneSize;

        void ApplyExperimentalRenderScale()
        {
            experimentalSceneSize = null;
            if (!ReadSetting("GPU", "experimental_render_scale", false)) return;
            if (!ReadSetting("GPU", "native_renderer", true))
                throw new InvalidOperationException("Experimental scene resolution requires the Native renderer.");
            if (ReadSetting("GPU", "ps4_pro", false))
                throw new InvalidOperationException("Turn off PS4 Pro mode before using experimental scene resolution.");
            bool dlss = ReadSetting("GPU", "dlss_enabled", false);
            var outputSize = RenderScaleOutputSize();
            string preset = ReadStringSetting("GPU", "experimental_render_preset", "performance");
            var size = RenderResolution.Calculate(outputSize.Width, outputSize.Height, preset);
            experimentalSceneSize = size;
            WriteSetting("GPU", "experimental_render_width", size.Width);
            WriteSetting("GPU", "experimental_render_height", size.Height);
            if (dlss) WriteSetting("GPU", "dlss_quality", preset == "ultra_quality" ? "quality" : preset);
            else if (ReadSetting("GPU", "fsr_enabled", false)) WriteSetting("GPU", "upscale_ratio", "0");
        }

        /// DLSS sharpening (GPU.dlss_sharpening, 0 off): a slider row, enabled while DLSS is on.
        SettingRow AddDlssSharpening(TableLayoutPanel panel, CheckBox dlss)
        {
            var slider = new Slider
            {
                Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10, Width = 330, Suffix = "%",
                AccessibleName = "DLSS sharpening"
            };
            slider.Value = Math.Max(0, Math.Min(100, ReadIntSetting("GPU", "dlss_sharpening", 0)));
            Action refresh = delegate { slider.Enabled = dlss.Checked && dlss.Enabled; };
            slider.ValueChanged += delegate
            {
                try { WriteSetting("GPU", "dlss_sharpening", slider.Value); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            dlss.CheckedChanged += delegate { refresh(); };
            dlss.EnabledChanged += delegate { refresh(); };
            refresh();
            var row = new SettingRow("DLSS sharpening (0% off; needs DLSS)", slider);
            panel.Controls.Add(row);
            return row;
        }

        CheckBox AddOption(TableLayoutPanel panel, string text, string sectionName, string key, bool fallback)
        {
            var box = new ToggleRow { Text = text, Checked = ReadSetting(sectionName, key, fallback) };
            box.CheckedChanged += delegate
            {
                try { WriteSetting(sectionName, key, box.Checked); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            panel.Controls.Add(box);
            return box;
        }

        Dictionary<string, object> LoadConfig()
        {
            if (!File.Exists(ConfigPath)) return new Dictionary<string, object>();
            var parsed = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(File.ReadAllText(ConfigPath));
            return parsed as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        internal static void InstallDlssLibrary(string source, string directory)
        {
            if (!string.Equals(Path.GetFileName(source), "nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Select the NVIDIA Super Resolution file named nvngx_dlss.dll.");
            using (var reader = new BinaryReader(File.OpenRead(source)))
            {
                if (reader.BaseStream.Length < 64 || reader.ReadUInt16() != 0x5a4d)
                    throw new InvalidDataException("The selected file is not a Windows DLL.");
                reader.BaseStream.Position = 0x3c;
                int pe = reader.ReadInt32();
                if (pe < 64 || pe > reader.BaseStream.Length - 26)
                    throw new InvalidDataException("The selected DLL has an invalid PE header.");
                reader.BaseStream.Position = pe;
                if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664)
                    throw new InvalidDataException("DLSS requires a 64-bit Windows DLL.");
                reader.BaseStream.Position = pe + 22;
                if ((reader.ReadUInt16() & 0x2000) == 0 || reader.ReadUInt16() != 0x20b)
                    throw new InvalidDataException("DLSS requires a 64-bit Windows DLL.");
            }
            var version = FileVersionInfo.GetVersionInfo(source);
            if ((version.CompanyName ?? "").IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) < 0 ||
                (version.FileDescription ?? "").IndexOf("DLSS", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidDataException("The selected file is not an NVIDIA DLSS library.");
            string destination = Path.Combine(directory, "nvngx_dlss.dll");
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(directory);
            string temporary = destination + ".install-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source, temporary);
                if (File.Exists(destination))
                    File.Replace(temporary, destination, destination + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".bak");
                else
                    File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        void RefreshState()
        {
            bool ready = File.Exists(Exe) && Directory.Exists(Path.Combine(root, "app0", "sce_module"));
            play.Enabled = ready && !busy;
            setup.Enabled = !busy;
            addDlss.Enabled = !busy;
            if (!busy) status.Text = ready ? "Ready. Press Play." : "Not set up yet: open the settings (the gear), pick your game's folders and press Set up.";
        }

        readonly object logLock = new object();
        string setupLogPath;

        void Log(string line)
        {
            lock (logLock)
            {
                try
                {
                    if (setupLogPath == null)
                    {
                        setupLogPath = LogFiles.Prepare(root, "setup.log");
                        File.AppendAllText(setupLogPath, LauncherVersion.Banner + Environment.NewLine);
                    }
                    File.AppendAllText(setupLogPath, line + Environment.NewLine);
                }
                catch { }
            }
            if (!IsHandleCreated || IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLogText), line); return; }
            AppendLogText(line);
        }

        void AppendLogText(string line)
        {
            if (!IsDisposed) log.AppendText(line + Environment.NewLine);
        }
        void SetStatus(string text)
        {
            Log("== " + text);
            if (!IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(SetStatusText), text); return; }
            SetStatusText(text);
        }

        void SetStatusText(string text)
        {
            status.Text = text;
        }

        /// RyGTS.exe --setup: the same set up without the window, from the folders in RyGT.ini.
        public int SetupWithoutWindow()
        {
            try { setupLogPath = LogFiles.Prepare(root, "setup.log"); RunSetup(); return 0; }
            catch (Exception e) { Log("ERROR: " + e.Message); return 1; }
        }

        void ShowSetupDumpHelp(Exception error)
        {
            if (error is SetupDumpException)
                MessageBox.Show(this, error.Message, "Check your game dump and system modules",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void StartSetup()
        {
            settings.Game = game.Text.Trim();
            settings.Update = update.Text.Trim();
            settings.Modules = modules.Text.Trim();
            settings.Fonts = fonts.Text.Trim();
            try { settings.Save(); } catch { }
            try { setupLogPath = LogFiles.Prepare(root, "setup.log"); }
            catch (Exception e) { MessageBox.Show(this, "Could not prepare setup log: " + e.Message); return; }
            busy = true;
            RefreshState();
            log.Clear();
            var thread = new Thread(delegate ()
            {
                bool ok = false;
                Exception failure = null;
                try { RunSetup(); ok = true; }
                catch (Exception e) { failure = e; Log("ERROR: " + e.Message); }
                BeginInvoke(new Action(delegate
                {
                    busy = false;
                    RefreshState();
                    if (ok) status.Text = "Set up finished. Press Play.";
                    else
                    {
                        status.Text = "Set up failed; see the log.";
                        ShowSetupDumpHelp(failure);
                    }
                }));
            });
            thread.IsBackground = true;
            thread.Start();
        }

        void RunSetup()
        {
            GameBuildIntegrity.RequireStopped(Exe);
            string baseDir = settings.Game, updateDir = settings.Update;
            if (!Directory.Exists(baseDir)) throw new Exception("Pick the game folder (the one holding gt00.vol and eboot.bin).");
            if (!File.Exists(Path.Combine(baseDir, "gt00.vol")))
                throw new Exception("The game folder has no gt00.vol. Pick the base game folder of CUSA03220.");
            if (updateDir.Length > 0 && !Directory.Exists(updateDir)) throw new Exception("The update folder does not exist.");
            if (updateDir.Length == 0) updateDir = null;
            string gameDir = updateDir ?? baseDir; // the newest executable, modules and data index

            string version = ReadAppVersion(Path.Combine(gameDir, "sce_sys", "param.sfo"));
            Log("Game version: " + (version ?? "unknown"));
            if (version != null && version != "01.69")
                throw new Exception("This build is for update 1.69, but the game is " + version + ". Pick the 1.69 update folder too.");

            string fontDir, font2Dir;
            fontDir = font2Dir = null;
            if (!String.IsNullOrWhiteSpace(settings.Fonts))
                FindFonts(settings.Fonts, out fontDir, out font2Dir);
            else
                Log("Note: PS4 fonts are optional; without them, some in-game UI fonts may not draw or appear correctly.");
            var firmware = new Dictionary<string, string>();
            foreach (var name in FirmwareModules)
            {
                var found = FindModule(settings.Modules, name);
                if (found == null) throw new SetupDumpException("System module " + name + ".sprx was not found under " + settings.Modules + ".\n\n" +
                    "Re-dump the complete sys_modules folder from your PS4, including this module. Select the new folder, then run Set up again.");
                firmware[name] = found;
            }
            foreach (var name in OptionalFirmwareModules)
            {
                var found = FindModule(settings.Modules, name);
                if (found != null) firmware[name] = found;
                else Log("Note: " + name + " is not in the system modules folder; some related game features may be unavailable.");
            }

            // 1. The executable and modules to convert, unwrapped to plain ELF.
            SetStatus("Preparing the game files...");
            string work = Path.Combine(root, "convert");
            string workModules = Path.Combine(work, "sce_module");
            if (Directory.Exists(workModules)) Directory.Delete(workModules, true);
            Directory.CreateDirectory(workModules);
            Unwrap(Path.Combine(gameDir, "eboot.bin"), Path.Combine(work, "eboot.elf"));
            foreach (var name in new[] { "libc.prx", "libSceFios2.prx" })
            {
                string source = FirstExisting(Path.Combine(gameDir, "sce_module", name), Path.Combine(baseDir, "sce_module", name));
                if (source == null) throw new Exception("The game's sce_module\\" + name + " is missing.");
                Unwrap(source, Path.Combine(workModules, name));
            }
            foreach (var name in new[] { "libSceSecure.prx", "logiWheel.prx" })
            {
                string source = FirstExisting(Path.Combine(gameDir, name), Path.Combine(baseDir, name));
                if (source != null) Unwrap(source, Path.Combine(workModules, name));
            }
            foreach (var pair in firmware) Unwrap(pair.Value, Path.Combine(workModules, pair.Key + ".prx"));

            // 2. Convert.
            SetStatus("Converting the game for Windows (this takes a while)...");
            string converter = Path.Combine(root, "converter", "ryty.exe");
            if (!File.Exists(converter)) throw new Exception("converter\\ryty.exe is missing from the RyGTS folder.");
            string staging = Path.Combine(work, "game-rebuild");
            string stagedExe = Path.Combine(staging, "GTSport.exe");
            string stagedModules = Path.Combine(staging, "app0", "sce_module");
            Directory.CreateDirectory(stagedModules);
            int code = Run(converter, "--windows --ps4 --skip-syscall-check --registry --report \"" +
                Path.Combine(work, "port-report.json") + "\" \"" + Path.Combine(work, "eboot.elf") + "\" \"" + stagedExe + "\"", work);
            if (code != 0 || !File.Exists(stagedExe)) throw new Exception("The converter failed (exit code " + code + ").");
            GameExe.Prepare(stagedExe);
            GameExe.ApplyVersionInfo(stagedExe);
            // Keep the previous executable until conversion and preparation have both succeeded.
            Directory.CreateDirectory(Path.Combine(root, "app0", "sce_module"));
            foreach (string module in Directory.GetFiles(stagedModules))
                File.Copy(module, Path.Combine(root, "app0", "sce_module", Path.GetFileName(module)), true);
            string registry = Path.Combine(staging, "GTSport.registry.json");
            if (File.Exists(registry)) File.Copy(registry, Path.Combine(root, "GTSport.registry.json"), true);
            if (File.Exists(Exe)) File.Replace(stagedExe, Exe, null);
            else File.Move(stagedExe, Exe);
            Log(Wine.Detected ? "Made GTSport.exe a Windows program." : "Gave GTSport.exe the Gran Turismo Sport icon and made it a Windows program.");

            // 3. app0: the game's files, the update's over the base game's, linked rather than copied.
            SetStatus("Linking the game files...");
            LinkGameFiles(baseDir, updateDir);

            // 4. Fonts.
            if (fontDir != null && font2Dir != null)
            {
                SetStatus("Copying the fonts...");
                CopyDirectory(fontDir, Path.Combine(root, "sys", "common", "font"));
                CopyDirectory(font2Dir, Path.Combine(root, "sys", "common", "font2"));
            }

            // 5. Offline livery and decal scripts, made from this game's own files.
            SetStatus("Making the offline livery scripts from your game files...");
            if (!BuildGameScripts(baseDir, updateDir))
                Log("Note: the offline livery scripts could not be made; liveries will not apply offline.");

            // 6. Shaders other players have met, prepared from this game's own files, into the
            // shader folder every RyGT folder shares.
            UsePortableShaderCache();
            RequireGameClosedForShaderPreparation();
            if (File.Exists(ShaderPack))
            {
                SetStatus("Preparing shaders from your game files (a few minutes)...");
                if (!PrepareShaders(baseDir, updateDir))
                    Log("Note: the shaders could not be prepared; they will compile while you play.");
            }

            GameBuildIntegrity.Record(Exe);
            Log("Recorded GTSport.exe SHA-256: " + GameBuildIntegrity.Hash(Exe));
            Log("Set up finished.");
        }

        // The game scripts GTS-005 serves from patches\scripts: offline livery apply (UI_KIT, LIVERY2,
        // THUMBNAIL) and the decals folder (GALLERY, GRIM). They are made from Polyphony's scripts, so
        // they are not shipped: tools\adcpatch\adcpatch.exe reads them from the player's own game
        // volumes (GTToolsSharp) and patches them (GTAdhocToolchain), both by Nenkai, MIT licensed.
        static readonly string[] GameScripts = { "UI_KIT.ADC", "LIVERY2.ADC", "THUMBNAIL.ADC", "GALLERY.ADC", "GRIM.ADC" };

        bool GameScriptsMissing()
        {
            string dir = Path.Combine(root, "patches", "scripts");
            return GameScripts.Any(name => !File.Exists(Path.Combine(dir, name)));
        }

        // Missing, or made by another toolsdcpatch (a newer build ships a newer one): the scripts
        // folder keeps a hash of the adcpatch.exe that made it.
        bool GameScriptsOutdated()
        {
            if (GameScriptsMissing()) return true;
            string tool = Path.Combine(root, "tools", "adcpatch", "adcpatch.exe");
            if (!File.Exists(tool)) return false;
            string stamp = Path.Combine(root, "patches", "scripts", "made-by.txt");
            try { return !File.Exists(stamp) || File.ReadAllText(stamp).Trim() != ToolHash(tool); }
            catch { return true; }
        }

        static string ToolHash(string tool)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(tool))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        bool BuildGameScripts(string baseDir, string updateDir)
        {
            string tool = Path.Combine(root, "tools", "adcpatch", "adcpatch.exe");
            if (!File.Exists(tool)) { Log("tools\\adcpatch\\adcpatch.exe is missing."); return false; }
            string output = Path.Combine(root, "patches", "scripts");
            Func<string, string> arg = d => "\"" + (d ?? "").TrimEnd('\\') + "\"";
            string arguments = "all " + arg(baseDir) + " " + arg(updateDir) + " " + arg(output);
            Log("> adcpatch.exe " + arguments);
            var info = new ProcessStartInfo(tool, arguments)
            {
                WorkingDirectory = Path.GetDirectoryName(tool), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            var lines = new List<string>();
            using (var process = new Process { StartInfo = info })
            {
                DataReceivedEventHandler collect = delegate (object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (lines) lines.Add(e.Data);
                    if (e.Data.StartsWith("[OK]") || e.Data.StartsWith("[X]") || e.Data.Contains(" scripts patched"))
                        Log(e.Data);
                };
                process.OutputDataReceived += collect;
                process.ErrorDataReceived += collect;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                lock (lines)
                {
                    try { File.WriteAllLines(Path.Combine(Path.GetDirectoryName(tool), "adcpatch.log"), lines); } catch { }
                }
                bool ok = process.ExitCode == 0 && !GameScriptsMissing();
                if (ok)
                {
                    try { File.WriteAllText(Path.Combine(output, "made-by.txt"), ToolHash(tool)); } catch { }
                }
                return ok;
            }
        }

        // Shader pre-build: tools\adcpatch\shaders.pack holds the settings of the shaders and
        // pipelines RyGT's players have met (no shader code). shaderprep writes them into the
        // shader cache and copies each shader's program out of this PC's game files; the renderer
        // translates them on the splash screen and builds the pipelines there, so they don't
        // pause the game the first time a car or track shows up.
        string ShaderPack { get { return Path.Combine(root, "tools", "adcpatch", "shaders.pack"); } }
        string ShaderPackStamp { get { return Path.Combine(ShaderFolder, "shaders-pack.txt"); } }

        void RequireGameClosedForShaderPreparation()
        {
            if (Process.GetProcessesByName("GTSport").Length != 0)
                throw new IOException("Close the game before checking or updating its shader cache.");
        }

        bool ShadersOutdated()
        {
            if (!File.Exists(ShaderPack)) return false;
            try { return !File.Exists(ShaderPackStamp) || File.ReadAllText(ShaderPackStamp).Trim() != ToolHash(ShaderPack); }
            catch { return true; }
        }

        bool PrepareShaders(string baseDir, string updateDir)
        {
            string tool = Path.Combine(root, "tools", "adcpatch", "adcpatch.exe");
            if (!File.Exists(tool) || !File.Exists(ShaderPack)) return false;
            string cache = ShaderFolder;
            Directory.CreateDirectory(cache);
            Func<string, string> arg = d => "\"" + (d ?? "").TrimEnd('\\') + "\"";
            string arguments = "shaderprep " + arg(baseDir) + " " + arg(updateDir) + " " + arg(ShaderPack) + " " + arg(cache);
            Log("> adcpatch.exe " + arguments);
            var info = new ProcessStartInfo(tool, arguments)
            {
                WorkingDirectory = Path.GetDirectoryName(tool), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            var lines = new List<string>();
            using (var process = new Process { StartInfo = info })
            {
                DataReceivedEventHandler collect = delegate (object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (lines) lines.Add(e.Data);
                    if (e.Data.Contains(" programs copied")) Log(e.Data);
                };
                process.OutputDataReceived += collect;
                process.ErrorDataReceived += collect;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                lock (lines)
                {
                    try { File.WriteAllLines(Path.Combine(Path.GetDirectoryName(tool), "shaderprep.log"), lines); } catch { }
                }
                if (process.ExitCode != 0) return false;
                try { File.WriteAllText(ShaderPackStamp, ToolHash(ShaderPack)); } catch { }
                return true;
            }
        }

        static string ReadAppVersion(string sfo)
        {
            if (!File.Exists(sfo)) return null;
            var d = File.ReadAllBytes(sfo);
            if (d.Length < 20 || BitConverter.ToUInt32(d, 0) != 0x46535000) return null;
            int keys = (int)BitConverter.ToUInt32(d, 8), data = (int)BitConverter.ToUInt32(d, 12), count = (int)BitConverter.ToUInt32(d, 16);
            for (int i = 0; i < count; i++)
            {
                int e = 20 + i * 16;
                int keyOffset = BitConverter.ToUInt16(d, e);
                int length = (int)BitConverter.ToUInt32(d, e + 4);
                int dataOffset = (int)BitConverter.ToUInt32(d, e + 12);
                int end = keys + keyOffset;
                while (end < d.Length && d[end] != 0) end++;
                string key = Encoding.ASCII.GetString(d, keys + keyOffset, end - keys - keyOffset);
                if (key == "APP_VER") return Encoding.ASCII.GetString(d, data + dataOffset, length).TrimEnd('\0');
            }
            return null;
        }

        static void FindFonts(string selected, out string font, out string font2)
        {
            if (!Directory.Exists(selected)) throw new Exception("Pick the fonts folder (the one holding font and font2).");
            if (Directory.Exists(Path.Combine(selected, "font")) && Directory.Exists(Path.Combine(selected, "font2")))
            {
                font = Path.Combine(selected, "font");
                font2 = Path.Combine(selected, "font2");
                return;
            }
            var parent = Directory.GetParent(selected);
            if (parent != null && Path.GetFileName(selected).Equals("font", StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(Path.Combine(parent.FullName, "font2")))
            {
                font = selected;
                font2 = Path.Combine(parent.FullName, "font2");
                return;
            }
            throw new Exception("The fonts folder needs font and font2 folders inside it.");
        }

        static string FindModule(string folder, string name)
        {
            if (!Directory.Exists(folder)) throw new Exception("Pick the system modules folder.");
            foreach (var extension in new[] { ".sprx", ".prx", ".elf" })
            {
                var hit = Directory.EnumerateFiles(folder, name + extension, SearchOption.AllDirectories).FirstOrDefault();
                if (hit != null) return hit;
            }
            return null;
        }

        static string FirstExisting(params string[] paths)
        {
            return paths.FirstOrDefault(File.Exists);
        }

        void Unwrap(string source, string destination)
        {
            var bytes = File.ReadAllBytes(source);
            File.WriteAllBytes(destination, Self.RelroToLoad(Self.ToElf(bytes, Path.GetFileName(source))));
            Log("Prepared " + Path.GetFileName(destination) + " from " + source);
        }

        int Run(string exe, string arguments, string directory)
        {
            Log("> " + Path.GetFileName(exe) + " " + arguments);
            var info = new ProcessStartInfo(exe, arguments)
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = new Process { StartInfo = info })
            {
                var lines = new List<string>();
                DataReceivedEventHandler collect = delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (lines) lines.Add(e.Data);
                    if (e.Data.StartsWith("FAIL") || e.Data.StartsWith("error") || e.Data.Contains("Exception")) Log(e.Data);
                };
                process.OutputDataReceived += collect;
                process.ErrorDataReceived += collect;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                lock (lines) File.WriteAllLines(Path.Combine(directory, "convert.log"), lines);
                Log("Converter finished with exit code " + process.ExitCode + " (full output in convert\\convert.log).");
                return process.ExitCode;
            }
        }

        void LinkGameFiles(string baseDir, string updateDir)
        {
            string app0 = Path.Combine(root, "app0");
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in new[] { baseDir, updateDir })
            {
                if (source == null) continue;
                foreach (var file in Directory.GetFiles(source))
                {
                    string name = Path.GetFileName(file);
                    if (!name.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase)) files[name] = file;
                }
                foreach (var dir in Directory.GetDirectories(source))
                {
                    string name = Path.GetFileName(dir);
                    if (!name.Equals("sce_module", StringComparison.OrdinalIgnoreCase)) dirs[name] = dir;
                }
            }
            // GT Sport only uses its data archives when gt.idx is there; without it the game closes
            // right after its splash screen.
            if (!files.ContainsKey("gt.idx"))
                throw new Exception("Neither game folder has gt.idx. Pick the GT Sport folder (CUSA03220) and its update, " +
                                    "the folders with gt.idx and the gt*.vol files.");
            int linked = 0, hard = 0, symbolic = 0;
            var unixLinks = new Dictionary<string, string>(); // under Wine: link -> target
            foreach (var pair in files)
            {
                string target = Path.Combine(app0, pair.Key);
                if (File.Exists(target)) File.Delete(target);
                if (Native.CreateHardLink(target, pair.Value, IntPtr.Zero)) { linked++; hard++; continue; }
                if (Wine.Detected) { unixLinks[target] = pair.Value; continue; }
                if (Native.CreateSymbolicLink(target, pair.Value, 2)) { linked++; symbolic++; continue; } // needs Developer Mode
                throw new Exception("Could not link " + pair.Value + ". Put the RyGTS folder on the same drive as the game, " +
                                    "or turn on Developer Mode in Windows settings.");
            }
            if (Wine.Detected)
            {
                foreach (var pair in dirs) unixLinks[Path.Combine(app0, pair.Key)] = pair.Value;
                Wine.Link(unixLinks);
                foreach (var pair in unixLinks)
                {
                    if (dirs.ContainsKey(Path.GetFileName(pair.Key)) ? !Directory.Exists(pair.Key) : !File.Exists(pair.Key))
                        throw new Exception("Could not link " + pair.Value + " into app0.");
                    if (!dirs.ContainsKey(Path.GetFileName(pair.Key))) linked++;
                }
                Log("Linked " + linked + " game files and " + dirs.Count + " folders into app0.");
                return;
            }
            foreach (var pair in dirs)
            {
                string target = Path.Combine(app0, pair.Key);
                if (Directory.Exists(target)) Directory.Delete(target, false);
                var info = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + target + "\" \"" + pair.Value + "\"")
                { UseShellExecute = false, CreateNoWindow = true };
                using (var process = Process.Start(info)) process.WaitForExit();
                if (!Directory.Exists(target)) throw new Exception("Could not link the folder " + pair.Value + ".");
            }
            foreach (var pair in files)
            {
                string problem = ReadProblem(Path.Combine(app0, pair.Key), new FileInfo(pair.Value).Length);
                if (problem != null)
                    throw new Exception("app0\\" + pair.Key + " " + problem + " after linking it to " + pair.Value +
                                        ". Put the RyGTS folder on the same drive as the game and press Set up again.");
            }
            Log("Linked " + linked + " game files (" + hard + " hard links, " + symbolic + " symbolic links) and " +
                dirs.Count + " folders into app0.");
        }

        /// Why a linked game file can't be used (missing, unreadable or a different size), or null.
        /// Opening it follows a symbolic link, unlike FileInfo.
        static string ReadProblem(string path, long expectedLength)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (expectedLength >= 0 && stream.Length != expectedLength)
                        return "has " + stream.Length + " bytes instead of " + expectedLength;
                }
                return null;
            }
            catch (FileNotFoundException) { return "is missing"; }
            catch (Exception e) { return "can't be read (" + e.Message.TrimEnd('.') + ")"; }
        }

        static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }

        // Everything RyGTS makes stays in the RyGTS folder (portable), and the shaders are in plain sight
        // in <RyGT>\shaders (the renderer's cache folder), never in AppData. Older folders kept them in
        // user\cache: a real folder there is moved to shaders once. Builds from 2026-10-18 to 10-20
        // made user\cache a link to one AppData folder shared by every RyGT folder; its shaders are
        // copied instead (the AppData folder stays for other RyGT folders still linked to it).
        static string OldSharedShaderCache
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "RyGT", "shader-cache");
            }
        }

        string ShaderFolder { get { return Path.Combine(root, "shaders"); } }

        bool OldShaderCacheExists()
        {
            return Directory.Exists(Path.Combine(root, "user", "cache"));
        }

        void UsePortableShaderCache()
        {
            string old = Path.Combine(root, "user", "cache");
            try
            {
                var info = new DirectoryInfo(old);
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // File by file, skipping what an earlier run (perhaps closed halfway) already
                    // copied, so a second start picks up where the first stopped. A link to a
                    // shared folder that is gone is just removed.
                    if (Directory.Exists(old))
                    {
                        Log("Copying the shaders from the shared AppData folder into the shaders folder (once)...");
                        int copied = 0;
                        CopyShaders(old, ShaderFolder, ref copied);
                        Log("Copied " + copied + " shader files.");
                    }
                    Directory.Delete(old, false); // the link only, not the shared folder
                    Log("Shaders are kept in the shaders folder now. " + OldSharedShaderCache +
                        " can be deleted once no other RyGT folder uses it.");
                }
                else if (info.Exists)
                {
                    Log("Moving the shaders from user\\cache to the shaders folder (once)...");
                    if (!Directory.Exists(ShaderFolder)) Directory.Move(old, ShaderFolder);
                    else { MergeShaderCache(old, ShaderFolder, true); Directory.Delete(old, true); }
                }
                Directory.CreateDirectory(ShaderFolder);
            }
            catch (Exception e)
            {
                Log("Note: shader folder: " + e.Message);
                try { Directory.CreateDirectory(ShaderFolder); } catch { }
            }
        }

        // Copies a shader folder's files that the target doesn't have yet, with progress in the status
        // line. The driver's pipeline cache file (several GB, unused since FIX-165) stays behind.
        void CopyShaders(string source, string target, ref int copied)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                    name.IndexOf(".vkpipelinecache", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                string destination = Path.Combine(target, name);
                if (File.Exists(destination)) continue;
                File.Copy(file, destination + ".part", true);
                File.Move(destination + ".part", destination);
                if (++copied % 2000 == 0) SetStatus("Copying your shaders into the shaders folder (once): " + copied + " files...");
            }
            foreach (var dir in Directory.GetDirectories(source))
                CopyShaders(dir, Path.Combine(target, Path.GetFileName(dir)), ref copied);
        }

        static void MergeShaderCache(string source, string target, bool move = true)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                string destination = Path.Combine(target, name);
                if (!File.Exists(destination))
                {
                    if (move) File.Move(file, destination); else File.Copy(file, destination);
                }
                else if (name.EndsWith(".vkpipelinecache", StringComparison.OrdinalIgnoreCase) &&
                         new FileInfo(file).Length > new FileInfo(destination).Length)
                    File.Copy(file, destination, true);
            }
            foreach (var dir in Directory.GetDirectories(source))
                MergeShaderCache(dir, Path.Combine(target, Path.GetFileName(dir)), move);
        }

        void Play()
        {
            if (!File.Exists(Exe)) { MessageBox.Show(this, "Press Set up first."); return; }
            try { ApplyNativeRendererDefault(); ApplyExperimentalRenderScale(); ResolveVulkanDevice(); }
            catch (Exception e) { MessageBox.Show(this, e.Message); return; }
            // Without its data index GT Sport closes right after the splash screen.
            string dataProblem = ReadProblem(Path.Combine(root, "app0", "gt.idx"), -1);
            if (dataProblem != null)
            {
                MessageBox.Show(this, "GT Sport's data index app0\\gt.idx " + dataProblem + ", so the game can't start.\n\n" +
                                "Press Set up again. If the RyGTS folder is on a different drive than the game, move it " +
                                "to the game's drive first.");
                return;
            }
            // The one-time steps below can take minutes (copying shaders, preparing them), so they run
            // beside the window, which keeps showing their progress.
            string gameDir = settings.Game;
            string updateDir = settings.Update.Length > 0 && Directory.Exists(settings.Update) ? settings.Update : null;
            busy = true;
            RefreshState();
            bool verified = false;
            var thread = new Thread(delegate ()
            {
                Exception failure = null;
                try
                {
                    SetStatus("Checking the game executable...");
                    GameBuildIntegrity.Ensure(Exe, delegate {
                        Log("GTSport.exe has changed or has no SHA-256 build record. Rebuilding from your game files...");
                        SetStatus("Rebuilding the game executable...");
                        RunSetup();
                    });
                    if (OldShaderCacheExists()) SetStatus("Moving your shaders to the shaders folder (once)...");
                    UsePortableShaderCache();
                    // Repair the old 16 GB profile even when Settings was never opened.
                    if (ReadIntSetting("GPU", "system_ram_gb", 0) == 16 &&
                        !ReadSetting("Vulkan", "pipeline_cache_enabled", true))
                        WriteSetting("Vulkan", "pipeline_cache_enabled", true);
                    RequireGameClosedForShaderPreparation();
                    // Folders set up by an earlier RyGTS.exe or adcpatch get the offline livery scripts
                    // (again) now (a few seconds).
                    if (GameScriptsOutdated() && Directory.Exists(gameDir))
                    {
                        SetStatus("Making the offline livery scripts from your game files...");
                        try { BuildGameScripts(gameDir, updateDir); }
                        catch (Exception e) { Log("Offline livery scripts: " + e.Message); }
                    }
                    // A newer shaders.pack (or a folder set up before it existed): prepared once now.
                    if (ShadersOutdated() && Directory.Exists(gameDir))
                    {
                        SetStatus("Preparing shaders from your game files (a few minutes, once)...");
                        try { PrepareShaders(gameDir, updateDir); }
                        catch (Exception e) { Log("Shaders: " + e.Message); }
                    }
                    verified = true;
                }
                catch (Exception e) { failure = e; Log("ERROR: " + e.Message); }
                BeginInvoke(new Action(delegate
                {
                    busy = false;
                    RefreshState();
                    if (!verified)
                    {
                        status.Text = "Game verification/rebuild failed; see the log. The game was not started.";
                        ShowSetupDumpHelp(failure);
                        return;
                    }
                    // PERF-210: Anti-Lag only without frame generation (FSR or DLSS).
                    bool antiLag = ReadSetting("GPU", "anti_lag", false) &&
                                   !ReadSetting("GPU", "framegen_enabled", false) &&
                                   !ReadSetting("GPU", "dlssg_enabled", false);
                    bool shaderObjects = ReadSetting("GPU", "shader_objects", false);
                    StartGame(root, settings.HddStreaming, experimentalSceneSize, antiLag, shaderObjects,
                              ReadSetting("GPU", "shader_pretranslate", false), ReadSetting("GPU", "compile_pack", false),
                              CustomEnvironment.Copy(settings.Env));
                    status.Text = "GT Sport is starting. Logs: logs\\stdout.log and logs\\stderr.log.";
                }));
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// Starts GTSport.exe with session log rotation and waits for GPU recovery if Vulkan loses
        /// its device.
        static void StartGame(string root, bool hddStreaming, Size? sceneSize, bool antiLag, bool shaderObjects, bool shaderPretranslate, bool compilePack, List<EnvVar> customEnv)
        {
            string exe = Path.Combine(root, "GTSport.exe");
            // Preserve GT Sport metadata while migrating the launcher publisher field from older installs.
            try { GameExe.ApplyVersionInfo(exe); } catch (Exception e) { AppendSetupLog(root, "Could not update game publisher metadata: " + e.Message); }
            // Folders set up by an earlier RyGT launcher get the icon and Windows subsystem now.
            try { if (!GameExe.IsPrepared(exe)) GameExe.Prepare(exe); } catch { }
            ThreadPool.QueueUserWorkItem(delegate { AmdSoftware.EnsureGameEntry(root, exe); });
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool recoveredOnce = false;
                while (true)
                {
                    string logDirectory;
                    try
                    {
                        logDirectory = LogFiles.PrepareRuntimeLogs(root);
                    }
                    catch (Exception e)
                    {
                        AppendSetupLog(root, "Could not prepare game logs: " + e.Message);
                        return;
                    }

                    Process started;
                    try
                    {
                        var start = new ProcessStartInfo(exe)
                        { WorkingDirectory = root, UseShellExecute = false };
                        RenderResolution.ConfigureStart(start, root, sceneSize);
                        start.EnvironmentVariables["RYGT_LOG_DIRECTORY"] = logDirectory;
                        start.EnvironmentVariables["RYGT_HDD_STREAMING"] = hddStreaming ? "1" : "0";
                        start.EnvironmentVariables["RYGT_ANTI_LAG"] = antiLag ? "1" : "0"; // PERF-210
                        start.EnvironmentVariables["RYGT_SHADER_OBJECT"] = shaderObjects ? "1" : "0";
                        // FEAT-GTS-445 from the Shaders card. Pre-translation: each shader translation leaves
                        // a record (shaders\<serial>.manifest) and its code (shaders\<serial>.capture, this
                        // PC's only); at start, the ones the shader store lacks (after a renderer update)
                        // are translated again in the background. The compile pack: the pipelines this PC's
                        // driver cache lacks (after a driver update) are compiled at start from the recipes
                        // kept. Only when checked, so a script that sets them itself keeps its setting (and
                        // the Advanced page, applied below, still wins).
                        if (shaderPretranslate)
                        {
                            start.EnvironmentVariables["RYGT_MANIFEST_RECORD"] = "1";
                            start.EnvironmentVariables["RYGT_MANIFEST_PRETRANSLATE"] = "1";
                        }
                        if (compilePack) start.EnvironmentVariables["RYGT_COMPILE_PACK"] = "1";
                        // FEAT-GTS-433: the user's list last, so it wins; only the count is logged.
                        int custom = CustomEnvironment.Apply(start, customEnv);
                        if (custom > 0) AppendSetupLog(root, custom + " custom environment variable(s) applied.");
                        started = Process.Start(start);
                    }
                    catch (Exception e)
                    {
                        AppendSetupLog(root, "Could not start GT Sport: " + e.Message);
                        return;
                    }
                    if (started == null) return;
                    using (Process game = started) game.WaitForExit();

                    if (recoveredOnce || !RendererReportedDeviceLoss(logDirectory)) return;
                    recoveredOnce = true;
                    AppendSetupLog(root, "Vulkan lost the GPU device. Waiting for Windows to restore the graphics device before restarting GT Sport.");
                    if (!WaitForGraphicsDevice(TimeSpan.FromMinutes(2)))
                    {
                        AppendSetupLog(root, "The graphics device did not recover within 2 minutes; GT Sport was not restarted.");
                        return;
                    }
                    AppendSetupLog(root, "The graphics device is responding again. Restarting GT Sport once; the interrupted session cannot be resumed.");
                }
            });
        }

        static bool RendererReportedDeviceLoss(string logDirectory)
        {
            string path = Path.Combine(logDirectory, "stdout.log");
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    int count = (int)Math.Min(stream.Length, 1024 * 1024);
                    stream.Seek(-count, SeekOrigin.End);
                    byte[] bytes = new byte[count];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = stream.Read(bytes, read, bytes.Length - read);
                        if (n == 0) break;
                        read += n;
                    }
                    string tail = Encoding.UTF8.GetString(bytes, 0, read);
                    return tail.IndexOf("ErrorDeviceLost", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           tail.IndexOf("Device lost", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        static bool WaitForGraphicsDevice(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                IntPtr device = IntPtr.Zero, context = IntPtr.Zero;
                try
                {
                    // A fresh hardware D3D11 device is a small probe that the Windows display
                    // driver has recovered. Driver type 1 is HARDWARE; SDK version is 7.
                    int featureLevel;
                    int result = Native.D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0,
                        IntPtr.Zero, 0, 7, out device, out featureLevel, out context);
                    if (result >= 0) return true;
                }
                catch { }
                finally
                {
                    if (context != IntPtr.Zero) Marshal.Release(context);
                    if (device != IntPtr.Zero) Marshal.Release(device);
                }
                Thread.Sleep(2000);
            }
            return false;
        }

        static void AppendSetupLog(string root, string line)
        {
            try { File.AppendAllText(Path.Combine(root, "setup.log"), line + Environment.NewLine); }
            catch { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            // Read-only snapshots and the executable preparation helper do not open the launcher.
            bool helper = args.Length > 0 &&
                (args[0].StartsWith("--snapshot", StringComparison.Ordinal) || args[0] == "--prepare");
            if (helper) return RunLauncher(args);

            // Hold the lock for the entire UI/setup lifetime. WaitOne is atomic across launches;
            // an abandoned lock from a crashed launcher can safely be acquired again.
            using (var instance = new Mutex(false, @"Local\RyGT.Launcher.SingleInstance"))
            {
                bool owns;
                try { owns = instance.WaitOne(0, false); }
                catch (AbandonedMutexException) { owns = true; }
                if (!owns)
                {
                    MessageBox.Show("RyGTS Launcher is already running.\n\n" +
                        "Please close the existing launcher, then open RyGTS Launcher again.",
                        "RyGTS Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 2;
                }
                try { return RunLauncher(args); }
                finally { instance.ReleaseMutex(); }
            }
        }

        static int RunLauncher(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Portable: the tools' work folders (and anything else asking for a temporary folder) go
            // into the RyGTS folder instead of AppData\Local\Temp; the tools delete theirs when done.
            try
            {
                string temp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
                Directory.CreateDirectory(temp);
                Environment.SetEnvironmentVariable("TMP", temp);
                Environment.SetEnvironmentVariable("TEMP", temp);
            }
            catch { }
            if (args.Contains("--setup")) return new LoaderForm().SetupWithoutWindow();
            if (args.Length == 2 && args[0] == "--prepare")
            {
                // Gives an already converted GTSport.exe the ryGT icon and the Windows subsystem.
                try { GameExe.Prepare(Path.GetFullPath(args[1])); return 0; }
                catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
            }
            if ((args.Length == 2 || args.Length == 3) && args[0] == "--snapshot")
            {
                // Renders the window's contents to a PNG (for checking the layout); a third argument
                // picks the view: home, settings or controls, or a settings page (game, display,
                // graphics, shaders, advanced, about).
                using (var form = new LoaderForm())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-4000, -4000);
                    form.ShowInTaskbar = false;
                    form.Show();
                    if (args.Length == 3)
                    {
                        int known = Array.IndexOf(new[] { "home", "settings", "controls" }, args[2]);
                        if (known >= 0) form.ShowPage(known);
                        else form.ShowSettings(char.ToUpperInvariant(args[2][0]) + args[2].Substring(1), false);
                    }
                    Application.DoEvents();
                    // The client area only: each part of the window drawn where it sits.
                    using (var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                    {
                        using (var g = Graphics.FromImage(image)) g.Clear(Dark.Back);
                        for (int i = form.Controls.Count - 1; i >= 0; --i)
                        {
                            var part = form.Controls[i];
                            if (part.Visible) part.DrawToBitmap(image, part.Bounds);
                        }
                        image.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                    }
                    form.Close();
                }
                return 0;
            }
            if (args.Length == 2 && args[0] == "--snapshot-previews")
            {
                // The controller preview's layouts side by side as a PNG, some controls pressed.
                var kinds = new[] { PadKind.DualShock4, PadKind.DualSense, PadKind.Xbox, PadKind.Generic };
                using (var image = new Bitmap(420 * kinds.Length, 290))
                {
                    for (int i = 0; i < kinds.Length; ++i)
                    {
                        using (var preview = new ControllerPreview
                        {
                            Kind = kinds[i], Connected = i != 3, Caption = kinds[i].ToString(), Size = new Size(420, 290),
                            Cross = true, R1 = true, PadLeft = true, LX = -0.8f, RY = 0.6f, L2 = 0.5f, R2 = 1f
                        })
                        using (var tile = new Bitmap(420, 290))
                        {
                            preview.DrawToBitmap(tile, new Rectangle(0, 0, 420, 290));
                            using (var g = Graphics.FromImage(image)) g.DrawImage(tile, 420 * i, 0);
                        }
                    }
                    image.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                }
                return 0;
            }
            if (args.Length == 2 && (args[0] == "--snapshot-ports" || args[0] == "--snapshot-bindings" || args[0] == "--snapshot-wheel" ||
                                     args[0] == "--snapshot-obsolete"))
            {
                // The Controller ports, Button bindings, Steering wheel or obsolete settings window as a PNG
                // (nothing is saved).
                string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                using (Form form = args[0] == "--snapshot-ports"
                           ? (Form)new PortsForm("", null, (ports, names) => { })
                           : args[0] == "--snapshot-wheel"
                               ? (Form)new WheelForm(root)
                               : args[0] == "--snapshot-obsolete"
                                   ? (Form)new ObsoleteSettingsDialog(ObsoleteSettings.Find(new Dictionary<string, object>
                                         { { "GPU", new Dictionary<string, object> { { "quality_preset", 2 } } } }))
                                   : new ControlsForm(root, 1, player => { }))
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-4000, -4000);
                    form.ShowInTaskbar = false;
                    form.Show();
                    Application.DoEvents();
                    // The wheel window shows live state from its probe; give it a moment to arrive.
                    var settle = DateTime.Now.AddMilliseconds(args[0] == "--snapshot-wheel" ? 2000 : 200);
                    while (DateTime.Now < settle) { Application.DoEvents(); Thread.Sleep(20); }
                    using (var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                    {
                        using (var g = Graphics.FromImage(image)) g.Clear(Dark.Back);
                        for (int i = form.Controls.Count - 1; i >= 0; --i)
                        {
                            var part = form.Controls[i];
                            if (part.Visible) part.DrawToBitmap(image, part.Bounds);
                        }
                        image.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                    }
                    form.Close();
                }
                return 0;
            }
            Application.Run(new LoaderForm());
            return 0;
        }
    }
}
