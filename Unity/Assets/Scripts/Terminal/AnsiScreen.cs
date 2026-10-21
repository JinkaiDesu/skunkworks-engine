using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
namespace Skunkworks.System
{
    internal struct TerminalStyle : IEquatable<TerminalStyle>
    {
        public int Foreground, Background;
        public bool Bold, Italic, Underline, Inverse, Hidden;
        public static TerminalStyle Default => new TerminalStyle { Foreground = 0x83d197 };
        public int VisibleBackground => Inverse ? Foreground : Background;
        public int VisibleForeground => Hidden ? VisibleBackground : Inverse ? Background : Foreground;
        public bool Equals(TerminalStyle other) => Foreground == other.Foreground && Background == other.Background &&
            Bold == other.Bold && Italic == other.Italic && Underline == other.Underline && Inverse == other.Inverse && Hidden == other.Hidden;
    }
    internal struct TerminalCell
    {
        public string Text;
        public TerminalStyle Style;
        public bool Continuation;
        public static TerminalCell Blank(TerminalStyle style) => new TerminalCell { Text = " ", Style = style };
    }
    internal struct BackgroundRun
    {
        public int Row, Column, Length, Color;
    }
    // VT screen promoted below the hot-load boundary. No locks, native I/O or Unity APIs here.
    // Its owner holds a dedicated screen lock independently of input and process lifecycle.
    internal sealed class AnsiScreen
    {
        private TerminalCell[,] cells, primary;
        private int x, y, savedX, savedY, primaryX, primaryY, top, bottom;
        private TerminalStyle style = TerminalStyle.Default, savedStyle, primaryStyle;
        private bool wrap, autoWrap = true, origin, insert;
        private int state;
        private char highSurrogate;
        private bool g0Graphics, g1Graphics, useG1, savedG0Graphics, savedG1Graphics, savedUseG1;
        private int charsetTarget;
        private readonly StringBuilder csi = new StringBuilder();
        private readonly Queue<string> history = new Queue<string>(), replies = new Queue<string>();
        public int Columns { get; private set; }
        public int Rows { get; private set; }
        public int CursorX => x;
        public int CursorY => y;
        public bool CursorVisible { get; private set; } = true;
        public bool ApplicationCursor { get; private set; }
        public bool BracketedPaste { get; private set; }
        public bool Alternate => primary != null;
        public AnsiScreen(int columns, int rows) { Resize(columns, rows); }
        public static void ValidateSize(int columns, int rows)
        {
            if (columns < 1 || columns > 500 || rows < 1 || rows > 200) throw new ArgumentOutOfRangeException("Terminal size must be 1..500 by 1..200");
        }
        public void Resize(int columns, int rows)
        {
            ValidateSize(columns, rows); cells = ResizeBuffer(cells, columns, rows);
            if (primary != null) primary = ResizeBuffer(primary, columns, rows);
            Columns = columns; Rows = rows; top = 0; bottom = rows - 1;
            x = Clamp(x, 0, columns - 1); y = Clamp(y, 0, rows - 1);
            savedX = Clamp(savedX, 0, columns - 1); savedY = Clamp(savedY, 0, rows - 1);
            primaryX = Clamp(primaryX, 0, columns - 1); primaryY = Clamp(primaryY, 0, rows - 1); wrap = false;
        }
        private static TerminalCell[,] ResizeBuffer(TerminalCell[,] source, int columns, int rows)
        {
            var target = new TerminalCell[rows, columns];
            for (int row = 0; row < rows; row++) for (int col = 0; col < columns; col++)
                target[row, col] = source != null && row < source.GetLength(0) && col < source.GetLength(1) ? source[row, col] : TerminalCell.Blank(TerminalStyle.Default);
            for (int row = 0; row < rows; row++) if (CellWidth(target[row, columns - 1].Text) == 2) target[row, columns - 1] = TerminalCell.Blank(TerminalStyle.Default);
            return target;
        }
        public TerminalCell Cell(int col, int row) => cells[row, col];
        public string History() => string.Join("\n", history.ToArray());
        public string[] DrainReplies() { var result = replies.ToArray(); replies.Clear(); return result; }
        public void Feed(string text) { foreach (char c in text) Feed(c); }
        public void Feed(char[] text, int count) { for (int i = 0; i < count; i++) Feed(text[i]); }
        public void Recover(string message)
        {
            AlternateScreen(false); state = 0; csi.Clear(); highSurrogate = '\0'; replies.Clear();
            style = TerminalStyle.Default; CursorVisible = autoWrap = true; ApplicationCursor = BracketedPaste = origin = insert = wrap = false;
            g0Graphics = g1Graphics = useG1 = savedG0Graphics = savedG1Graphics = savedUseG1 = false;
            top = 0; bottom = Rows - 1;
            var clean = new StringBuilder(); foreach (char c in message ?? "") if (!char.IsControl(c) || c == '\n' || c == '\r') clean.Append(c);
            Feed("\r\n" + clean + "\r\n");
        }
        private void Feed(char value)
        {
            if (value == '\u0018' || value == '\u001a') { state = 0; csi.Clear(); return; }
            if (state == 3 || state == 5)
            { if (value == '\a' && state == 3) state = 0; else if (value == '\u001b') state = state == 3 ? 4 : 6; return; }
            if (state == 4 || state == 6) { state = value == '\\' ? 0 : state == 4 ? 3 : 5; return; }
            if (value == '\u001b') { FlushSurrogate(); state = 1; return; }
            if (state == 7)
            {
                if (charsetTarget == 0) g0Graphics = value == '0';
                else if (charsetTarget == 1) g1Graphics = value == '0';
                state = 0; return;
            }
            if (state == 1)
            {
                state = 0;
                switch (value)
                {
                    case '[': csi.Clear(); state = 2; break;
                    case ']': state = 3; break;
                    case 'P': case 'X': case '^': case '_': state = 5; break;
                    case '(': case ')': case '*': case '+': charsetTarget = value == '(' ? 0 : value == ')' ? 1 : 2; state = 7; break;
                    case '7': Save(); break;
                    case '8': Restore(); break;
                    case 'D': Index(); break;
                    case 'E': x = 0; Index(); break;
                    case 'M': if (y == top) Scroll(1, false); else y = Math.Max(0, y - 1); wrap = false; break;
                    case 'c': Reset(); break;
                }
                return;
            }
            if (state == 2)
            {
                if (value >= '@' && value <= '~') { Execute(value, csi.ToString()); csi.Clear(); state = 0; }
                else if (value >= ' ' && value <= '?' && csi.Length < 256) csi.Append(value);
                else { csi.Clear(); state = 0; }
                return;
            }
            switch (value)
            {
                case '\u000e': useG1 = true; return;
                case '\u000f': useG1 = false; return;
                case '\r': FlushSurrogate(); x = 0; wrap = false; return;
                case '\n': case '\v': case '\f': FlushSurrogate(); Index(); return;
                case '\b': FlushSurrogate(); x = Math.Max(0, x - 1); wrap = false; return;
                case '\t': FlushSurrogate(); x = Math.Min(Columns - 1, ((x / 8) + 1) * 8); wrap = false; return;
            }
            if (value < ' ' || value == '\u007f') return;
            if (char.IsHighSurrogate(value)) { FlushSurrogate(); highSurrogate = value; return; }
            if (char.IsLowSurrogate(value))
            {
                if (highSurrogate == 0) Put("\uFFFD", 1);
                else { string pair = new string(new[] { highSurrogate, value }); highSurrogate = '\0'; Put(pair, CellWidth(pair)); }
                return;
            }
            FlushSurrogate();
            // ncurses and other CLI tools can designate DEC line graphics instead
            // of emitting UTF-8 box-drawing characters. Store their Unicode meaning.
            if ((useG1 ? g1Graphics : g0Graphics) && value >= '_' && value <= '~')
                value = "\u00a0◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·"[value - '_'];
            UnicodeCategory category = char.GetUnicodeCategory(value);
            if (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.EnclosingMark)
            {
                int col = wrap ? x : x - 1;
                if (col >= 0) { if (cells[y, col].Continuation && col > 0) col--; cells[y, col].Text += value; }
                return;
            }
            Put(value.ToString(), CellWidth(value.ToString()));
        }
        private void FlushSurrogate() { if (highSurrogate != 0) { highSurrogate = '\0'; Put("\uFFFD", 1); } }
        private void Put(string value, int width)
        {
            if (wrap && autoWrap) { x = 0; Index(); } wrap = false;
            if (width == 2 && Columns == 1) { value = "\uFFFD"; width = 1; }
            if (width == 2 && x == Columns - 1) { if (autoWrap) { x = 0; Index(); } else { value = "\uFFFD"; width = 1; } }
            if (insert) Shift(width, true);
            ClearGlyph(x); if (width == 2) ClearGlyph(x + 1);
            cells[y, x] = new TerminalCell { Text = value, Style = style };
            if (width == 2) cells[y, x + 1] = new TerminalCell { Text = "", Style = style, Continuation = true };
            if (x + width >= Columns) { x = Columns - 1; wrap = autoWrap; } else x += width;
        }
        private void ClearGlyph(int col)
        {
            if (cells[y, col].Continuation && col > 0) cells[y, col - 1] = TerminalCell.Blank(style);
            if (col + 1 < Columns && cells[y, col + 1].Continuation) cells[y, col + 1] = TerminalCell.Blank(style);
            cells[y, col] = TerminalCell.Blank(style);
        }
        internal static int CellWidth(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int c = char.IsHighSurrogate(text[0]) && text.Length > 1 && char.IsLowSurrogate(text[1]) ? char.ConvertToUtf32(text[0], text[1]) : text[0];
            return c >= 0x1100 && (c <= 0x115f || c == 0x2329 || c == 0x232a || (c >= 0x2e80 && c <= 0xa4cf && c != 0x303f) ||
                (c >= 0xac00 && c <= 0xd7a3) || (c >= 0xf900 && c <= 0xfaff) || (c >= 0xfe10 && c <= 0xfe6f) ||
                (c >= 0xff01 && c <= 0xff60) || (c >= 0xffe0 && c <= 0xffe6) || (c >= 0x1f300 && c <= 0x1faff) || (c >= 0x20000 && c <= 0x3ffff)) ? 2 : 1;
        }
        private void Index() { wrap = false; if (y == bottom) Scroll(1, true); else y = Math.Min(Rows - 1, y + 1); }
        private void Scroll(int count, bool up)
        {
            count = Clamp(count, 1, bottom - top + 1);
            for (int n = 0; n < count; n++)
            {
                if (up)
                {
                    if (!Alternate && top == 0 && bottom == Rows - 1) { history.Enqueue(RenderRow(0)); if (history.Count > 1000) history.Dequeue(); }
                    for (int row = top; row < bottom; row++) CopyRow(row + 1, row); ClearRow(bottom);
                }
                else { for (int row = bottom; row > top; row--) CopyRow(row - 1, row); ClearRow(top); }
            }
        }
        private void CopyRow(int from, int to) { for (int col = 0; col < Columns; col++) cells[to, col] = cells[from, col]; }
        private void ClearRow(int row) { for (int col = 0; col < Columns; col++) cells[row, col] = TerminalCell.Blank(style); }
        private void Save() { savedX = x; savedY = y; savedStyle = style; savedG0Graphics = g0Graphics; savedG1Graphics = g1Graphics; savedUseG1 = useG1; }
        private void Restore() { x = Clamp(savedX, 0, Columns - 1); y = Clamp(savedY, 0, Rows - 1); style = savedStyle; g0Graphics = savedG0Graphics; g1Graphics = savedG1Graphics; useG1 = savedUseG1; wrap = false; }
        private void Reset()
        {
            AlternateScreen(false); style = savedStyle = TerminalStyle.Default; x = y = savedX = savedY = 0;
            top = 0; bottom = Rows - 1; CursorVisible = autoWrap = true; origin = insert = ApplicationCursor = BracketedPaste = wrap = false;
            g0Graphics = g1Graphics = useG1 = savedG0Graphics = savedG1Graphics = savedUseG1 = false;
            for (int row = 0; row < Rows; row++) ClearRow(row);
        }
        private void AlternateScreen(bool enabled)
        {
            if (enabled && !Alternate)
            { primary = cells; primaryX = x; primaryY = y; primaryStyle = style; cells = ResizeBuffer(null, Columns, Rows); x = y = 0; top = 0; bottom = Rows - 1; wrap = false; }
            if (!enabled && Alternate)
            { cells = primary; primary = null; x = primaryX; y = primaryY; style = primaryStyle; top = 0; bottom = Rows - 1; wrap = false; }
        }
        private void Execute(char command, string sequence)
        {
            bool dec = sequence.StartsWith("?", StringComparison.Ordinal);
            int[] p = Parameters(sequence.TrimStart('?', '>', '!')); int n = Clamp(Param(p, 0, 1), 1, Math.Max(Columns, Rows));
            int low = origin ? top : 0, high = origin ? bottom : Rows - 1;
            switch (command)
            {
                case 'A': y = Clamp(y - n, low, high); break;
                case 'B': case 'e': y = Clamp(y + n, low, high); break;
                case 'C': case 'a': x = Clamp(x + n, 0, Columns - 1); break;
                case 'D': x = Clamp(x - n, 0, Columns - 1); break;
                case 'E': y = Clamp(y + n, low, high); x = 0; break;
                case 'F': y = Clamp(y - n, low, high); x = 0; break;
                case 'G': case '`': x = Clamp(Param(p, 0, 1) - 1, 0, Columns - 1); break;
                case 'd': y = Clamp(low + Param(p, 0, 1) - 1, low, high); break;
                case 'H': case 'f': y = Clamp(low + Param(p, 0, 1) - 1, low, high); x = Clamp(Param(p, 1, 1) - 1, 0, Columns - 1); break;
                case 'J': EraseDisplay(Param(p, 0, 0)); break;
                case 'K': EraseLine(Param(p, 0, 0)); break;
                case 's': Save(); break;
                case 'u': Restore(); break;
                case 'm': Sgr(p); return;
                case 'r':
                    int a = Clamp(Param(p, 0, 1) - 1, 0, Rows - 1), b = Clamp(Param(p, 1, Rows) - 1, 0, Rows - 1);
                    if (!dec && a < b) { top = a; bottom = b; x = 0; y = low; } break;
                case 'S': Scroll(n, true); break;
                case 'T': Scroll(n, false); break;
                case 'L': case 'M': if (y >= top && y <= bottom) { int old = top; top = y; Scroll(n, command == 'M'); top = old; } break;
                case '@': Shift(n, true); break;
                case 'P': Shift(n, false); break;
                case 'X': for (int col = x; col < Math.Min(Columns, x + n); col++) ClearGlyph(col); break;
                case 'h': case 'l':
                    foreach (int mode in p)
                    {
                        bool on = command == 'h'; if (!dec) { if (mode == 4) insert = on; continue; }
                        switch (mode)
                        {
                            case 1: ApplicationCursor = on; break;
                            case 6: origin = on; x = 0; y = origin ? top : 0; break;
                            case 7: autoWrap = on; break;
                            case 25: CursorVisible = on; break;
                            case 47: case 1047: case 1049: AlternateScreen(on); break;
                            case 1048: if (on) Save(); else Restore(); break;
                            case 2004: BracketedPaste = on; break;
                        }
                    }
                    break;
                case 'n':
                    if (!dec && Param(p, 0, 0) == 5) Reply("\u001b[0n");
                    if (!dec && Param(p, 0, 0) == 6) Reply("\u001b[" + (y + 1) + ";" + (x + 1) + "R"); return;
                case 'c': if (sequence.Length == 0 || sequence == "0") Reply("\u001b[?1;2c"); return;
                default: return;
            }
            wrap = false;
        }
        private void Reply(string value) { if (replies.Count < 64) replies.Enqueue(value); }
        private void Shift(int count, bool right)
        {
            count = Math.Min(count, Columns - x);
            if (right) { for (int col = Columns - 1; col >= x + count; col--) cells[y, col] = cells[y, col - count]; for (int col = x; col < x + count; col++) cells[y, col] = TerminalCell.Blank(style); }
            else { for (int col = x; col < Columns - count; col++) cells[y, col] = cells[y, col + count]; for (int col = Columns - count; col < Columns; col++) cells[y, col] = TerminalCell.Blank(style); }
        }
        private void EraseLine(int mode)
        { if (mode < 0 || mode > 2) return; for (int col = mode == 0 ? x : 0; col <= (mode == 1 ? x : Columns - 1); col++) ClearGlyph(col); }
        private void EraseDisplay(int mode)
        {
            if (mode == 3) { history.Clear(); return; }
            if (mode == 2) { for (int row = 0; row < Rows; row++) ClearRow(row); return; }
            if (mode == 0) { EraseLine(0); for (int row = y + 1; row < Rows; row++) ClearRow(row); }
            if (mode == 1) { EraseLine(1); for (int row = 0; row < y; row++) ClearRow(row); }
        }
        private void Sgr(int[] p)
        {
            if (p.Length == 0) p = new[] { 0 };
            for (int i = 0; i < p.Length; i++)
            {
                int code = p[i];
                switch (code)
                {
                    case 0: style = TerminalStyle.Default; break;
                    case 1: style.Bold = true; break;
                    case 3: style.Italic = true; break;
                    case 4: style.Underline = true; break;
                    case 7: style.Inverse = true; break;
                    case 8: style.Hidden = true; break;
                    case 22: style.Bold = false; break;
                    case 23: style.Italic = false; break;
                    case 24: style.Underline = false; break;
                    case 27: style.Inverse = false; break;
                    case 28: style.Hidden = false; break;
                    case 39: style.Foreground = TerminalStyle.Default.Foreground; break;
                    case 49: style.Background = TerminalStyle.Default.Background; break;
                    case 38: case 48:
                        int color = -1;
                        if (i + 2 < p.Length && p[i + 1] == 5) { color = Palette(Clamp(p[i + 2], 0, 255)); i += 2; }
                        else if (i + 4 < p.Length && p[i + 1] == 2) { color = (Clamp(p[i + 2], 0, 255) << 16) | (Clamp(p[i + 3], 0, 255) << 8) | Clamp(p[i + 4], 0, 255); i += 4; }
                        if (color >= 0) { if (code == 38) style.Foreground = color; else style.Background = color; } break;
                    default:
                        if (code >= 30 && code <= 37) style.Foreground = Palette(code - 30);
                        if (code >= 40 && code <= 47) style.Background = Palette(code - 40);
                        if (code >= 90 && code <= 97) style.Foreground = Palette(code - 90 + 8);
                        if (code >= 100 && code <= 107) style.Background = Palette(code - 100 + 8); break;
                }
            }
        }
        private static readonly int[] BasicColors = { 0x000000, 0x800000, 0x008000, 0x808000, 0x000080, 0x800080, 0x008080, 0xc0c0c0, 0x808080, 0xff0000, 0x00ff00, 0xffff00, 0x0000ff, 0xff00ff, 0x00ffff, 0xffffff };
        internal static int Palette(int i)
        {
            if (i < 16) return BasicColors[i]; if (i >= 232) return (8 + (i - 232) * 10) * 0x010101;
            int v = i - 16, r = v / 36, g = (v / 6) % 6, b = v % 6;
            return ((r == 0 ? 0 : 55 + r * 40) << 16) | ((g == 0 ? 0 : 55 + g * 40) << 8) | (b == 0 ? 0 : 55 + b * 40);
        }
        private static int[] Parameters(string value)
        { if (value.Length == 0) return new int[0]; var parts = value.Split(';'); var p = new int[parts.Length]; for (int i = 0; i < p.Length; i++) int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out p[i]); return p; }
        private static int Param(int[] p, int i, int fallback) => i >= p.Length || p[i] == 0 ? fallback : p[i];
        private static int Clamp(int v, int a, int b) => Math.Max(a, Math.Min(b, v));
        public string Render() { var b = new StringBuilder(); for (int row = 0; row < Rows; row++) { if (row != 0) b.Append('\n'); b.Append(RenderRow(row)); } return b.ToString().TrimEnd('\n'); }
        private string RenderRow(int row) { var b = new StringBuilder(); for (int col = 0; col < Columns; col++) if (!cells[row, col].Continuation) b.Append(cells[row, col].Text); return b.ToString().TrimEnd(' '); }
        public string RenderRichText()
        {
            var b = new StringBuilder();
            for (int row = 0; row < Rows; row++)
            {
                if (row != 0) b.Append('\n');
                int col = 0;
                while (col < Columns)
                {
                    TerminalStyle s = cells[row, col].Style; int end = col + 1;
                    while (end < Columns && s.Equals(cells[row, end].Style)) end++;
                    b.Append("<color=#").Append(s.VisibleForeground.ToString("x6", CultureInfo.InvariantCulture)).Append('>');
                    if (s.Bold) b.Append("<b>"); if (s.Italic) b.Append("<i>"); if (s.Underline) b.Append("<u>");
                    for (; col < end; col++) if (!cells[row, col].Continuation)
                        b.Append((cells[row, col].Text ?? " ").Replace("<", "<noparse><</noparse>"));
                    if (s.Underline) b.Append("</u>"); if (s.Italic) b.Append("</i>"); if (s.Bold) b.Append("</b>"); b.Append("</color>");
                }
            }
            return b.ToString();
        }
        public BackgroundRun[] Backgrounds()
        {
            var result = new List<BackgroundRun>();
            for (int row = 0; row < Rows; row++) for (int col = 0; col < Columns;)
            {
                int color = cells[row, col].Style.VisibleBackground, start = col++;
                while (col < Columns && cells[row, col].Style.VisibleBackground == color) col++;
                if (color != 0) result.Add(new BackgroundRun { Row = row, Column = start, Length = col - start, Color = color });
            }
            return result.ToArray();
        }
    }
}
