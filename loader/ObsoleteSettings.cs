// FEAT-GTS-469: entries in the game's config (user\custom_configs\CUSA03220.json) that neither the
// launcher nor the renderer reads any more. When the launcher opens it offers to remove them.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace RyGT
{
    static class ObsoleteSettings
    {
        /// Section, key, and what replaced it. Only list a key once nothing reads it: the config is
        /// shared with shadGT (src/core/emulator_settings.h) and the renderer, so a key the launcher
        /// stopped showing (GPU.compile_freeze, say) can still be in use there.
        static readonly string[][] Entries =
        {
            new[] { "GPU", "quality_preset", "replaced by System memory (GPU.system_ram_gb)" },
        };

        /// The obsolete entries present in `config`, as "Section.key - what replaced it".
        public static List<string> Find(Dictionary<string, object> config)
        {
            var found = new List<string>();
            foreach (var entry in Entries)
            {
                var section = Section(config, entry[0]);
                if (section != null && section.ContainsKey(entry[1])) found.Add(entry[0] + "." + entry[1] + " - " + entry[2]);
            }
            return found;
        }

        /// Removes the obsolete entries from `config`; the number removed. Sections left empty go too.
        public static int Remove(Dictionary<string, object> config)
        {
            int removed = 0;
            foreach (var entry in Entries)
            {
                var section = Section(config, entry[0]);
                if (section == null || !section.Remove(entry[1])) continue;
                removed++;
                if (section.Count == 0) config.Remove(entry[0]);
            }
            return removed;
        }

        static Dictionary<string, object> Section(Dictionary<string, object> config, string name)
        {
            object found;
            return config.TryGetValue(name, out found) ? found as Dictionary<string, object> : null;
        }
    }

    /// "Remove obsolete settings?": Yes removes them, No keeps them; "Don't show again" stops the question.
    /// FEAT-GTS-275: with `renames`, the list also holds switches saved under an old name, and Yes renames them.
    class ObsoleteSettingsDialog : Form
    {
        readonly DarkCheckBox dontShow;

        /// At most 15 lines, then how many more.
        static string[] Shorten(IList<string> entries)
        {
            var lines = new List<string>(entries);
            if (lines.Count <= 15) return lines.ToArray();
            int more = lines.Count - 14;
            lines = lines.GetRange(0, 14);
            lines.Add("... and " + more + " more");
            return lines.ToArray();
        }

        public bool DontShowAgain { get { return dontShow.Checked; } }

        public ObsoleteSettingsDialog(IList<string> entries, bool renames = false)
        {
            Text = renames ? "Update obsolete settings?" : "Remove obsolete settings?";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(16, 16, 16, 12);
            Font = new Font("Segoe UI", 9f);

            var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, BackColor = Color.Transparent,
                                                Location = new Point(Padding.Left, Padding.Top) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var icon = new PictureBox { Image = SystemIcons.Question.ToBitmap(), SizeMode = PictureBoxSizeMode.AutoSize,
                                        Margin = new Padding(0, 2, 14, 0), BackColor = Color.Transparent };
            layout.Controls.Add(icon, 0, 0);
            layout.SetRowSpan(icon, 3);

            var message = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(440, 0),
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, 10),
                Text = (renames ? "Your settings contain one or more obsolete entries:\n\n    "
                                : "Your config file contains one or more obsolete entries:\n\n    ") +
                       string.Join("\n    ", Shorten(entries)) +
                       "\n\nNothing in this version of RyGTS reads them" + (renames ? " under these names" : "") +
                       ". Consider that an older RyGTS may still use them." +
                       (renames ? "\n\nThe switches are now named after where they take effect: SHADGT_ for the renderer, " +
                                  "RYGT_ for the runtime, SHARED_ for both." : "") +
                       "\n\nDo you wish to let the launcher " + (renames ? "update" : "remove") + " them for you now? " +
                       "The change is saved right away."
            };
            layout.Controls.Add(message, 1, 0);

            dontShow = new DarkCheckBox { Text = "Don't show again", AutoSize = true, Margin = new Padding(0, 0, 0, 14) };
            layout.Controls.Add(dontShow, 1, 1);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Right, BackColor = Color.Transparent,
                                                FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0) };
            var no = new Button { Text = "No", DialogResult = DialogResult.No, Size = new Size(88, 28), Margin = new Padding(8, 0, 0, 0) };
            var yes = new Button { Text = "Yes", DialogResult = DialogResult.Yes, Size = new Size(88, 28), Margin = new Padding(0) };
            buttons.Controls.Add(no);
            buttons.Controls.Add(yes);
            layout.Controls.Add(buttons, 1, 2);

            Controls.Add(layout);
            AcceptButton = yes;
            CancelButton = no;
            Dark.Apply(this);
            HandleCreated += delegate { Dark.TitleBar(this); };
        }
    }
}
