using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Skunkworks.System
{
    // Binding data and terminal conventions live above the Unity input substrate.
    internal sealed class TerminalKeys
    {
        internal sealed class Binding
        {
            public readonly string Action;
            public readonly string Text;
            public readonly bool Repeat;
            public Binding(string action, string text = "", bool repeat = false)
            { Action = action; Text = text; Repeat = repeat; }
        }

        private const string Defaults = "f1 = f1\nctrl+shift+c = copy\nctrl+shift+v = paste\nctrl+q = quit\nctrl+shift+r = reload\nctrl+shift+f5 = restart\nctrl+c = interrupt\nctrl+d = eof\nctrl+l = clear\nenter = enter\nkeypad_enter = enter\nescape = escape\ntab = tab\nshift+tab = backtab\nbackspace = backspace\nup = up\ndown = down\nleft = left\nright = right\nhome = home\nend = end\ninsert = insert\ndelete = delete\npage_up = page_up\npage_down = page_down\nf2 = f2\nf3 = f3\nf4 = f4\nf5 = f5\nf6 = f6\nf7 = f7\nf8 = f8\nf9 = f9\nf10 = f10\nf11 = f11\nf12 = f12";
        private readonly Dictionary<string, Binding> bindings =
            new Dictionary<string, Binding>(StringComparer.OrdinalIgnoreCase);

        public TerminalKeys(string path)
        {
            int number = 0;
            foreach (string raw in File.Exists(path) ? File.ReadAllLines(path) : Defaults.Split('\n'))
            {
                number++;
                string line = raw.Trim().TrimStart('\uFEFF');
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                int separator = line.IndexOf('=');
                if (separator <= 0) throw new FormatException(path + ":" + number + ": expected gesture = action");
                string gesture = line.Substring(0, separator).Trim().ToLowerInvariant();
                string action = line.Substring(separator + 1).Trim().ToLowerInvariant();
                if (bindings.ContainsKey(gesture)) throw new FormatException(path + ": duplicate gesture " + gesture);
                bindings.Add(gesture, Action(action));
            }
        }

        public Binding Resolve(InputSample sample)
        {
            if (sample.Kind != InputKind.KeyDown || IsAltGr(sample.Modifiers)) return null;
            InputModifiers mods = sample.Modifiers;
            if ((mods & InputModifiers.Meta) != 0) return null;
            string prefix = ((mods & InputModifiers.Control) != 0 ? "ctrl+" : "") +
                ((mods & InputModifiers.Alt) != 0 ? "alt+" : "");
            bool shift = (mods & InputModifiers.Shift) != 0;
            string gesture = prefix + (shift ? "shift+" : "") + sample.Control;
            // Migrate old saved F1 toggle/blur bindings without stealing F1 from applications.
            if (sample.Control == "f1") return new Binding("f1", "\u001bOP");
            if (gesture == "ctrl+q") return new Binding("quit");
            if (bindings.TryGetValue(gesture, out Binding exact)) return exact;
            if (gesture == "ctrl+shift+f5") return new Binding("restart");
            // Shift does not change e.g. Enter unless explicitly mapped.
            if (shift && bindings.TryGetValue(prefix + sample.Control, out Binding basic)) return basic;

            if ((mods & InputModifiers.Control) != 0 && (mods & InputModifiers.Alt) == 0)
            {
                string letter = sample.DisplayName.Length == 1 ? sample.DisplayName.ToLowerInvariant() : sample.Control;
                if (letter.Length == 1 && letter[0] >= 'a' && letter[0] <= 'z')
                    return new Binding("control", ((char)(letter[0] - 'a' + 1)).ToString());
                if (sample.Control == "space") return new Binding("control", "\0");
                if (sample.Control == "left_bracket") return new Binding("control", "\u001b");
                if (sample.Control == "backslash") return new Binding("control", "\u001c");
                if (sample.Control == "right_bracket") return new Binding("control", "\u001d");
            }
            return null; // printable text comes only from the Text channel
        }

        public static string CommittedText(InputSample sample)
        {
            if (sample.Kind != InputKind.Text || (sample.Modifiers & InputModifiers.Meta) != 0) return "";
            bool altGr = IsAltGr(sample.Modifiers);
            if (!altGr && (sample.Modifiers & InputModifiers.Control) != 0) return "";
            var result = new StringBuilder();
            // Control characters are already handled as keys. Keep surrogate pairs intact.
            foreach (char c in sample.Text) if (!char.IsControl(c)) result.Append(c);
            if (result.Length == 0) return "";
            if (!altGr && (sample.Modifiers & InputModifiers.Alt) != 0) result.Insert(0, '\u001b');
            return result.ToString();
        }

        public static bool IsAltGr(InputModifiers mods)
        {
            return (mods & InputModifiers.RightAlt) != 0 && (mods & InputModifiers.Control) != 0;
        }

        private static Binding Action(string name)
        {
            switch (name)
            {
                case "toggle": case "blur": case "copy": case "paste": case "reload": case "restart": case "quit": return new Binding(name);
                case "enter": return new Binding(name, "\r");
                case "interrupt": return new Binding(name, "\u0003");
                case "eof": return new Binding(name, "\u0004");
                case "clear": return new Binding(name, "\u000c");
                case "escape": return new Binding(name, "\u001b");
                case "tab": return new Binding(name, "\t");
                case "backtab": return new Binding(name, "\u001b[Z");
                case "backspace": return new Binding(name, "\u007f", true);
                case "delete": return new Binding(name, "\u001b[3~", true);
                case "insert": return new Binding(name, "\u001b[2~");
                case "up": return new Binding(name, "\u001b[A", true);
                case "down": return new Binding(name, "\u001b[B", true);
                case "right": return new Binding(name, "\u001b[C", true);
                case "left": return new Binding(name, "\u001b[D", true);
                case "home": return new Binding(name, "\u001b[H");
                case "end": return new Binding(name, "\u001b[F");
                case "page_up": return new Binding(name, "\u001b[5~", true);
                case "page_down": return new Binding(name, "\u001b[6~", true);
                case "f1": return new Binding(name, "\u001bOP");
                case "f2": return new Binding(name, "\u001bOQ");
                case "f3": return new Binding(name, "\u001bOR");
                case "f4": return new Binding(name, "\u001bOS");
                case "f5": return new Binding(name, "\u001b[15~");
                case "f6": return new Binding(name, "\u001b[17~");
                case "f7": return new Binding(name, "\u001b[18~");
                case "f8": return new Binding(name, "\u001b[19~");
                case "f9": return new Binding(name, "\u001b[20~");
                case "f10": return new Binding(name, "\u001b[21~");
                case "f11": return new Binding(name, "\u001b[23~");
                case "f12": return new Binding(name, "\u001b[24~");
                default: throw new FormatException("Unknown terminal action: " + name);
            }
        }
    }
}
