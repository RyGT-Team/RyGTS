// FEAT-GTS-437: the Advanced page. The experimental renderer and runtime switches (RYGT_* environment
// variables read by shadGT and RyGTS) as one table with a description on hover, and a table for any
// other variable. All of them are saved in RyGT.ini as env= lines and set for GTSport.exe when the
// launcher starts it (CustomEnvironment.Apply).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace RyGT
{
    /// One RYGT_* switch found in the renderer (libSceVideoOut.prx).
    class RendererSwitch
    {
        public string Name = "", Kind = "", Default = "", Values = "", Tooltip = "", Group = "";

        public bool IsFlag { get { return Kind == "flag"; } }
        public bool Diagnostic { get { return Group == SwitchWalker.DiagnosticsGroup; } }
        public bool Described { get { return Tooltip.Length > 0; } }
    }

    /// FEAT-GTS-437: finds the switches in the renderer file instead of a list in the launcher, so a
    /// new switch appears as soon as the renderer reads it. Reads the file (never loads it) and
    /// collects:
    /// - every string that is exactly RYGT_ and capitals, digits and _ (the names the renderer passes
    ///   to getenv), and
    /// - every "RYGTSW1|NAME|kind|group|default|values|description" string: the renderer's own
    ///   description of a switch (src/common/rygt_switches.cpp in shadGT).
    /// A switch without a description is still listed; a description without the name is not.
    static class SwitchWalker
    {
        public const string RendererFile = "libSceVideoOut.prx";
        public const string DiagnosticsGroup = "Diagnostics & tracing";
        public const string UndescribedGroup = "Not described in this renderer build";
        const string MetadataPrefix = "RYGTSW1|";

        /// Set by the launcher itself from its own settings; changing them here would be overwritten.
        static readonly HashSet<string> LauncherManaged = new HashSet<string>
        {
            "RYGT_LOG_DIRECTORY", "RYGT_HDD_STREAMING", "RYGT_ANTI_LAG", "RYGT_PATCH", "RYGT_EXPERIMENTAL_SCENE_VIEWPORT",
            "RYGT_SCENE_WIDTH", "RYGT_SCENE_HEIGHT", "RYGT_LOW_RES_COMPOSITION"
        };

        static readonly string[] GroupOrder =
        {
            "Shader objects & caches", "Shader compiling", "Native recorder & sync", "Memory & VRAM", "Tessellation",
            "Frame pacing & present", "Rendering workarounds", "AMD", "NVIDIA", "Runtime", DiagnosticsGroup, UndescribedGroup
        };

        public static List<RendererSwitch> Walk(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var described = new Dictionary<string, RendererSwitch>(StringComparer.Ordinal);
            for (int i = 0; i + 5 < data.Length; ++i)
            {
                // Strings may follow other data directly (the compiler packs them), so only a name
                // character right before rules a match out.
                if (data[i] != (byte)'R' || (i > 0 && IsNameByte(data[i - 1]))) continue;
                if (data[i + 1] != (byte)'Y' || data[i + 2] != (byte)'G' || data[i + 3] != (byte)'T') continue;
                int end = i;
                while (end < data.Length && data[end] != 0 && end - i < 4096) ++end;
                if (end >= data.Length || data[end] != 0) continue;
                string text = Encoding.UTF8.GetString(data, i, end - i);
                i = end;
                if (text.StartsWith(MetadataPrefix, StringComparison.Ordinal))
                {
                    var entry = Parse(text.Substring(MetadataPrefix.Length));
                    if (entry != null) described[entry.Name] = entry;
                }
                else if (text.Length > 5 && text.StartsWith("RYGT_", StringComparison.Ordinal) && CustomEnvironment.IsValidName(text) &&
                         text.ToUpperInvariant() == text)
                    names.Add(text);
            }

            var list = new List<RendererSwitch>();
            // Only names the renderer really reads: a description alone (e.g. for a switch that only a
            // diagnostics build reads) adds nothing.
            foreach (var name in names)
            {
                if (LauncherManaged.Contains(name)) continue;
                RendererSwitch entry;
                if (!described.TryGetValue(name, out entry)) entry = new RendererSwitch { Name = name, Group = UndescribedGroup };
                list.Add(entry);
            }
            Func<string, int> order = group => { int i = Array.IndexOf(GroupOrder, group); return i < 0 ? GroupOrder.Length - 2 : i; };
            return list.OrderBy(s => order(s.Group)).ThenBy(s => s.Group, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal).ToList();
        }

        static bool IsNameByte(byte b)
        {
            return b == '_' || (b >= '0' && b <= '9') || (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z');
        }

        /// NAME|kind|group|default|values|description (the description may contain |).
        static RendererSwitch Parse(string text)
        {
            var parts = text.Split(new[] { '|' }, 6);
            if (parts.Length < 6 || !CustomEnvironment.IsValidName(parts[0])) return null;
            return new RendererSwitch
            {
                Name = parts[0], Kind = parts[1].Trim().ToLowerInvariant(), Group = parts[2].Trim().Length > 0 ? parts[2].Trim() : "Other",
                Default = parts[3].Trim(), Values = parts[4].Trim(), Tooltip = parts[5].Trim()
            };
        }
    }

    static class AdvancedPage
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string lParam);

        const string DefaultText = "Default", OnText = "On (1)", OffText = "Off (0)";

        /// Builds the page into `page`. `env` is the saved list (RyGT.ini env= lines); `save` gets the
        /// new full list after every change.
        public static void Build(TableLayoutPanel page, Control title, Color cardFill, string root, List<EnvVar> env, Action<List<EnvVar>> save)
        {
            string rendererPath = Path.Combine(root, SwitchWalker.RendererFile);
            List<RendererSwitch> catalog;
            string walkError = null;
            try { catalog = SwitchWalker.Walk(rendererPath); }
            catch (Exception e) { catalog = new List<RendererSwitch>(); walkError = e.Message; }
            var known = new HashSet<string>(catalog.Select(s => s.Name));
            // name -> value for the switches in the table; everything else stays in the other table.
            var values = new Dictionary<string, string>();
            var others = new List<EnvVar>();
            foreach (var entry in env)
            {
                // An unticked row stays in the other table (unticked) rather than being lost.
                if (known.Contains(entry.Name) && entry.Enabled) values[entry.Name] = entry.Value;
                else others.Add(entry);
            }
            Action store = delegate
            {
                var list = new List<EnvVar>();
                foreach (var s in catalog)
                {
                    string value;
                    if (values.TryGetValue(s.Name, out value)) list.Add(new EnvVar { Name = s.Name, Value = value });
                }
                list.AddRange(others);
                save(list);
            };

            page.ColumnCount = 1;
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(title);

            // The search box, the filters and Reset all.
            var bar = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 8) };
            var searchField = new Card(Dark.Field) { AutoSize = false, Width = 260, Height = 30, Radius = 8, Edge = Color.Empty, Padding = new Padding(10, 7, 10, 6), Margin = new Padding(0, 0, 14, 0) };
            var search = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Margin = new Padding(0), AccessibleName = "Search switches" };
            searchField.Controls.Add(search);
            // The grey hint in the empty box (EM_SETCUEBANNER).
            search.HandleCreated += delegate { SendMessage(search.Handle, 0x1501, new IntPtr(1), "Search names and descriptions"); };
            var showDiagnostics = new DarkCheckBox { Text = "Show diagnostics and tracing", AutoSize = true, Margin = new Padding(0, 6, 14, 0) };
            var onlyChanged = new DarkCheckBox { Text = "Only changed", AutoSize = true, Margin = new Padding(0, 6, 14, 0) };
            var reset = new Button { Text = "Reset all", AutoSize = true, Margin = new Padding(0, 1, 0, 0) };
            var count = new Label { AutoSize = true, ForeColor = Dark.DimText, Tag = "heading", BackColor = Color.Transparent, Margin = new Padding(14, 7, 0, 0) };
            bar.Controls.Add(searchField);
            bar.Controls.Add(showDiagnostics);
            bar.Controls.Add(onlyChanged);
            bar.Controls.Add(reset);
            bar.Controls.Add(count);
            page.Controls.Add(bar);

            // The switches: a group heading row, then one row per switch.
            var card = new Card(cardFill) { Dock = DockStyle.Fill, Padding = new Padding(12), Margin = new Padding(0, 0, 0, 12) };
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var grid = CustomEnvironment.DarkGrid("Experimental switches");
            grid.Dock = DockStyle.Fill;
            grid.Margin = new Padding(0);
            grid.AllowUserToDeleteRows = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            grid.EditMode = DataGridViewEditMode.EditOnEnter;
            grid.ShowCellToolTips = true;
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Switch", ReadOnly = true, FillWeight = 40, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "setting", HeaderText = "Setting", FillWeight = 22, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "default", HeaderText = "When not set", ReadOnly = true, FillWeight = 38, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns[2].DefaultCellStyle.ForeColor = Dark.DimText;
            grid.Columns[0].DefaultCellStyle.Font = new Font("Consolas", 9f);

            var headingStyle = new DataGridViewCellStyle
            {
                BackColor = Dark.Button, SelectionBackColor = Dark.Button, ForeColor = Dark.Text, SelectionForeColor = Dark.Text,
                Font = new Font("Segoe UI Semibold", 9f)
            };
            var rowSwitch = new Dictionary<DataGridViewRow, RendererSwitch>();
            var headings = new List<KeyValuePair<DataGridViewRow, string>>();
            bool loading = true;
            string group = null;
            foreach (var s in catalog)
            {
                if (s.Group != group)
                {
                    group = s.Group;
                    int h = grid.Rows.Add(group, "", "");
                    var heading = grid.Rows[h];
                    heading.ReadOnly = true;
                    heading.DefaultCellStyle = headingStyle;
                    headings.Add(new KeyValuePair<DataGridViewRow, string>(heading, group));
                }
                int index = grid.Rows.Add(s.Name, "", s.Default);
                var row = grid.Rows[index];
                rowSwitch[row] = s;
                string current;
                values.TryGetValue(s.Name, out current);
                if (s.IsFlag)
                {
                    var combo = new DataGridViewComboBoxCell { FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing };
                    combo.Items.AddRange(DefaultText, OnText, OffText);
                    string shown = current == null ? DefaultText : current == "1" ? OnText : current == "0" ? OffText : current;
                    if (!combo.Items.Contains(shown)) combo.Items.Add(shown);
                    row.Cells[1] = combo;
                    combo.Value = shown;
                }
                else row.Cells[1].Value = current ?? "";
                string tip = Tip(s);
                foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = tip;
                Mark(row, current != null);
            }
            loading = false;
            card.Controls.Add(grid);
            page.Controls.Add(card);
            page.RowStyles.Clear();
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Action filter = delegate
            {
                string text = search.Text.Trim();
                int shown = 0;
                grid.SuspendLayout();
                var visibleGroups = new HashSet<string>();
                if (grid.CurrentCell != null && grid.CurrentCell.RowIndex >= 0) grid.CurrentCell = null;
                foreach (var pair in rowSwitch)
                {
                    var s = pair.Value;
                    bool changed = values.ContainsKey(s.Name);
                    bool visible = (showDiagnostics.Checked || !s.Diagnostic || changed) && (!onlyChanged.Checked || changed) &&
                                   (text.Length == 0 || s.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    s.Tooltip.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
                    pair.Key.Visible = visible;
                    if (visible) { ++shown; visibleGroups.Add(s.Group); }
                }
                foreach (var heading in headings) heading.Key.Visible = visibleGroups.Contains(heading.Value);
                grid.ResumeLayout();
                count.Text = walkError != null ? "Could not read " + SwitchWalker.RendererFile + ": " + walkError
                           : catalog.Count == 0 ? "No RYGT_ switches found in " + SwitchWalker.RendererFile
                           : shown + " of " + catalog.Count + " switches, " + values.Count + " changed";
            };

            Action<DataGridViewRow> apply = row =>
            {
                RendererSwitch s;
                if (loading || !rowSwitch.TryGetValue(row, out s)) return;
                string text = ((row.Cells[1].Value as string) ?? "").Trim();
                string value = s.IsFlag ? (text == DefaultText ? "" : text == OnText ? "1" : text == OffText ? "0" : text) : text;
                string error = value.Length == 0 || Valid(s, value) ? "" : "Not saved: " + KindHint(s) + ".";
                row.ErrorText = error;
                if (value.Length == 0 || error.Length > 0) values.Remove(s.Name);
                else values[s.Name] = value;
                Mark(row, values.ContainsKey(s.Name));
                store();
                int comma = count.Text.LastIndexOf(',');
                if (comma > 0) count.Text = count.Text.Substring(0, comma + 1) + " " + values.Count + " changed";
            };
            grid.CurrentCellDirtyStateChanged += delegate
            {
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewComboBoxCell) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.CellValueChanged += (sender, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 1) apply(grid.Rows[e.RowIndex]); };
            grid.DataError += (sender, e) => { e.ThrowException = false; };
            // Delete or Backspace on a setting returns it to the default.
            grid.KeyDown += (sender, e) =>
            {
                var cell = grid.CurrentCell;
                if (cell == null || cell.ColumnIndex != 1 || grid.IsCurrentCellInEditMode || (e.KeyCode != Keys.Delete && e.KeyCode != Keys.Back)) return;
                if (!rowSwitch.ContainsKey(grid.Rows[cell.RowIndex])) return;
                cell.Value = cell is DataGridViewComboBoxCell ? (object)DefaultText : "";
                e.Handled = true;
            };

            search.TextChanged += delegate { filter(); };
            showDiagnostics.CheckedChanged += delegate { filter(); };
            onlyChanged.CheckedChanged += delegate { filter(); };
            reset.Click += delegate
            {
                if (values.Count == 0) return;
                if (MessageBox.Show(page.FindForm(), "Return all " + values.Count + " changed switches to their defaults?", "Reset all",
                                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                loading = true;
                foreach (var pair in rowSwitch)
                {
                    pair.Key.Cells[1].Value = pair.Key.Cells[1] is DataGridViewComboBoxCell ? (object)DefaultText : "";
                    pair.Key.ErrorText = "";
                    Mark(pair.Key, false);
                }
                loading = false;
                values.Clear();
                store();
                filter();
            };
            filter();

            // Any other variable: names the table does not list.
            var otherCard = new Card(cardFill) { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 14), Margin = new Padding(0) };
            otherCard.Controls.Add(new Label { Text = "Other environment variables", AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), Margin = new Padding(0, 0, 0, 8) });
            Control otherEditor;
            otherCard.Controls.Add(otherEditor = CustomEnvironment.Editor(others, list =>
            {
                // A switch typed here belongs in the table above: kept here until the next start.
                others = list;
                store();
            }));
            page.Controls.Add(otherCard);
            Action cap = delegate
            {
                otherCard.MaximumSize = new Size(0, Math.Max(120, page.ClientSize.Height / 3));
                var refit = otherEditor.Tag as Action;
                if (refit != null) refit();
            };
            page.Resize += delegate { cap(); };
            cap();
        }

        static void Mark(DataGridViewRow row, bool changed)
        {
            row.Cells[1].Style.ForeColor = changed ? Dark.AccentText : Dark.Text;
            row.Cells[1].Style.SelectionForeColor = changed ? Dark.AccentText : Dark.Text;
            row.Cells[0].Style.ForeColor = changed ? Dark.AccentText : Dark.Text;
        }

        static string KindHint(RendererSwitch s)
        {
            switch (s.Kind)
            {
                case "int": return "a whole number";
                case "hex": return "a hexadecimal number such as 0xFFFF0000";
                case "float": return "a number such as 1.5";
                default: return "letters and digits";
            }
        }

        static bool Valid(RendererSwitch s, string value)
        {
            if (value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0) return false;
            long l; double d; ulong u;
            switch (s.Kind)
            {
                case "int": return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out l);
                case "hex":
                    string hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value.Substring(2) : value;
                    return hex.Length > 0 && ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out u) ||
                           long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out l);
                case "float": return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
                default: return true;
            }
        }

        /// The hover text: the description, then the values and the default, wrapped for the tooltip.
        static string Tip(RendererSwitch s)
        {
            if (!s.Described)
                return Wrap(s.Name + " is read by this renderer build (" + SwitchWalker.RendererFile + ") but has no description in it. " +
                            "Most switches take 1 to turn on and 0 to turn off.", 72);
            var text = new StringBuilder(Wrap(s.Tooltip, 72));
            if (s.Values.Length > 0) text.Append("\n\nValues: ").Append(Wrap(s.Values, 64));
            if (s.Default.Length > 0) text.Append("\nWhen not set: ").Append(Wrap(s.Default, 60));
            if (s.Diagnostic) text.Append("\n\nDiagnosis only: adds logging or dumps.");
            return text.ToString();
        }

        static string Wrap(string text, int width)
        {
            var lines = new StringBuilder();
            int column = 0;
            foreach (var word in (text ?? "").Split(' '))
            {
                if (column > 0 && column + 1 + word.Length > width) { lines.Append('\n'); column = 0; }
                else if (column > 0) { lines.Append(' '); ++column; }
                lines.Append(word);
                column += word.Length;
            }
            return lines.ToString();
        }
    }
}
