using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using OS.Devices;
using TMPro;
using TrixelCreative.TrixelAudio;
using TrixelCreative.TrixelAudio.Data;
using UI.Terminal.SimpleTerminal.Data;
using UI.Terminal.SimpleTerminal.Pty;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.UIElements;
using Utility;
using static UI.Terminal.SimpleTerminal.Data.EmulatorConstants;
using BinaryExpression = System.Linq.Expressions.BinaryExpression;
using Expression = System.Linq.Expressions.Expression;
using Image = UnityEngine.UI.Image;

namespace UI.Terminal.SimpleTerminal
{
    public class SimpleTerminalRenderer :
        MonoBehaviour,
        IUpdateSelectedHandler,
        IPointerDownHandler,
        IPointerUpHandler,
        IPointerMoveHandler,
        ISelectHandler,
        IDeselectHandler,
        IScrollHandler
    {
        private static readonly string[] vt100_0 = new string[62]
        {
            /* 0x41 - 0x7e */
            "↑", "↓", "→", "←", "█", "▚", "☃", /* A - G */
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, /* H - O */
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, /* P - W */
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
            " ", /* X - _ */
            "◆", "▒", "␉", "␌", "␍", "␊", "°", "±", /* ` - g */
            "␤", "␋", "┘", "┐", "┌", "└", "┼", "⎺", /* h - o */
            "⎻", "─", "⎼", "⎽", "├", "┤", "┴", "┬", /* p - w */
            "│", "≤", "≥", "π", "≠", "£", "·" /* x - ~ */
        };

        private RectTransform rectTransform;
        private Camera myCamera;
        private LayoutElement layoutElement;
        private RectTransform parentRectTransform;
        private int tickInterval;
        private TrixelAudioSource trixelAudio;
        private RectTransform parentRect;
        private RectTransform rect;
        private const int defaultbg = 258;
        private const int defaultfg = 259;
        private readonly Dictionary<int, string> colors = new Dictionary<int, string>();
        private readonly TCursor[] cursorMemory = new TCursor[2];
        private RenderedLine[] renderedLines = Array.Empty<RenderedLine>();
        private const int BUFSIZ = 8192;
        private readonly byte[] ttybuf = new byte[BUFSIZ];
        private int ttybuflen;
        private Term term = new Term();
        private Selection sel = new Selection();
        private STREscape strescseq;
        private CSIEscape csiescseq;
        private TerminalOptions ptyOptions = new TerminalOptions();
        private PseudoTerminal master;
        private PseudoTerminal slave;
        private float timeout;
        private bool drawing;
        private float lastBlink;
        private float trigger;
        private float now;
        private bool isFocused;
        private int oldRows;
        private int oldColumns;
        private RawImage bgImage;
        private float lastClick;
        private float earlierClick;
        private bool isSelecting;
        private const int HISTSIZE = 2000;
        private const int RESIZEBUFFER = 1000;
        private IVisualElementScheduledItem scheduledUpdate;
        private int rowCount = 43;
        private int columnCount = 132;
        private VisualElement panelRoot;
        private Func<SimpleTerminalRenderer, Vector2> RetrieveMeasuredTextResult;


        public int DefaultBackgroundId => defaultbg;

        private float ww;
        private float wh;

        [Header("Settings")]
        [SerializeField]
        private TMP_FontAsset font = null!;

        [SerializeField]
        private int fontSize = 12;
        
        [SerializeField]
        private bool allowAltScreen = true;

        [SerializeField]
        private string vtiden = "st";
        
        [SerializeField]
        private int tabSpaces = 8;

        [Header("Timing")]
        [SerializeField]
        private float minLatency;

        [SerializeField]
        private float doubleClickTime = 0;

        [SerializeField]
        private float trippleClickTime = 0;

        [SerializeField]
        private float maxLatency;

        [SerializeField]
        private float blinkTimeout;

        [SerializeField]
        private int mouseScrollLineCount = 3;

        [Header("Sound Effects")]
        [SerializeField]
        private SoundEffectAsset asciiBeep = null!;
        
        public int DefaultRowCount => this.rowCount;
        public int DefaultColumnCount => this.columnCount;
        public float CharacterWidth => this.TextWidthApproximation('?', font, fontSize);

        public float LineHeight
        {
            get
            {
                return CalculateCharSize('?', font, fontSize).y;
            }
        }
        
        /*/// <inheritdoc />
        protected override void ExecuteDefaultAction(EventBase evt)
        {
            long id = evt.eventTypeId;
            switch (id)
            {
                case {} when id == KeyDownEvent.TypeId():
                    ProcessKeyDownEvent((KeyDownEvent)evt);
                    break;
            }
        } */
        
        public bool IsDefaultColor(string color)
        {
            return this.GetColor(defaultbg) == color;
        }

        private void OnDestroy()
        {
            this.master.Close();
            this.slave.Close();

            this.master = null;
            this.slave = null;
        }

        private void Awake()
        {
            this.MustGetComponent(out rectTransform);
            this.MustGetComponent(out layoutElement);
            this.MustGetComponent(out bgImage);

            this.myCamera = Camera.main;
            
            this.transform.parent.MustGetComponent(out parentRectTransform);
        }

        private void Start()
        {
            this.timeout = -1;
            this.drawing = false;
            this.lastBlink = 0;
        }


        private bool ritchie;
        private void Update()
        {
            if (this.master is null)
                return;

            if (font == null)
                return;
            
            this.ptyOptions.RowCount = this.term.row;
            this.ptyOptions.ColumnCount = this.term.col;
            this.ptyOptions.CursorLeft = this.term.c.x;
            this.ptyOptions.CursorTop = this.term.c.y;

            float nww = this.parentRectTransform.rect.width;
            float nwh = this.parentRectTransform.rect.height;
            if (this.ww != nww || this.wh != nwh)
            {
                if (!ritchie)
                {
                    this.ww = nww;
                    this.wh = nwh;

                    if (!this.drawing)
                    {
                        this.drawing = true;
                        this.trigger = this.now;
                    }

                    float cw = this.CharacterWidth;
                    float ch = this.LineHeight;

                    int r = (int)Math.Ceiling(this.wh / ch) - 1;
                    int c = (int)Math.Ceiling(this.ww / cw) - 1;

                    r = Math.Max(r, this.DefaultRowCount);
                    c = Math.Max(c, this.DefaultColumnCount);

                    this.layoutElement.minWidth = DefaultColumnCount * cw;
                    this.layoutElement.minHeight = this.DefaultRowCount * ch;
                    if (r != this.term.row || c != this.term.col)
                    {
                        this.Resize(c, r);
                    }
                }
            }

            this.now += Time.deltaTime;

            // Here's where we read from the master pty device to get new characters to output.
            int ret = this.TtyRead();

            if (ret > 0)
            {
                if (!this.drawing)
                {
                    this.trigger = this.now;
                    this.drawing = true;
                }

                this.timeout = (this.maxLatency - (this.now - this.trigger))
                    / this.maxLatency * this.minLatency;
                if (this.timeout > 0)
                    return;
            }

            this.timeout = -1;
            if (this.blinkTimeout > 0)
            {
                this.timeout = this.blinkTimeout - (this.now - this.lastBlink);
                if (this.timeout <= 0)
                {
                    if (-this.timeout > this.blinkTimeout)
                    {
                        // what
                    }

                    this.SetDirtyAttribute(GlyphAttribute.ATTR_BLINK);
                    this.lastBlink = this.now;
                    this.timeout = this.blinkTimeout;
                }
            }

            this.Draw();
            this.drawing = false;
        }
        
        public void ApplyFallbackPalette()
        {
            this.SetDirtyFull();

            this.colors.Clear();

            // Fallback palette is based on the default palette in st
            this.SetColor(0, "000000");
            this.SetColor(1, "ce0000");
            this.SetColor(2, "00ce00");
            this.SetColor(3, "cece00");
            this.SetColor(4, "0000ee");
            this.SetColor(5, "ce00ce");
            this.SetColor(6, "00cece");
            this.SetColor(7, "e6e6e6");

            this.SetColor(8, "#808080");
            this.SetColor(9, "ff0000");
            this.SetColor(10, "00ff00");
            this.SetColor(11, "ffff00");
            this.SetColor(12, "5c5cff");
            this.SetColor(13, "ff00ff");
            this.SetColor(14, "00ffff");
            this.SetColor(15, "ffffff");

            this.SetColor(256, "cccccc");
            this.SetColor(257, "555555");
            this.SetColor(258, "000000");
            this.SetColor(259, "e6e6e6");

            string bg = this.GetColor(defaultbg);

            if (ColorUtility.TryParseHtmlString("#" + bg, out Color bgcol)) this.bgImage.color = bgcol;
        }

        private void ProcessKeyDownEvent(KeyDownEvent evt)
        {
            // Force redraw on keyboard events, just like st does
            if (!this.drawing)
            {
                this.trigger = this.now;
                this.drawing = true;
            }

            bool control = (evt.modifiers & (EventModifiers.Control | EventModifiers.Command)) != 0;
            bool alt = (evt.modifiers & EventModifiers.Alt) != 0;
            bool shift = (evt.modifiers & EventModifiers.Shift) != 0;
            bool modifiers = control
                             || alt
                             || shift;

            switch (evt.keyCode)
            {
                case KeyCode.PageUp when !modifiers:
                    this.RegionScrollUp(-1);
                    break;
                case KeyCode.PageDown when !modifiers:
                    this.RegionScrollDown(-1);
                    break;

                case KeyCode.Delete when !modifiers:
                    this.master.WriteByte(0x7f);
                    break;
                case KeyCode.UpArrow when !modifiers:
                    this.MasterWrite("\x1b[1A");
                    break;
                case KeyCode.DownArrow when !modifiers:
                    this.MasterWrite("\x1b[1B");
                    break;
                case KeyCode.RightArrow when !modifiers:
                    this.MasterWrite("\x1b[1C");
                    break;
                case KeyCode.LeftArrow when !modifiers:
                    this.MasterWrite("\x1b[1D");
                    break;
                case KeyCode.End when !modifiers:
                    this.MasterWrite("\x1b[4~");
                    break;
                case KeyCode.Home when !modifiers:
                    this.MasterWrite("\x1b[1~");
                    break;
                case KeyCode.Backspace when !modifiers:
                    this.MasterWrite("\b");
                    break;
                case KeyCode.Return when !modifiers :
                case KeyCode.KeypadEnter when !modifiers:
                    this.MasterWrite("\r\n");
                    break;
                case { } when evt.character != '\0':
                    char ch = evt.character;
                    if (ch < 127 || !this.IS_SET(TermMode.MODE_UTF8))
                    {
                        this.master.WriteByte((byte)ch);
                    }
                    else
                    {
                        var buf = new byte[UTF_SIZ];
                        unsafe
                        {
                            fixed (byte* c = buf)
                            {
                                TermUtf8.utf8encode(ch, c);
                            }
                        }

                        this.master.Write(buf, 0, buf.Length);
                    }

                    break;
            }
        }


        public string GetSelection()
        {
            var str = new StringBuilder();
            var y = 0;
            var lastx = 0;
            var linelen = 0;

            ref Glyph gp = ref this.term.line[0].glyphs[0];
            ref Glyph last = ref this.term.line[0].glyphs[0];

            if (this.sel.ob.x == -1 || this.sel.alt != this.IS_SET(TermMode.MODE_ALTSCREEN))
                return string.Empty;

            /* append every set & selected glyph to the selection */
            for (y = this.sel.nb.y; y <= this.sel.ne.y; y++)
            {
                ref Data.Line line = ref this.TLINE(y);
                if ((linelen = this.LineLength(ref line)) == 0)
                {
                    str.AppendLine();
                    continue;
                }

                var xtemp = 0;
                if (this.sel.type == Data.SelectionType.SEL_RECTANGULAR)
                {
                    xtemp = this.sel.nb.x;
                    gp = ref line.glyphs[xtemp];
                    lastx = this.sel.ne.x;
                }
                else
                {
                    xtemp = this.sel.nb.y == y ? this.sel.nb.x : 0;
                    gp = ref line.glyphs[xtemp];
                    lastx = this.sel.ne.y == y ? this.sel.ne.x : this.term.col - 1;
                }

                int xtemp2 = Math.Min(lastx, linelen - 1);
                last = ref line.glyphs[xtemp2];

                string glyphs = this.GetGlyphs(ref line, xtemp, xtemp2);
                str.Append(glyphs);

                /*
                 * Copy and pasting of line endings is inconsistent
                 * in the inconsistent terminal and GUI world.
                 * The best solution seems like to produce '\n' when
                 * something is copied from st and convert '\n' to
                 * '\r', when something to be pasted is received by
                 * st.
                 * FIXME: Fix the computer world.
                 */
                if ((y < this.sel.ne.y || lastx >= linelen) &&
                    ((line.glyphs[xtemp2].mode & GlyphAttribute.ATTR_WRAP) == 0 ||
                     this.sel.type == Data.SelectionType.SEL_RECTANGULAR))
                    str.AppendLine();
            }

            return str.ToString();
        }

        private void DrawLine(ref Glyph[] glyphs, int x1, int y, int x2)
        {
            RenderedLine line = this.renderedLines[y];
            line.SetGlyphs(ref glyphs, x1, x2, y);
        }

        private void DrawRegion(int x1, int y1, int x2, int y2)
        {
            int y;

            for (y = y1; y < y2; y++)
            {
                if (this.term.dirty[y] != 1)
                    continue;

                this.term.dirty[y] = 0;
                this.DrawLine(ref this.TLINE(y).glyphs, x1, y, x2);
            }
        }

        private void Draw()
        {
            int cx = this.term.c.x, ocx = this.term.ocx, ocy = this.term.ocy;

            /* adjust cursor position */
            this.term.ocx = Math.Clamp(this.term.ocx, 0, this.term.col - 1);
            this.term.ocy = Math.Clamp(this.term.ocy, 0, this.term.row - 1);
            if ((this.term.line[this.term.ocy].glyphs[this.term.ocx].mode & GlyphAttribute.ATTR_WDUMMY) != 0)
                this.term.ocx--;
            if ((this.term.line[this.term.c.y].glyphs[cx].mode & GlyphAttribute.ATTR_WDUMMY) != 0)
                cx--;

            this.UpdateCursorRect(cx, this.term.c.y, ref this.term.line[this.term.c.y].glyphs[cx], this.term.ocx,
                this.term.ocy, ref this.term.line[this.term.ocy].glyphs[this.term.ocx]);

            this.DrawRegion(0, 0, this.term.col, this.term.row);
            
            this.term.ocx = cx;
            this.term.ocy = this.term.c.y;

            // if (ocx != term.ocx || ocy != term.ocy)
            //    xximspot(term.ocx, term.ocy);
        }

        private void UpdateCursorRect(int cx, int cy, ref Glyph g, int ocx, int ocy, ref Glyph og)
        {
            if (cy != ocy)
            {
                this.term.dirty[ocy] = 1;
            }

            ref Glyph oldGlyph = ref this.TLINEABS(ocy).glyphs[ocx];
            ref Glyph newGlyph = ref this.TLINEABS(cy).glyphs[cx];
            
            oldGlyph.mode &= ~GlyphAttribute.ATTR_REVERSE;

            if (this.isFocused)
            {
                if ((newGlyph.mode & GlyphAttribute.ATTR_REVERSE) == 0)
                {
                    this.term.dirty[cy] = 1;
                    newGlyph.mode |= GlyphAttribute.ATTR_REVERSE;
                }
            }
            else
            {
                if ((newGlyph.mode & GlyphAttribute.ATTR_REVERSE) != 0)
                {
                    this.term.dirty[cy] = 1;
                    newGlyph.mode &= ~GlyphAttribute.ATTR_REVERSE;
                }
            }

            this.term.ocx = cx;
            this.term.ocy = ocy;
        }

        public string GetColor(int c, Color rgbColor = default)
        {
            if (c == -1)
                return ColorUtility.ToHtmlStringRGB(rgbColor);

            if (this.colors.ContainsKey(c))
                return this.colors[c];
            return "#000000";
        }

        public void SetColor(int index, string c)
        {
            if (c.StartsWith("#"))
                c = c.Remove(0, 1);

            if (this.colors.ContainsKey(index))
                this.colors[index] = c;
            else
                this.colors.Add(index, c);
        }


        private void SetDirtyAttribute(GlyphAttribute attr)
        {
            int i, j;

            for (i = 0; i < this.term.row - 1; i++)
            for (j = 0; j < this.term.col - 1; j++)
                if ((this.term.line[i].glyphs[j].mode & attr) != 0)
                {
                    this.term.dirty[i] = 1;
                    break;
                }
        }

        private void TerminalNew(int cols, int rows)
        {
            this.term = new Term();


            var i = 0;
            var j = 0;

            for (i = 0; i < 2; i++)
            {
                this.term.line = new Data.Line[rows];
                for (j = 0; j < rows; j++) this.term.line[j].glyphs = new Glyph[cols];
                this.term.col = cols;
                this.term.row = rows;
                this.SwapScreen();
            }

            this.term.dirty = new int[rows];
            this.term.tabs = new int[cols];
            this.term.hist = XRealloc(ref this.term.hist, HISTSIZE);
            for (i = 0; i < HISTSIZE; i++) this.term.hist[i].glyphs = new Glyph[cols];

            this.Reset();
            this.ApplyFallbackPalette();

            this.term.mode |= TermMode.MODE_CRLF;
            this.ReallocateTextPool();
        }

        private void Resize(int col, int row)
        {
            /* col and row are always MAX(_, 1)
            if (col < 1 || row < 1) {
                fprintf(stderr, "tresize: error resizing to %dx%d\n", col, row);
                return;
            } */

            this.term.dirty = XRealloc(ref this.term.dirty, row);
            this.term.tabs = XRealloc(ref this.term.tabs, col);

            if (col > this.term.col)
            {
                int bp = this.term.col;
                for (int i = bp; i < this.term.tabs.Length; i++) this.term.tabs[i] = 0;

                while (--bp > 0 && this.term.tabs[bp] == 0)
                    /* nothing */ ;
                for (bp += this.tabSpaces; bp < col; bp += this.tabSpaces) this.term.tabs[bp] = 1;
            }

            if (this.IS_SET(TermMode.MODE_ALTSCREEN))
                this.ResizeAlt(col, row);
            else
                this.ResizeDef(col, row);

            this.ReallocateTextPool();
        }

        private void ReallocateTextPool()
        {
            if (this.renderedLines is null) this.renderedLines = new RenderedLine[this.term.row];

            if (this.renderedLines.Length != this.term.row)
            {
                for (int i = this.term.row; i < this.renderedLines.Length; i++)
                {
                    RenderedLine line = this.renderedLines[i];
                    line.Destroy();
                    this.renderedLines[i] = null;
                }

                XRealloc(ref this.renderedLines, this.term.row);

                for (var i = 0; i < this.renderedLines.Length; i++)
                {
                    RenderedLine line = this.renderedLines[i];
                    if (line is null)
                        line = new RenderedLine(this);

                    line.SetFont(font, fontSize);
                    line.SetColumnSize(this.term.col);
                    this.renderedLines[i] = line;
                }
            }
        }

        private Data.Line[] altline = null;
        private int altcol = 0;
        private int altrow = 0;

        private void SwapScreen()
        {
            Data.Line[] tmpline = this.term.line;
            int tmpcol = this.term.col;
            int tmprow = this.term.row;

            this.term.line = this.altline;
            this.term.col = this.altcol;
            this.term.row = this.altrow;
            this.altline = tmpline;
            this.altcol = tmpcol;
            this.altrow = tmprow;
            this.term.mode ^= TermMode.MODE_ALTSCREEN;
        }

        private void SetDirtyFull()
        {
            for (var i = 0; i < this.term.row; i++) this.term.dirty[i] = 1;
        }

        private void SetScroll(int t, int b)
        {
            int temp;

            t = Math.Clamp(t, 0, this.term.row - 1);
            b = Math.Clamp(b, 0, this.term.row - 1);
            if (t > b)
            {
                temp = t;
                t = b;
                b = temp;
            }

            this.term.top = t;
            this.term.bot = b;
        }

        private void Reset()
        {
            var i = 0u;
            var x = 0;
            var y = 0;

            this.ResetCursor();

            for (i = 0; i < this.term.col; i++) this.term.tabs[i] = 0;
            for (i = (uint)this.tabSpaces; i < this.term.col; i += (uint)this.tabSpaces) this.term.tabs[i] = 1;
            this.term.top = 0;
            this.term.histf = 0;
            this.term.scr = 0;
            this.term.bot = this.term.row - 1;
            this.term.mode = TermMode.MODE_WRAP | TermMode.MODE_UTF8;

            for (i = 0; i < this.term.trantbl.Length; i++) this.term.trantbl[i] = Charset.CS_USA;

            this.term.charset = 0;
            this.SelectionRemove();

            for (i = 0; i < 2; i++)
            {
                this.CursorMovementHandler(CursorMovement.CURSOR_SAVE); /* reset saved cursor */
                for (y = 0; y < this.term.row; y++)
                for (x = 0; x < this.term.col; x++)
                    this.ClearGlyph(ref this.term.line[y].glyphs[x], false);
                this.SwapScreen();
            }

            this.SetDirtyFull();
        }

        private void CursorMovementHandler(CursorMovement mode)
        {
            int alt = this.IS_SET(TermMode.MODE_ALTSCREEN) ? 1 : 0;

            if (mode == CursorMovement.CURSOR_SAVE)
            {
                this.cursorMemory[alt] = this.term.c;
            }
            else if (mode == CursorMovement.CURSOR_LOAD)
            {
                this.term.c = this.cursorMemory[alt];
                this.MoveTo(this.cursorMemory[alt].x, this.cursorMemory[alt].y);
            }
        }

        private void PutTab(int n)
        {
            int x = this.term.c.x;

            if (n > 0)
                while (x < this.term.col && n-- != 0)
                    for (++x; x < this.term.col && this.term.tabs[x] == 0; ++x)
                        /* nothing */
                        ;
            else if (n < 0)
                while (x > 0 && n++ != 0)
                    for (--x; x > 0 && this.term.tabs[x] != 0; --x)
                        /* nothing */
                        ;

            this.term.c.x = Math.Clamp(x, 0, this.term.col - 1);
        }

        private void ControlCode(uint ascii)
        {
            switch (ascii)
            {
                case '\t': /* HT */
                    this.PutTab(1);
                    return;
                case '\b': /* BS */
                    this.MoveTo(this.term.c.x - 1, this.term.c.y);
                    return;
                case '\r': /* CR */
                    this.MoveTo(0, this.term.c.y);
                    return;
                case '\f': /* LF */
                case '\v': /* VT */
                case '\n': /* LF */
                    /* go to first col if the mode is set */
                    this.NewLine(this.IS_SET(TermMode.MODE_CRLF) ? 1 : 0, 1);
                    return;
                case '\a': /* BEL */
                    if ((this.term.esc & EscapeState.ESC_STR_END) != 0)
                        /* backwards compatibility to xterm */
                        this.StrHandle();
                    else
                        this.Bell();

                    break;
                case 0x1b: /* ESC */
                    this.CsiReset();
                    this.term.esc &= ~(EscapeState.ESC_CSI | EscapeState.ESC_ALTCHARSET | EscapeState.ESC_TEST);
                    this.term.esc |= EscapeState.ESC_START;
                    return;
                case 0x0e: /* SO (LS1 -- Locking shift 1) */
                case 0x0f: /* SI (LS0 -- Locking shift 0) */
                    this.term.charset = 1 - (byte)(ascii - 0x16);
                    return;
                case 0x1a: /* SUB */
                    this.SetChar('?', ref this.term.c.attr, this.term.c.x, this.term.c.y);
                    /* FALLTHROUGH */
                    goto case 0x18;
                case 0x18: /* CAN */
                    this.CsiReset();
                    break;
                case 0x05: /* ENQ (IGNORED) */
                case 0x00: /* NUL (IGNORED) */
                case 0x11: /* XON (IGNORED) */
                case 0x13: /* XOFF (IGNORED) */
                case 0x7f: /* DEL (IGNORED) */
                    return;
                case 0x80: /* TODO: PAD */
                case 0x81: /* TODO: HOP */
                case 0x82: /* TODO: BPH */
                case 0x83: /* TODO: NBH */
                case 0x84: /* TODO: IND */
                    break;
                case 0x85: /* NEL -- Next line */
                    this.NewLine(1); /* always go to first col */
                    break;
                case 0x86: /* TODO: SSA */
                case 0x87: /* TODO: ESA */
                    break;
                case 0x88: /* HTS -- Horizontal tab stop */
                    this.term.tabs[this.term.c.x] = 1;
                    break;
                case 0x89: /* TODO: HTJ */
                case 0x8a: /* TODO: VTS */
                case 0x8b: /* TODO: PLD */
                case 0x8c: /* TODO: PLU */
                case 0x8d: /* TODO: RI */
                case 0x8e: /* TODO: SS2 */
                case 0x8f: /* TODO: SS3 */
                case 0x91: /* TODO: PU1 */
                case 0x92: /* TODO: PU2 */
                case 0x93: /* TODO: STS */
                case 0x94: /* TODO: CCH */
                case 0x95: /* TODO: MW */
                case 0x96: /* TODO: SPA */
                case 0x97: /* TODO: EPA */
                case 0x98: /* TODO: SOS */
                case 0x99: /* TODO: SGCI */
                    break;
                case 0x9a: /* DECID -- Identify Terminal */
                    break;
                case 0x9b: /* TODO: CSI */
                case 0x9c: /* TODO: ST */
                    break;
                case 0x90: /* DCS -- Device Control String */
                case 0x9d: /* OSC -- Operating System Command */
                case 0x9e: /* PM -- Privacy Message */
                case 0x9f: /* APC -- Application Program Command */
                    this.StrSequence(ascii);
                    return;
            }

            /* only CAN, SUB, \a and C1 chars interrupt a sequence */
            this.term.esc &= ~(EscapeState.ESC_STR_END | EscapeState.ESC_STR);
        }

        public void Bell()
        {
            if (this.asciiBeep != null)
                this.trixelAudio.Play(this.asciiBeep);
        }

        private void StrSequence(uint ascii)
        {
        }

        private void StrHandle()
        {
        }

        private void CsiReset()
        {
            this.csiescseq = new CSIEscape();
        }

        private void CsiParse()
        {
            unsafe
            {
                fixed (byte* p = this.csiescseq.buf)
                {
                    byte* np = null;
                    long v = 0;

                    byte* p2 = p;

                    this.csiescseq.narg = 0;
                    if (*p2 == '?')
                    {
                        this.csiescseq.priv = 1;
                        p2++;
                    }

                    this.csiescseq.buf[this.csiescseq.len] = 0;
                    while (p2 < p + this.csiescseq.len)
                    {
                        np = null;
                        byte** nnp = &np;
                        v = strtol(p2, ref nnp, 10);
                        if (np == p)
                            v = 0;
                        if (v == long.MaxValue || v == long.MinValue)
                            v = -1;
                        this.csiescseq.arg[this.csiescseq.narg++] = (int)v;
                        p2 = np;
                        if (*p2 != ';' || this.csiescseq.narg == ESC_ARG_SIZ)
                            break;
                        p2++;
                    }

                    this.csiescseq.mode[0] = *p2++;
                    this.csiescseq.mode[1] = (byte)(p2 < p + this.csiescseq.len ? *p2 : 0);
                }
            }
        }

        private void CsiDump()
        {
        }

        private void MoveATo(int x, int y)
        {
            this.MoveTo(x, y + ((this.term.c.state & CursorState.CURSOR_ORIGIN) != 0 ? this.term.top : 0));
        }

        private void CsiHandle()
        {
            unsafe
            {
                byte* buf = stackalloc byte[40];
                var n = 0;
                var x = 0;


                switch ((char)this.csiescseq.mode[0])
                {
                    default:
                        unknown:
                        Debug.LogError("unknown csi");
                        this.CsiDump();
                        break;
                    case '@': /* ICH -- Insert <n> blank char */
                        this.csiescseq.arg[0] = this.csiescseq.arg[0] == 0 ? 1 : this.csiescseq.arg[0];
                        this.InsertBlank(this.csiescseq.arg[0]);
                        break;
                    case 'A': /* CUU -- Cursor <n> Up */
                        this.csiescseq.arg[0] = this.csiescseq.arg[0] == 0 ? 1 : this.csiescseq.arg[0];
                        this.MoveTo(this.term.c.x, this.term.c.y - this.csiescseq.arg[0]);
                        break;
                    case 'B': /* CUD -- Cursor <n> Down */
                    case 'e': /* VPR --Cursor <n> Down */
                        this.csiescseq.arg[0] = this.csiescseq.arg[0] == 0 ? 1 : this.csiescseq.arg[0];
                        this.MoveTo(this.term.c.x, this.term.c.y + this.csiescseq.arg[0]);
                        break;
                    case 'i': /* MC -- Media Copy */
                        switch (this.csiescseq.arg[0])
                        {
                            case 0:
                                this.Dump();
                                break;
                            case 1:
                                this.DumpLine(this.term.c.y);
                                break;
                            case 2:
                                this.DumpSelection();
                                break;
                            case 4:
                                this.term.mode &= ~TermMode.MODE_PRINT;
                                break;
                            case 5:
                                this.term.mode |= TermMode.MODE_PRINT;
                                break;
                        }

                        break;
                    case 'c': /* DA -- Device Attributes */
                        if (this.csiescseq.arg[0] == 0) this.MasterWrite(this.vtiden);
                        break;
                    case 'b': /* REP -- if last char is printable print it <n> more times */
                        this.csiescseq.arg[0] = this.csiescseq.arg[0] == 0 ? 1 : this.csiescseq.arg[0];
                        if (this.term.lastc != 0)
                            while (this.csiescseq.arg[0]-- > 0)
                                this.PutChar(this.term.lastc);
                        break;
                    case 'C': /* CUF -- Cursor <n> Forward */
                    case 'a': /* HPR -- Cursor <n> Forward */
                        this.csiescseq.arg[0] = this.csiescseq.arg[0] == 0 ? 1 : this.csiescseq.arg[0];
                        this.MoveTo(this.term.c.x + this.csiescseq.arg[0], this.term.c.y);
                        break;
                    case 'D': /* CUB -- Cursor <n> Backward */
                        this.csiescseq.arg[0] = this.csiescseq.arg[0] == 0 ? 1 : this.csiescseq.arg[0];
                        this.MoveTo(this.term.c.x - this.csiescseq.arg[0], this.term.c.y);
                        break;
                    case 'E': /* CNL -- Cursor <n> Down and first col */
                        this.csiescseq.arg[0] = this.csiescseq.arg[0] == 0 ? 1 : this.csiescseq.arg[0];
                        this.MoveTo(0, this.term.c.y + this.csiescseq.arg[0]);
                        break;
                    case 'F': /* CPL -- Cursor <n> Up and first col */
                        this.csiescseq.arg[0] = this.csiescseq.arg[0] == 0 ? 1 : this.csiescseq.arg[0];
                        this.MoveTo(0, this.term.c.y - this.csiescseq.arg[0]);
                        break;
                    case 'g': /* TBC -- Tabulation clear */
                        switch (this.csiescseq.arg[0])
                        {
                            case 0: /* clear current tab stop */
                                this.term.tabs[this.term.c.x] = 0;
                                break;
                            case 3: /* clear all the tabs */
                            {
                                for (var t = 0; t < this.term.tabs.Length; t++) this.term.tabs[t] = 0;
                            }
                                break;
                            default:
                                goto unknown;
                        }

                        break;
                    case 'G': /* CHA -- Move to <col> */
                    case '`': /* HPA */
                        this.csiescseq.arg[0] = this.csiescseq.arg[0] == 0 ? 1 : this.csiescseq.arg[0];
                        this.MoveTo(this.csiescseq.arg[0] - 1, this.term.c.y);
                        break;
                    case 'H': /* CUP -- Move to <row> <col> */
                    case 'f': /* HVP */
                        this.csiescseq.arg[0] = this.csiescseq.arg[0] == 0 ? 1 : this.csiescseq.arg[0];
                        this.csiescseq.arg[1] = this.csiescseq.arg[1] == 0 ? 1 : this.csiescseq.arg[1];
                        this.MoveATo(this.csiescseq.arg[1] - 1, this.csiescseq.arg[0] - 1);
                        break;
                    case 'I': /* CHT -- Cursor Forward Tabulation <n> tab stops */
                        this.csiescseq.arg[0] = this.csiescseq.arg[0] == 0 ? 1 : this.csiescseq.arg[0];
                        this.PutTab(this.csiescseq.arg[0]);
                        break;
                    case 'J': /* ED -- Clear screen */
                        switch (this.csiescseq.arg[0])
                        {
                            case 0: /* below */
                                this.ClearRegion(this.term.c.x, this.term.c.y, this.term.col - 1, this.term.c.y, true);
                                if (this.term.c.y < this.term.row - 1)
                                    this.ClearRegion(0, this.term.c.y + 1, this.term.col - 1, this.term.row - 1, true);
                                break;
                            case 1: /* above */
                                if (this.term.c.y >= 1)
                                    this.ClearRegion(0, 0, this.term.col - 1, this.term.c.y - 1, true);
                                this.ClearRegion(0, this.term.c.y, this.term.c.x, this.term.c.y, true);
                                break;
                            case 2: /* all */
                                if (this.IS_SET(TermMode.MODE_ALTSCREEN))
                                {
                                    this.ClearRegion(0, 0, this.term.col - 1, this.term.row - 1, true);
                                    break;
                                }
                                /* vte does this:
                                tscrollup(0, term.row-1, term.row, SCROLL_SAVEHIST); */

                                /* alacritty does this: */
                                for (n = this.term.row - 1;
                                     n >= 0 && this.LineLength(ref this.term.line[n]) == 0;
                                     n--) ;
                                if (n >= 0) this.ScrollUp(0, this.term.row - 1, n + 1, ScrollMode.SCROLL_SAVEHIST);
                                this.ScrollUp(0, this.term.row - 1, this.term.row - n - 1,
                                    ScrollMode.SCROLL_NOSAVEHIST);
                                break;
                            default:
                                goto unknown;
                        }

                        break;
                    case 'K': /* EL -- Clear line */
                        switch (this.csiescseq.arg[0])
                        {
                            case 0: /* right */
                                this.ClearRegion(this.term.c.x, this.term.c.y, this.term.col - 1, this.term.c.y, true);
                                break;
                            case 1: /* left */
                                this.ClearRegion(0, this.term.c.y, this.term.c.x, this.term.c.y, true);
                                break;
                            case 2: /* all */
                                this.ClearRegion(0, this.term.c.y, this.term.col - 1, this.term.c.y, true);
                                break;
                        }

                        break;
                    case 'S': /* SU -- Scroll <n> line up */
                        this.csiescseq.arg[0] = this.csiescseq.arg[0] == 0 ? 1 : this.csiescseq.arg[0];
                        /* xterm, urxvt, alacritty save this in history */
                        this.ScrollUp(this.term.top, this.term.bot, this.csiescseq.arg[0], ScrollMode.SCROLL_SAVEHIST);
                        break;
                    case 'T': /* SD -- Scroll <n> line down */
                        this.csiescseq.arg[0] = this.csiescseq.arg[0] == 0 ? 1 : this.csiescseq.arg[0];
                        this.ScrollDown(this.term.top, this.csiescseq.arg[0]);
                        break;
                    case 'L': /* IL -- Insert <n> blank lines */
                        this.csiescseq.arg[0] = this.csiescseq.arg[0] == 0 ? 1 : this.csiescseq.arg[0];
                        this.InsertBlankLine(this.csiescseq.arg[0]);
                        break;
                    case 'l': /* RM -- Reset Mode */
                        this.SetMode(this.csiescseq.priv, 0, this.csiescseq.arg, this.csiescseq.narg);
                        break;
                    case 'M': /* DL -- Delete <n> lines */
                        this.csiescseq.arg[0] = this.csiescseq.arg[0] == 0 ? 1 : this.csiescseq.arg[0];
                        this.DeleteLine(this.csiescseq.arg[0]);
                        break;
                    case 'X': /* ECH -- Erase <n> char */
                        if (this.csiescseq.arg[0] < 0)
                            return;

                        this.csiescseq.arg[0] = this.csiescseq.arg[0] == 0 ? 1 : this.csiescseq.arg[0];
                        x = Math.Min(this.term.c.x + this.csiescseq.arg[0], this.term.col) - 1;
                        this.ClearRegion(this.term.c.x, this.term.c.y, x, this.term.c.y, true);
                        break;
                    case 'P': /* DCH -- Delete <n> char */
                        this.csiescseq.arg[0] = this.csiescseq.arg[0] == 0 ? 1 : this.csiescseq.arg[0];
                        this.DeleteChar(this.csiescseq.arg[0]);
                        break;
                    case 'Z': /* CBT -- Cursor Backward Tabulation <n> tab stops */
                        this.csiescseq.arg[0] = this.csiescseq.arg[0] == 0 ? 1 : this.csiescseq.arg[0];
                        this.PutTab(-this.csiescseq.arg[0]);
                        break;
                    case 'd': /* VPA -- Move to <row> */
                        this.csiescseq.arg[0] = this.csiescseq.arg[0] == 0 ? 1 : this.csiescseq.arg[0];
                        this.MoveATo(this.term.c.x, this.csiescseq.arg[0] - 1);
                        break;
                    case 'h': /* SM -- Set terminal mode */
                        this.SetMode(this.csiescseq.priv, 1, this.csiescseq.arg, this.csiescseq.narg);
                        break;
                    case 'm': /* SGR -- Terminal attribute (color) */
                        this.SetAttr(this.csiescseq.arg, this.csiescseq.narg);
                        break;
                    case 'n': /* DSR – Device Status Report (cursor position) */
                        if (this.csiescseq.arg[0] == 6)
                            this.MasterWrite($"\x1b[{this.term.c.x + 1};{this.term.c.y + 1}R");

                        break;
                    case 'r': /* DECSTBM -- Set Scrolling Region */
                        if (this.csiescseq.priv > 0)
                        {
                            goto unknown;
                        }
                        else
                        {
                            this.csiescseq.arg[0] = this.csiescseq.arg[0] == 0 ? 1 : this.csiescseq.arg[0];
                            this.csiescseq.arg[1] = this.csiescseq.arg[1] == 0 ? this.term.row : this.csiescseq.arg[1];
                            this.SetScroll(this.csiescseq.arg[0] - 1, this.csiescseq.arg[1] - 1);
                            this.MoveATo(0, 0);
                        }

                        break;
                    case 's': /* DECSC -- Save cursor position (ANSI.SYS) */
                        this.CursorMovementHandler(CursorMovement.CURSOR_SAVE);
                        break;
                    case 'u': /* DECRC -- Restore cursor position (ANSI.SYS) */
                        this.CursorMovementHandler(CursorMovement.CURSOR_LOAD);
                        break;
                    case ' ':
                        switch ((char)this.csiescseq.mode[1])
                        {
                            case 'q': /* DECSCUSR -- Set Cursor Style */
                                // if (SetCursor(csiescseq.arg[0]))
                                // goto unknown;
                                break;
                            default:
                                goto unknown;
                        }

                        break;
                }
            }
        }

        private void Dump()
        {
        }

        private void DumpLine(int y)
        {
        }

        private void DumpSelection()
        {
        }

        private void DeleteChar(int n)
        {
            var src = 0;
            var dst = 0;
            var size = 0;

            if (n <= 0)
                return;

            dst = this.term.c.x;
            src = Math.Min(this.term.c.x + n, this.term.col);
            size = this.term.col - src;

            if (size > 0)
            {
                /* otherwise src would point beyond the array
	                   https://stackoverflow.com/questions/29844298 */
                ref Data.Line line = ref this.term.line[this.term.c.y];
                Array.Copy(line.glyphs, src, line.glyphs, dst, size);
            }

            this.ClearRegion(dst + size, this.term.c.y, this.term.col - 1, this.term.c.y, true);
        }

        private void DeleteLine(int n)
        {
            if (BETWEEN(this.term.c.y, this.term.top, this.term.bot))
                this.ScrollUp(this.term.c.y, this.term.bot, n, ScrollMode.SCROLL_NOSAVEHIST);
        }

        private void SetMode(int priv, int set, int[] args, int narg)
        {
            unsafe
            {
                int* lim = null;

                fixed (int* a = args)
                {
                    int* ptr = a;
                    for (lim = ptr + narg; ptr < lim; ptr++)
                        if (priv == 1)
                            switch (*ptr)
                            {
                                case 1: /* DECCKM -- Cursor key */
                                    // xsetmode(set, MODE_APPCURSOR);
                                    break;
                                case 5: /* DECSCNM -- Reverse video */
                                    // xsetmode(set, MODE_REVERSE);
                                    break;
                                case 6: /* DECOM -- Origin */
                                    this.MODBIT(ref this.term.c.state, set, CursorState.CURSOR_ORIGIN);
                                    this.MoveATo(0, 0);
                                    break;
                                case 7: /* DECAWM -- Auto wrap */
                                    this.MODBIT(ref this.term.mode, set, TermMode.MODE_WRAP);
                                    break;
                                case 0: /* Error (IGNORED) */
                                case 2: /* DECANM -- ANSI/VT52 (IGNORED) */
                                case 3: /* DECCOLM -- Column  (IGNORED) */
                                case 4: /* DECSCLM -- Scroll (IGNORED) */
                                case 8: /* DECARM -- Auto repeat (IGNORED) */
                                case 18: /* DECPFF -- Printer feed (IGNORED) */
                                case 19: /* DECPEX -- Printer extent (IGNORED) */
                                case 42: /* DECNRCM -- National characters (IGNORED) */
                                case 12: /* att610 -- Start blinking cursor (IGNORED) */
                                    break;
                                case 25: /* DECTCEM -- Text Cursor Enable Mode */
                                    // xsetmode(!set, MODE_HIDE);
                                    break;
                                case 9: /* X10 mouse compatibility mode */
                                    //xsetpointermotion(0);
                                    //xsetmode(0, MODE_MOUSE);
                                    //xsetmode(set, MODE_MOUSEX10);
                                    break;
                                case 1000: /* 1000: report button press */
                                    // xsetpointermotion(0);
                                    // xsetmode(0, MODE_MOUSE);
                                    // xsetmode(set, MODE_MOUSEBTN);
                                    break;
                                case 1002: /* 1002: report motion on button press */
                                    // xsetpointermotion(0);
                                    // xsetmode(0, MODE_MOUSE);
                                    // xsetmode(set, MODE_MOUSEMOTION);
                                    break;
                                case 1003: /* 1003: enable all mouse motions */
                                    // xsetpointermotion(set);
                                    // xsetmode(0, MODE_MOUSE);
                                    // xsetmode(set, MODE_MOUSEMANY);
                                    break;
                                case 1004: /* 1004: send focus events to tty */
                                    // xsetmode(set, MODE_FOCUS);
                                    break;
                                case 1006: /* 1006: extended reporting mode */
                                    // xsetmode(set, MODE_MOUSESGR);
                                    break;
                                case 1034:
                                    // xsetmode(set, MODE_8BIT);
                                    break;
                                case 1049: /* swap screen & set/restore cursor as xterm */
                                case 47: /* swap screen */
                                case 1047: /* swap screen, clearing alternate screen */
                                    if (!this.allowAltScreen)
                                        break;

                                    if (set != 0)
                                        this.LoadAltScreen(*ptr == 1049, *ptr == 1049);
                                    else
                                        this.LoadDefScreen(*ptr == 1047, *ptr == 1049);
                                    break;
                                case 1048:
                                    if (!this.allowAltScreen)
                                        break;

                                    this.CursorMovementHandler(set != 0
                                        ? CursorMovement.CURSOR_SAVE
                                        : CursorMovement.CURSOR_LOAD);
                                    break;
                                case 2004: /* 2004: bracketed paste mode */
                                    // xsetmode(set, MODE_BRCKTPASTE);
                                    break;
                                /* Not implemented mouse modes. See comments there. */
                                case 1001: /* mouse highlight mode; can hang the
				      terminal by design when implemented. */
                                case 1005: /* UTF-8 mouse mode; will confuse
				      applications not supporting UTF-8
				      and luit. */
                                case 1015: /* urxvt mangled mouse mode; incompatible
				      and can be mistaken for other control
				      codes. */
                                    break;
                                default:
                                    Debug.LogError($"erresc: unknown private set/reset mode {*ptr}");
                                    break;
                            }
                        else
                            switch (*ptr)
                            {
                                case 0: /* Error (IGNORED) */
                                    break;
                                case 2:
                                    // xsetmode(set, MODE_KBDLOCK);
                                    break;
                                case 4: /* IRM -- Insertion-replacement */
                                    this.MODBIT(ref this.term.mode, set, TermMode.MODE_INSERT);
                                    break;
                                case 12: /* SRM -- Send/Receive */
                                    this.MODBIT(ref this.term.mode, set == 0 ? 1 : 0, TermMode.MODE_ECHO);
                                    break;
                                case 20: /* LNM -- Linefeed/new line */
                                    this.MODBIT(ref this.term.mode, set, TermMode.MODE_CRLF);
                                    break;
                                default:
                                    Debug.LogError($"erresc: unknown set/reset mode {*ptr}\n");
                                    break;
                            }
                }
            }
        }

        private void SetAttr(int[] attr, int l)
        {
            var i = 0;

            for (i = 0; i < l; i++)
                switch (attr[i])
                {
                    case 0:
                        this.term.c.attr.mode &= ~(
                            GlyphAttribute.ATTR_BOLD |
                            GlyphAttribute.ATTR_FAINT |
                            GlyphAttribute.ATTR_ITALIC |
                            GlyphAttribute.ATTR_UNDERLINE |
                            GlyphAttribute.ATTR_BLINK |
                            GlyphAttribute.ATTR_REVERSE |
                            GlyphAttribute.ATTR_INVISIBLE |
                            GlyphAttribute.ATTR_STRUCK);
                        this.term.c.attr.fg = defaultfg;
                        this.term.c.attr.bg = defaultbg;
                        break;
                    case 1:
                        this.term.c.attr.mode |= GlyphAttribute.ATTR_BOLD;
                        break;
                    case 2:
                        this.term.c.attr.mode |= GlyphAttribute.ATTR_FAINT;
                        break;
                    case 3:
                        this.term.c.attr.mode |= GlyphAttribute.ATTR_ITALIC;
                        break;
                    case 4:
                        this.term.c.attr.mode |= GlyphAttribute.ATTR_UNDERLINE;
                        break;
                    case 5: /* slow blink */
                    /* FALLTHROUGH */
                    case 6: /* rapid blink */
                        this.term.c.attr.mode |= GlyphAttribute.ATTR_BLINK;
                        break;
                    case 7:
                        this.term.c.attr.mode |= GlyphAttribute.ATTR_REVERSE;
                        break;
                    case 8:
                        this.term.c.attr.mode |= GlyphAttribute.ATTR_INVISIBLE;
                        break;
                    case 9:
                        this.term.c.attr.mode |= GlyphAttribute.ATTR_STRUCK;
                        break;
                    case 22:
                        this.term.c.attr.mode &= ~(GlyphAttribute.ATTR_BOLD | GlyphAttribute.ATTR_FAINT);
                        break;
                    case 23:
                        this.term.c.attr.mode &= ~GlyphAttribute.ATTR_ITALIC;
                        break;
                    case 24:
                        this.term.c.attr.mode &= ~GlyphAttribute.ATTR_UNDERLINE;
                        break;
                    case 25:
                        this.term.c.attr.mode &= ~GlyphAttribute.ATTR_BLINK;
                        break;
                    case 27:
                        this.term.c.attr.mode &= ~GlyphAttribute.ATTR_REVERSE;
                        break;
                    case 28:
                        this.term.c.attr.mode &= ~GlyphAttribute.ATTR_INVISIBLE;
                        break;
                    case 29:
                        this.term.c.attr.mode &= ~GlyphAttribute.ATTR_STRUCK;
                        break;
                    case 38:
                    {
                        if (i + 1 >= l)
                            continue;

                        int fmt = attr[i + 1];

                        switch (fmt)
                        {
                            case 2:
                                i += 2;
                                if (i + 2 >= l)
                                {
                                    Debug.LogError("Malformed RGB foreground color sequence");
                                    CsiDump();
                                    return;
                                }

                                int r = attr[i];
                                int g = attr[i + 1];
                                int b = attr[i + 2];

                                i += 2;

                                term.c.attr.fg = -1;
                                term.c.attr.fgRgb = new Color(r / 255f, g / 255f, b / 255f);
                                
                                break;
                            default:
                                Debug.LogError($"Color format {fmt} not supported");
                                this.CsiDump();
                                return;
                        }
                        break;
                    }
                    case 39:
                        this.term.c.attr.fg = defaultfg;
                        break;
                    case 48:
                        // if ((idx = tdefcolor(attr, &i, l)) >= 0)
                            // term.c.attr.bg = idx;
                        break;
                    case 49:
                        this.term.c.attr.bg = defaultbg;
                        break;
                    default:
                        if (BETWEEN(attr[i], 30, 37))
                        {
                            this.term.c.attr.fg = attr[i] - 30;
                        }
                        else if (BETWEEN(attr[i], 40, 47))
                        {
                            this.term.c.attr.bg = attr[i] - 40;
                        }
                        else if (BETWEEN(attr[i], 90, 97))
                        {
                            this.term.c.attr.fg = attr[i] - 90 + 8;
                        }
                        else if (BETWEEN(attr[i], 100, 107))
                        {
                            this.term.c.attr.bg = attr[i] - 100 + 8;
                        }
                        else
                        {
                            Debug.LogError($"erresc(default): gfx attr {attr[i]} unknown\n");
                            this.CsiDump();
                        }

                        break;
                }
        }

        private void InsertBlankLine(int n)
        {
            if (BETWEEN(this.term.c.y, this.term.top, this.term.bot)) this.ScrollDown(this.term.c.y, n);
        }

        private void InsertBlank(int n)
        {
            var dst = 0;
            var src = 0;
            var size = 0;

            if (n <= 0)
                return;

            dst = Math.Min(this.term.c.x + n, this.term.col);
            src = this.term.c.x;
            size = this.term.col - dst;

            if (size > 0)
            {
                /* otherwise dst would point beyond the array */
                ref Data.Line line = ref this.term.line[this.term.c.y];
                Array.Copy(line.glyphs, src, line.glyphs, dst, size);
            }

            this.ClearRegion(src, this.term.c.y, dst - 1, this.term.c.y, true);
        }

        private void SetChar(uint u, ref Glyph attr, int x, int y)
        {
            /*
             * The table is proudly stolen from rxvt.
             */
            if (this.term.trantbl[this.term.charset] == Charset.CS_GRAPHIC0 &&
                BETWEEN(u, 0x41, 0x7e) && !string.IsNullOrEmpty(vt100_0[u - 0x41]))
                TermUtf8.utf8decode(Encoding.UTF8.GetBytes(vt100_0[u - 0x41]), 0, out u, (int)UTF_SIZ);

            ref Glyph g = ref this.term.line[y].glyphs[x];
            if ((g.mode & GlyphAttribute.ATTR_WIDE) != 0)
            {
                if (x + 1 < this.term.col)
                {
                    ref Glyph g1 = ref this.term.line[y].glyphs[x + 1];
                    g1.u = ' ';
                    g1.mode &= ~GlyphAttribute.ATTR_WDUMMY;
                }
            }
            else if ((g.mode & GlyphAttribute.ATTR_WDUMMY) != 0)
            {
                ref Glyph g1 = ref this.term.line[y].glyphs[x - 1];
                g1.u = ' ';
                g1.mode &= ~GlyphAttribute.ATTR_WIDE;
            }

            this.term.dirty[y] = 1;
            g.mode = attr.mode;
            g.fg = attr.fg;
            g.fgRgb = attr.fgRgb;
            g.bg = attr.bg;
            g.u = u;
            g.mode |= GlyphAttribute.ATTR_SET;
        }

        private void DefUtf8(uint u)
        {
        }

        private void DefTran(uint u)
        {
        }

        private void DecTest(uint u)
        {
        }

        private bool EscHandle(uint ascii)
        {
            switch (ascii)
            {
                case '[':
                    this.term.esc |= EscapeState.ESC_CSI;
                    return false;
                case '#':
                    this.term.esc |= EscapeState.ESC_TEST;
                    return false;
                case '%':
                    this.term.esc |= EscapeState.ESC_UTF8;
                    return false;
                case 'P': /* DCS -- Device Control String */
                case '_': /* APC -- Application Program Command */
                case '^': /* PM -- Privacy Message */
                case ']': /* OSC -- Operating System Command */
                case 'k': /* old title set compatibility */
                    this.StrSequence(ascii);
                    return false;
                case 'n': /* LS2 -- Locking shift 2 */
                case 'o': /* LS3 -- Locking shift 3 */
                    this.term.charset = 2 + (byte)(ascii - 'n');
                    break;
                case '(': /* GZD4 -- set primary charset G0 */
                case ')': /* G1D4 -- set secondary charset G1 */
                case '*': /* G2D4 -- set tertiary charset G2 */
                case '+': /* G3D4 -- set quaternary charset G3 */
                    this.term.icharset = (byte)(ascii - '(');
                    this.term.esc |= EscapeState.ESC_ALTCHARSET;
                    return false;
                case 'D': /* IND -- Linefeed */
                    if (this.term.c.y == this.term.bot)
                        this.ScrollUp(this.term.top, this.term.bot, 1, ScrollMode.SCROLL_SAVEHIST);
                    else
                        this.MoveTo(this.term.c.x, this.term.c.y + 1);

                    break;
                case 'E': /* NEL -- Next line */
                    this.NewLine(1); /* always go to first col */
                    break;
                case 'H': /* HTS -- Horizontal tab stop */
                    this.term.tabs[this.term.c.x] = 1;
                    break;
                case 'M': /* RI -- Reverse index */
                    if (this.term.c.y == this.term.top)
                        this.ScrollDown(this.term.top, 1);
                    else
                        this.MoveTo(this.term.c.x, this.term.c.y - 1);

                    break;
                case 'Z': /* DECID -- Identify Terminal */
                    this.MasterWrite(this.vtiden);
                    break;
                case 'c': /* RIS -- Reset to initial state */
                    this.Reset();
                    break;
                case '=': /* DECPAM -- Application keypad */
                    // xsetmode(1, MODE_APPKEYPAD);
                    break;
                case '>': /* DECPNM -- Normal keypad */
                    // xsetmode(0, MODE_APPKEYPAD);
                    break;
                case '7': /* DECSC -- Save Cursor */
                    this.CursorMovementHandler(CursorMovement.CURSOR_SAVE);
                    break;
                case '8': /* DECRC -- Restore Cursor */
                    this.CursorMovementHandler(CursorMovement.CURSOR_LOAD);
                    break;
                case '\\': /* ST -- String Terminator */
                    if ((this.term.esc & EscapeState.ESC_STR_END) != 0) this.StrHandle();
                    break;
                default:
                    break;
            }

            return true;
        }

        public bool Selected(int x, int y)
        {
            return this.RegionSelected(x, y, x, y);
        }

        private void PutChar(uint u)
        {
            unsafe
            {
                byte* c = stackalloc byte[(int)UTF_SIZ];
                var control = false;
                var len = 0;
                var width = 0;

                control = IsControl(u);

                if (u < 127 || !this.IS_SET(TermMode.MODE_UTF8))
                {
                    c[0] = (byte)u;
                    width = len = 1;
                }
                else
                {
                    len = TermUtf8.utf8encode(u, c);

                    if (!control && (width = wcwidth(u)) == -1)
                        width = 1;
                }

                if ((this.term.esc & EscapeState.ESC_STR) != 0)
                {
                    if (u == '\a' || u == 030 || u == 032 || u == 033 ||
                        ISCONTROLC1(u))
                    {
                        this.term.esc &= ~(EscapeState.ESC_START | EscapeState.ESC_STR);
                        this.term.esc |= EscapeState.ESC_STR_END;
                        goto CheckControlCode;
                    }

                    if (this.strescseq.len + len >= this.strescseq.siz)
                    {
                        /*
                         * Here is a bug in terminals. If the user never sends
                         * some code to stop the str or esc command, then st
                         * will stop responding. But this is better than
                         * silently failing with unknown characters. At least
                         * then users will report back.
                         *
                         * In the case users ever get fixed, here is the code:
                         */
                        /*
                         * term.esc = 0;
                         * strhandle();
                         */
                        if (this.strescseq.siz > (int.MaxValue - UTF_SIZ) / 2)
                            return;
                        this.strescseq.siz *= 2;
                        this.strescseq.buf = XRealloc(ref this.strescseq.buf, this.strescseq.siz);
                    }

                    var ptr = new IntPtr(c);
                    Marshal.Copy(ptr, this.strescseq.buf, this.strescseq.len, len);
                    this.strescseq.len += len;
                    return;
                }

                CheckControlCode:

                if (control)
                {
                    this.ControlCode(u);

                    if (this.term.esc == 0) this.term.lastc = 0;
                    return;
                }
                else if ((this.term.esc & EscapeState.ESC_START) != 0)
                {
                    if ((this.term.esc & EscapeState.ESC_CSI) != 0)
                    {
                        this.csiescseq.buf[this.csiescseq.len++] = (byte)u;
                        if (BETWEEN(u, 0x40, 0x7E) || this.csiescseq.len >= this.csiescseq.buf.Length - 1)
                        {
                            this.term.esc = 0;
                            this.CsiParse();
                            this.CsiHandle();
                        }

                        return;
                    }
                    else if ((this.term.esc & EscapeState.ESC_UTF8) != 0)
                    {
                        this.DefUtf8(u);
                    }
                    else if ((this.term.esc & EscapeState.ESC_ALTCHARSET) != 0)
                    {
                        this.DefTran(u);
                    }
                    else if ((this.term.esc & EscapeState.ESC_TEST) != 0)
                    {
                        this.DecTest(u);
                    }
                    else
                    {
                        if (!this.EscHandle(u))
                            return;
                    }

                    this.term.esc = 0;
                    return;
                }

                /* selected() takes relative coordinates */
                if (this.Selected(this.term.c.x + this.term.scr, this.term.c.y + this.term.scr)) this.ClearSelection();

                ref Glyph g = ref this.term.line[this.term.c.y].glyphs[this.term.c.x];

                if (this.IS_SET(TermMode.MODE_WRAP) && (this.term.c.state & CursorState.CURSOR_WRAPNEXT) != 0)
                {
                    g.mode |= GlyphAttribute.ATTR_WRAP;
                    this.NewLine(1);
                    g = ref this.term.line[this.term.c.y].glyphs[this.term.c.x];
                }

                if (this.IS_SET(TermMode.MODE_INSERT) && this.term.c.x + width < this.term.col)
                    Array.Copy(this.term.line[this.term.c.y].glyphs, this.term.c.x,
                        this.term.line[this.term.c.y].glyphs, this.term.c.x + width,
                        width);

                if (this.term.c.x + width > this.term.col)
                {
                    this.NewLine(1);
                    g = ref this.term.line[this.term.c.y].glyphs[this.term.c.x];
                }

                this.SetChar(u, ref this.term.c.attr, this.term.c.x, this.term.c.y);
                this.term.lastc = u;

                g = ref this.term.line[this.term.c.y].glyphs[this.term.c.x];

                if (width == 2)
                {
                    g.mode |= GlyphAttribute.ATTR_WIDE;

                    if (this.term.c.x + 1 < this.term.col)
                    {
                        ref Glyph g1 = ref this.term.line[this.term.c.y].glyphs[this.term.c.x + 1];
                        if ((g1.mode & GlyphAttribute.ATTR_WIDE) != 0 && this.term.c.x + 2 < this.term.col)
                        {
                            ref Glyph g2 = ref this.term.line[this.term.c.y].glyphs[this.term.c.x + 2];
                            g2.u = ' ';
                            g2.mode &= GlyphAttribute.ATTR_WDUMMY;
                        }

                        g1.u = '\0';
                        g1.mode = GlyphAttribute.ATTR_WDUMMY;
                    }
                }

                if (this.term.c.x + width < this.term.col)
                {
                    this.MoveTo(this.term.c.x + width, this.term.c.y);
                }
                else
                {
                    this.term.wrapcwidth[this.IS_SET(TermMode.MODE_ALTSCREEN) ? 1 : 0] = width;
                    this.term.c.state |= CursorState.CURSOR_WRAPNEXT;
                }
            }
        }

        private void NewLine(int firstCol, int amount = 1)
        {
            int y = this.term.c.y;
            if (y + amount > this.term.bot)
                this.ScrollUp(this.term.top, this.term.bot, 1, ScrollMode.SCROLL_SAVEHIST);
            else
                y += amount;

            this.MoveTo(firstCol == 1 ? 0 : this.term.c.x, y);
        }

        private void MoveTo(int x, int y)
        {
            var miny = 0;
            var maxy = 0;

            if ((this.term.c.state & CursorState.CURSOR_ORIGIN) != 0)
            {
                miny = this.term.top;
                maxy = this.term.bot;
            }
            else
            {
                miny = 0;
                maxy = this.term.row - 1;
            }

            this.term.c.state &= ~CursorState.CURSOR_WRAPNEXT;
            this.term.c.x = Math.Clamp(x, 0, this.term.col - 1);
            this.term.c.y = Math.Clamp(y, miny, maxy);

            this.ptyOptions.CursorLeft = this.term.c.x;
            this.ptyOptions.CursorTop = this.term.c.y;
        }

        private void RegionScrollDown(int n)
        {
            if (this.term.scr == 0 || this.IS_SET(TermMode.MODE_ALTSCREEN))
                return;

            if (n < 0)
                n = Math.Max(this.term.row / -n, 1);

            if (n <= this.term.scr)
            {
                this.term.scr -= n;
            }
            else
            {
                n = this.term.scr;
                this.term.scr = 0;
            }

            if (this.sel.ob.x != -1 && !this.sel.alt) this.SelectionMove(-n); /* negate change in term.scr */
            this.SetDirtyFull();
        }

        private void RegionScrollUp(int n)
        {
            if (this.term.histf == 0 || this.IS_SET(TermMode.MODE_ALTSCREEN))
                return;

            if (n < 0)
                n = Math.Max(this.term.row / -n, 1);

            if (this.term.scr + n <= this.term.histf)
            {
                this.term.scr += n;
            }
            else
            {
                n = this.term.histf - this.term.scr;
                this.term.scr = this.term.histf;
            }

            if (this.sel.ob.x != -1 && !this.sel.alt) this.SelectionMove(n); /* negate change in term.scr */
            this.SetDirtyFull();
        }

        private void Reflow(int col, int row)
        {
            var i = 0;
            var j = 0;
            var oce = 0;
            var nce = 0;
            var bot = 0;
            var scr = 0;
            var ox = 0;
            int oy = -this.term.histf;
            var nx = 0;
            int ny = -1;
            var len = 0;

            int cy = -1; /* proxy for new y coordinate of cursor */
            var nlines = 0;
            Data.Line[] buf = Array.Empty<Data.Line>();
            var line = default(Data.Line);

            /* y coordinate of cursor line end */
            for (oce = this.term.c.y;
                 oce < this.term.row - 1 && this.IsWrapped(ref this.term.line[oce]);
                 oce++) ;

            nlines = this.term.histf + oce + 1;
            if (col < this.term.col)
            {
                /* each line can take this many lines after reflow */
                j = (this.term.col + col - 1) / col;
                nlines = j * nlines;
                if (nlines > HISTSIZE + RESIZEBUFFER + row)
                {
                    nlines = HISTSIZE + RESIZEBUFFER + row;
                    oy = -(nlines / j - oce - 1);
                }
            }

            buf = XRealloc(ref buf, nlines);

            do
            {
                if (nx == 0)
                    buf[++ny].glyphs = new Glyph[col];
                if (ox == 0)
                {
                    line = this.TLINEABS(oy);
                    len = this.LineLength(ref line);
                }

                if (oy == this.term.c.y)
                {
                    if (ox == 0)
                        len = Math.Max(len, this.term.c.x + 1);

                    /* update cursor */
                    if (cy < 0 && this.term.c.x - ox < col - nx)
                    {
                        this.term.c.x = nx + this.term.c.x - ox;
                        cy = ny;
                        this.UPDATEWRAPNEXT(false, col);
                    }
                }

                /* get reflowed lines in buf */
                if (col - nx > len - ox)
                {
                    Array.Copy(line.glyphs, ox, buf[ny].glyphs, nx, len - ox);
                    nx += len - ox;
                    if (len == 0 || (line.glyphs[len - 1].mode & GlyphAttribute.ATTR_WRAP) == 0)
                    {
                        for (j = nx; j < col; j++) this.ClearGlyph(ref buf[ny].glyphs[j], false);
                        nx = 0;
                    }
                    else if (nx > 0)
                    {
                        buf[ny].glyphs[nx - 1].mode &= ~GlyphAttribute.ATTR_WRAP;
                    }

                    ox = 0;
                    oy++;
                }
                else if (col - nx == len - ox)
                {
                    Array.Copy(line.glyphs, ox, buf[ny].glyphs, nx, col - nx);
                    ox = 0;
                    oy++;
                    nx = 0;
                }
                else /* if (col - nx < len - ox) */
                {
                    // fucking memcpy I swear
                    Array.Copy(line.glyphs, ox, buf[ny].glyphs, nx, col - nx);
                    ox += col - nx;
                    buf[ny].glyphs[col - 1].mode |= GlyphAttribute.ATTR_WRAP;
                    nx = 0;
                }
            } while (oy <= oce);

            if (nx != 0)
                for (j = nx; j < col; j++)
                    this.ClearGlyph(ref buf[ny].glyphs[j], false);

            /* free extra lines */
            for (i = row; i < this.term.row; i++) this.term.line[i].glyphs = null;
            /* resize to new height */
            this.term.line = XRealloc(ref this.term.line, row);

            bot = Math.Min(ny, row - 1);
            scr = Math.Max(row - this.term.row, 0);

            /* update y coordinate of cursor line end */
            nce = Math.Min(oce + scr, bot);

            /* update cursor y coordinate */
            this.term.c.y = nce - (ny - cy);

            if (this.term.c.y < 0)
            {
                j = nce;
                nce = Math.Min(nce + -this.term.c.y, bot);
                this.term.c.y += nce - j;
                while (this.term.c.y < 0)
                {
                    buf[ny--].glyphs = null;
                    this.term.c.y++;
                }
            }

            /* allocate new rows */
            for (i = row - 1; i > nce; i--)
            {
                this.term.line[i].glyphs = new Glyph[col];
                for (j = 0; j < col; j++) this.ClearGlyph(ref this.term.line[i].glyphs[j], false);
            }

            /* fill visible area */
            for ( /*i = nce */; i >= this.term.row; i--, ny--) this.term.line[i].glyphs = buf[ny].glyphs;

            for ( /*i = term.row - 1 */; i >= 0; i--, ny--)
            {
                this.term.line[i].glyphs = null;
                this.term.line[i].glyphs = buf[ny].glyphs;
            }

            /* fill lines in history buffer and update term.histf */
            for ( /*i = -1 */; ny >= 0 && i >= -HISTSIZE; i--, ny--)
            {
                j = (this.term.histi + i + 1 + HISTSIZE) % HISTSIZE;
                this.term.hist[j].glyphs = null;
                this.term.hist[j].glyphs = buf[ny].glyphs;
            }

            this.term.histf = -i - 1;
            this.term.scr = Math.Min(this.term.scr, this.term.histf);
            /* resize rest of the history lines */
            for ( /*i = -term.histf - 1 */; i >= -HISTSIZE; i--)
            {
                j = (this.term.histi + i + 1 + HISTSIZE) % HISTSIZE;
                this.term.hist[j].glyphs = XRealloc(ref this.term.hist[j].glyphs, col);
            }
        }

        private void ReflowScrollDown(int n)
        {
            var i = 0;
            Data.Line temp;

            /* can never be true as of now
            if (IS_SET(MODE_ALTSCREEN))
                return; */

            if ((n = Math.Min(n, this.term.histf)) <= 0)
                return;

            for (i = this.term.c.y + n; i >= n; i--)
            {
                temp = this.term.line[i];
                this.term.line[i] = this.term.line[i - n];
                this.term.line[i - n] = temp;
            }

            for ( /*i = n - 1 */; i >= 0; i--)
            {
                temp = this.term.line[i];
                this.term.line[i] = this.term.hist[this.term.histi];
                this.term.hist[this.term.histi] = temp;
                this.term.histi = (this.term.histi - 1 + HISTSIZE) % HISTSIZE;
            }

            this.term.c.y += n;
            this.term.histf -= n;
            if ((i = this.term.scr - n) >= 0)
            {
                this.term.scr = i;
            }
            else
            {
                this.term.scr = 0;
                if (this.sel.ob.x != -1 && !this.sel.alt) this.SelectionMove(-i);
            }
        }

        private void ResizeDef(int col, int row)
        {
            var i = 0;
            var j = 0;

            /* return if dimensions haven't changed */
            if (this.term.col == col && this.term.row == row)
            {
                this.SetDirtyFull();
                return;
            }

            if (col != this.term.col)
            {
                if (!this.sel.alt) this.SelectionRemove();
                this.Reflow(col, row);
            }
            else
            {
                /* slide screen up if otherwise cursor would get out of the screen */
                if (this.term.c.y >= row)
                {
                    this.ScrollUp(0, this.term.row - 1, this.term.c.y - row + 1, ScrollMode.SCROLL_RESIZE);
                    this.term.c.y = row - 1;
                }

                for (i = row; i < this.term.row; i++) this.term.line[i].glyphs = null;

                /* resize to new height */
                this.term.line = XRealloc(ref this.term.line, row);

                /* allocate any new rows */
                for (i = this.term.row; i < row; i++)
                {
                    this.term.line[i].glyphs = new Glyph[col];
                    for (j = 0; j < col; j++) this.ClearGlyph(ref this.term.line[i].glyphs[j], false);
                }

                /* scroll down as much as height has increased */
                this.ReflowScrollDown(row - this.term.row);
            }

            /* update terminal size */
            this.term.col = col;
            this.term.row = row;

            /* reset scrolling region */
            this.term.top = 0;
            this.term.bot = row - 1;

            /* dirty all lines */
            this.SetDirtyFull();
        }

        private void ResizeAlt(int col, int row)
        {
            var i = 0;
            var j = 0;

            /* return if dimensions haven't changed */
            if (this.term.col == col && this.term.row == row)
            {
                this.SetDirtyFull();
                return;
            }

            if (this.sel.alt) this.SelectionRemove();

            /* slide screen up if otherwise cursor would get out of the screen */
            for (i = 0; i <= this.term.c.y - row; i++) this.term.line[i].glyphs = null;

            if (i > 0)
            {
                /* ensure that both src and dst are not NULL */
                Array.Copy(this.term.line, i, this.term.line, 0, row);
                this.term.c.y = row - 1;
            }

            for (i += row; i < this.term.row; i++) this.term.line[i].glyphs = null;

            /* resize to new height */
            this.term.line = XRealloc(ref this.term.line, row);

            /* resize to new width */
            for (i = 0; i < Math.Min(row, this.term.row); i++)
            {
                this.term.line[i].glyphs = XRealloc(ref this.term.line[i].glyphs, col);
                for (j = this.term.col; j < col; j++) this.ClearGlyph(ref this.term.line[i].glyphs[j], false);
            }

            /* allocate any new rows */
            for ( /*i = MIN(row, term.row) */; i < row; i++)
            {
                this.term.line[i].glyphs = new Glyph[col];
                for (j = 0; j < col; j++) this.ClearGlyph(ref this.term.line[i].glyphs[j], false);
            }

            /* update cursor */
            if (this.term.c.x >= col)
            {
                this.term.c.state &= ~CursorState.CURSOR_WRAPNEXT;
                this.term.c.x = col - 1;
            }
            else
            {
                this.UPDATEWRAPNEXT(true, col);
            }

            /* update terminal size */
            this.term.col = col;
            this.term.row = row;

            /* reset scrolling region */
            this.term.top = 0;
            this.term.bot = row - 1;
            /* dirty all lines */
            this.SetDirtyFull();
        }

        private void LoadDefScreen(bool clear, bool loadcursor)
        {
            var col = 0;
            var row = 0;
            bool alt = this.IS_SET(TermMode.MODE_ALTSCREEN);

            if (alt)
            {
                if (clear) this.ClearRegion(0, 0, this.term.col - 1, this.term.row - 1, true);
                col = this.term.col;
                row = this.term.row;
                this.SwapScreen();
            }

            if (loadcursor) this.CursorMovementHandler(CursorMovement.CURSOR_LOAD);
            if (alt) this.ResizeDef(col, row);
        }

        private void LoadAltScreen(bool clear, bool savecursor)
        {
            var col = 0;
            var row = 0;
            bool def = !this.IS_SET(TermMode.MODE_ALTSCREEN);

            if (savecursor) this.CursorMovementHandler(CursorMovement.CURSOR_SAVE);

            if (def)
            {
                col = this.term.col;
                row = this.term.row;
                this.SwapScreen();
                this.term.scr = 0;
                this.ResizeAlt(col, row);
            }

            if (clear) this.ClearRegion(0, 0, this.term.col - 1, this.term.row - 1, true);
        }

        private bool IsAltScreen()
        {
            return this.IS_SET(TermMode.MODE_ALTSCREEN);
        }

        private void ScrollDown(int top, int n)
        {
            var i = 0;
            int bot = this.term.bot;
            Data.Line temp = default;

            if (n <= 0)
                return;
            n = Math.Min(n, bot - top + 1);

            this.SetDirty(top, bot - n);
            this.ClearRegion(0, bot - n + 1, this.term.col - 1, bot, true);

            for (i = bot; i >= top + n; i--)
            {
                temp = this.term.line[i];
                this.term.line[i] = this.term.line[i - n];
                this.term.line[i - n] = temp;
            }

            if (this.sel.ob.x != -1 && this.sel.alt == this.IS_SET(TermMode.MODE_ALTSCREEN))
                this.SelectionScroll(top, bot, n);
        }

        private void ScrollUp(int top, int bot, int n, ScrollMode mode)
        {
            var i = 0;
            var j = 0;
            var s = 0;
            bool alt = this.IS_SET(TermMode.MODE_ALTSCREEN);
            bool savehist = !alt && top == 0 && mode != ScrollMode.SCROLL_NOSAVEHIST;

            if (n <= 0)
                return;
            n = Math.Min(n, bot - top + 1);

            if (this.term.scr > 0 && this.term.scr < HISTSIZE)
                this.term.scr = Math.Min(this.term.scr + n, HISTSIZE - 1);

            // SD bugfix: cursor not updating properly
            ref Glyph cursorGlyph = ref TLINEABS(this.term.c.y).glyphs[this.term.c.x];
            if ((cursorGlyph.mode & GlyphAttribute.ATTR_REVERSE) != 0)
            {
                cursorGlyph.mode &= ~GlyphAttribute.ATTR_REVERSE;
                this.term.dirty[this.term.c.y] = 1;
            }
            
            if (savehist)
            {
                for (i = 0; i < n; i++)
                {
                    this.term.histi = (this.term.histi + 1) % HISTSIZE;
                    Data.Line temp = this.term.hist[this.term.histi];
                    for (j = 0; j < this.term.col; j++) this.ClearGlyph(ref temp.glyphs[j], true);
                    this.term.hist[this.term.histi] = this.term.line[i];
                    this.term.line[i] = temp;
                }

                this.term.histf = Math.Min(this.term.histf + n, HISTSIZE);
                s = n;
                if (this.term.scr != 0)
                {
                    j = this.term.scr;
                    this.term.scr = Math.Min(j + n, HISTSIZE);
                    s = j + n - this.term.scr;
                }

                if (mode != ScrollMode.SCROLL_RESIZE) this.SetDirtyFull();
            }
            else
            {
                this.ClearRegion(0, top, this.term.col - 1, top + n - 1, true);
                this.SetDirty(top + n, bot);
            }

            for (i = top; i <= bot - n; i++)
            {
                ref Data.Line temp = ref this.term.line[i];
                ref Data.Line temp2 = ref this.term.line[i + n];
                (temp2.glyphs, temp.glyphs) = (temp.glyphs, temp2.glyphs);
            }

            if (this.sel.ob.x != -1 && this.sel.alt == alt)
            {
                if (!savehist)
                {
                    this.SelectionScroll(top, bot, -n);
                }
                else if (s > 0)
                {
                    this.SelectionMove(-s);
                    if (-this.term.scr + this.sel.nb.y < -this.term.histf) this.SelectionRemove();
                }
            }
        }

        private void SelectionMove(int n)
        {
            this.sel.ob.y += n;
            this.sel.nb.y += n;
            this.sel.oe.y += n;
            this.sel.ne.y += n;
        }

        private void SelectionRemove()
        {
            this.sel.mode = SelectionMode.SEL_IDLE;
            this.sel.ob.x = -1;
        }

        private bool RegionSelected(int x1, int y1, int x2, int y2)
        {
            if (this.sel.ob.x == -1 || this.sel.mode == SelectionMode.SEL_EMPTY ||
                this.sel.alt != this.IS_SET(TermMode.MODE_ALTSCREEN) || this.sel.nb.y > y2 || this.sel.ne.y < y1)
                return false;

            return this.sel.type == Data.SelectionType.SEL_RECTANGULAR
                ? this.sel.nb.x <= x2 && this.sel.ne.x >= x1
                : (this.sel.nb.y != y2 || this.sel.nb.x <= x2) &&
                  (this.sel.ne.y != y1 || this.sel.ne.x >= x1);
        }

        private void SelectionScroll(int top, int bot, int n)
        {
            /* turn absolute coordinates into relative */
            top += this.term.scr;
            bot += this.term.scr;

            if (BETWEEN(this.sel.nb.y, top, bot) != BETWEEN(this.sel.ne.y, top, bot))
            {
                this.ClearSelection();
            }
            else if (BETWEEN(this.sel.nb.y, top, bot))
            {
                this.SelectionMove(n);
                if (this.sel.nb.y < top || this.sel.ne.y > bot) this.ClearSelection();
            }
        }

        private void ClearSelection()
        {
            if (this.sel.ob.x == -1)
                return;
            this.SelectionRemove();
            this.SetDirty(this.sel.nb.y, this.sel.ne.y);
        }

        private void ExtendSelection(int col, int row, Data.SelectionType type, bool done)
        {
            var oldey = 0;
            var oldex = 0;
            var oldsby = 0;
            var oldsey = 0;
            var oldtype = default(Data.SelectionType);

            if (this.sel.mode == SelectionMode.SEL_IDLE)
                return;
            if (done && this.sel.mode == SelectionMode.SEL_EMPTY)
            {
                this.ClearSelection();
                return;
            }

            oldey = this.sel.oe.y;
            oldex = this.sel.oe.x;
            oldsby = this.sel.nb.y;
            oldsey = this.sel.ne.y;
            oldtype = this.sel.type;

            this.sel.oe.x = col;
            this.sel.oe.y = row;
            this.sel.type = type;
            this.NormalizeSelection();

            if (oldey != this.sel.oe.y || oldex != this.sel.oe.x ||
                oldtype != this.sel.type || this.sel.mode == SelectionMode.SEL_EMPTY)
                this.SetDirty(Math.Min(this.sel.nb.y, oldsby), Math.Max(this.sel.ne.y, oldsey));

            this.sel.mode = done ? SelectionMode.SEL_IDLE : SelectionMode.SEL_READY;
        }

        private void StartSelection(int col, int row, SelectionSnap snap)
        {
            this.ClearSelection();
            this.sel.mode = SelectionMode.SEL_EMPTY;
            this.sel.type = Data.SelectionType.SEL_REGULAR;
            this.sel.alt = this.IS_SET(TermMode.MODE_ALTSCREEN);
            this.sel.snap = snap;
            this.sel.oe.x = this.sel.ob.x = col;
            this.sel.oe.y = this.sel.ob.y = row;
            this.NormalizeSelection();

            if (this.sel.snap != 0) this.sel.mode = SelectionMode.SEL_READY;
            this.SetDirty(this.sel.nb.y, this.sel.ne.y);
        }

        private void SnapSelection(ref int x, ref int y, int direction)
        {
            var newx = 0;
            var newy = 0;
            var xt = 0;
            var yt = 0;
            var rtop = 0;
            int rbot = this.term.row - 1;
            var delim = false;
            var prevdelim = false;

            ref Glyph prevgp = ref this.term.line[0].glyphs[0];
            ref Glyph gp = ref this.term.line[0].glyphs[0];

            if (!this.IS_SET(TermMode.MODE_ALTSCREEN))
            {
                rtop += -this.term.histf + this.term.scr;
                rbot += this.term.scr;
            }

            switch (this.sel.snap)
            {
                case SelectionSnap.SNAP_WORD:
                    /*
                     * Snap around if the word wraps around at the end or
                     * beginning of a line.
                     */
                    prevgp = ref this.TLINE(y).glyphs[x];
                    prevdelim = this.ISDELIM(prevgp.u);
                    for (;;)
                    {
                        newx = x + direction;
                        newy = y;
                        if (!BETWEEN(newx, 0, this.term.col - 1))
                        {
                            newy += direction;
                            newx = (newx + this.term.col) % this.term.col;
                            if (!BETWEEN(newy, rtop, rbot))
                                break;

                            if (direction > 0)
                            {
                                yt = y;
                                xt = x;
                            }
                            else
                            {
                                yt = newy;
                                xt = newx;
                            }

                            if ((this.TLINE(yt).glyphs[xt].mode & GlyphAttribute.ATTR_WRAP) == 0)
                                break;
                        }

                        if (newx >= this.LineLength(ref this.TLINE(newy)))
                            break;

                        gp = ref this.TLINE(newy).glyphs[newx];
                        delim = this.ISDELIM(gp.u);
                        if ((gp.mode & GlyphAttribute.ATTR_WDUMMY) == 0 && (delim != prevdelim ||
                                                                            (delim && !(gp.u == ' ' &&
                                                                                prevgp.u == ' '))))
                            break;

                        x = newx;
                        y = newy;
                        prevgp = gp;
                        prevdelim = delim;
                    }

                    break;
                case SelectionSnap.SNAP_LINE:
                    /*
                     * Snap around if the the previous line or the current one
                     * has set ATTR_WRAP at its end. Then the whole next or
                     * previous line will be selected.
                     */
                    x = direction < 0 ? 0 : this.term.col - 1;
                    if (direction < 0)
                    {
                        for (; y > rtop; y -= 1)
                            if (!this.IsWrapped(ref this.TLINE(y - 1)))
                                break;
                    }
                    else if (direction > 0)
                    {
                        for (; y < rbot; y += 1)
                            if (!this.IsWrapped(ref this.TLINE(y)))
                                break;
                    }

                    break;
            }
        }

        private void NormalizeSelection()
        {
            int i;

            if (this.sel.type == Data.SelectionType.SEL_REGULAR && this.sel.ob.y != this.sel.oe.y)
            {
                this.sel.nb.x = this.sel.ob.y < this.sel.oe.y ? this.sel.ob.x : this.sel.oe.x;
                this.sel.ne.x = this.sel.ob.y < this.sel.oe.y ? this.sel.oe.x : this.sel.ob.x;
            }
            else
            {
                this.sel.nb.x = Math.Min(this.sel.ob.x, this.sel.oe.x);
                this.sel.ne.x = Math.Max(this.sel.ob.x, this.sel.oe.x);
            }

            this.sel.nb.y = Math.Min(this.sel.ob.y, this.sel.oe.y);
            this.sel.ne.y = Math.Max(this.sel.ob.y, this.sel.oe.y);

            this.SnapSelection(ref this.sel.nb.x, ref this.sel.nb.y, -1);
            this.SnapSelection(ref this.sel.ne.x, ref this.sel.ne.y, +1);

            /* expand selection over line breaks */
            if (this.sel.type == Data.SelectionType.SEL_RECTANGULAR)
                return;

            i = this.LineLength(ref this.TLINE(this.sel.nb.y));
            if (this.sel.nb.x > i) this.sel.nb.x = i;
            if (this.sel.ne.x >= this.LineLength(ref this.TLINE(this.sel.ne.y))) this.sel.ne.x = this.term.col - 1;
        }

        private bool IsWrapped(ref Data.Line line)
        {
            int len = this.LineLength(ref line);

            return len > 0 && (line.glyphs[len - 1].mode & GlyphAttribute.ATTR_WRAP) != 0;
        }

        private string GetGlyphs(ref Data.Line line, int gp, int lgp)
        {
            var sb = new StringBuilder();

            while (gp <= lgp)
            {
                ref Glyph glyph = ref line.glyphs[gp];

                if ((glyph.mode & GlyphAttribute.ATTR_WDUMMY) != 0)
                {
                    gp++;
                }
                else
                {
                    sb.Append((char)glyph.u);
                    gp++;
                }
            }

            return sb.ToString();
        }

        private string GetLine(ref Data.Line line)
        {
            var fgp = 0;
            int lgp = this.term.col - 1;

            while (lgp > fgp && (line.glyphs[lgp].mode & (GlyphAttribute.ATTR_SET | GlyphAttribute.ATTR_WRAP)) == 0)
                lgp--;
            string str = this.GetGlyphs(ref line, fgp, lgp);
            if ((line.glyphs[lgp].mode & GlyphAttribute.ATTR_WRAP) == 0)
                str += "\n";
            return str;
        }


        private int LineLength(ref Data.Line line)
        {
            int i = this.term.col - 1;

            for (; i >= 0 && (line.glyphs[i].mode & (GlyphAttribute.ATTR_SET | GlyphAttribute.ATTR_WRAP)) == 0; i--) ;
            return i + 1;
        }

        private void SetDirty(int top, int bot)
        {
            int i;

            top = Math.Clamp(top, 0, this.term.row - 1);
            bot = Math.Clamp(bot, 0, this.term.row - 1);

            for (i = top; i <= bot; i++) this.term.dirty[i] = 1;
        }

        private void ClearGlyph(ref Glyph gp, bool usecurattr)
        {
            if (usecurattr)
            {
                gp.fg = this.term.c.attr.fg;
                gp.bg = this.term.c.attr.bg;
                gp.fgRgb = this.term.c.attr.fgRgb;
            }
            else
            {
                gp.fg = defaultfg;
                gp.bg = defaultbg;
                gp.fgRgb = default;
            }

            gp.mode = GlyphAttribute.ATTR_NULL;
            gp.u = ' ';
        }

        private void ResetCursor()
        {
            this.term.c.attr.mode = GlyphAttribute.ATTR_NULL;
            this.term.c.attr.fg = defaultfg;
            this.term.c.attr.bg = defaultbg;
            this.term.c.attr.fgRgb = default;
            this.term.c.x = 0;
            this.term.c.y = 0;
            this.term.c.state = CursorState.CURSOR_DEFAULT;
        }

        private void ClearRegion(int x1, int y1, int x2, int y2, bool usecurattr)
        {
            var x = 0;
            var y = 0;

            /* regionselected() takes relative coordinates */
            if (this.RegionSelected(x1 + this.term.scr, y1 + this.term.scr, x2 + this.term.scr, y2 + this.term.scr))
                this.SelectionRemove();

            for (y = y1; y <= y2; y++)
            {
                this.term.dirty[y] = 1;
                for (x = x1; x <= x2; x++) this.ClearGlyph(ref this.term.line[y].glyphs[x], usecurattr);
            }
        }

        private void TtyInit(int colCount, int rowCount)
        {
            Debug.Log("Initializing pseudoterminal");

            // Enforce CRLF
            this.ptyOptions.LFlag = 0;

            // Control codes
            this.ptyOptions.C_cc[PtyConstants.VERASE] = (byte)'\b';
            this.ptyOptions.ColumnCount = colCount;
            this.ptyOptions.RowCount = rowCount;

            PseudoTerminal.CreatePair(out this.master, out this.slave, this.ptyOptions);
        }

        private int TtyRead()
        {
            if (this.master is null)
                return 0;

            var ret = 0;
            var written = 0;

            ret = this.master.Read(this.ttybuf, this.ttybuflen, this.ttybuf.Length - this.ttybuflen);

            if (ret > 0)
            {
                this.ttybuflen += ret;
                written = this.TerminalWrite(this.ttybuf, this.ttybuflen, false);
                this.ttybuflen -= written;

                if (this.ttybuflen > 0) Buffer.BlockCopy(this.ttybuf, 0, this.ttybuf, written, this.ttybuflen);
            }

            return ret;
        }

        private int TerminalWrite(byte[] buf, int buflen, bool showControl)
        {
            var charsize = 0;
            var u = 0u;
            var n = 0;

            for (n = 0; n < buflen; n += charsize)
            {
                if (this.IS_SET(TermMode.MODE_UTF8))
                {
                    charsize = TermUtf8.utf8decode(buf, n, out u, buflen - n);
                    if (charsize == 0)
                        break;
                }
                else
                {
                    u = (uint)(buf[n] & 0xff);
                    charsize = 1;
                }

                if (showControl && IsControl(u))
                {
                    if ((u & 0x80) != 0)
                    {
                        u &= 0x7f;
                        this.PutChar('^');
                        this.PutChar('[');
                    }
                    else if (u != '\n' && u != '\r' && u != '\t')
                    {
                        u ^= 0x40;
                        this.PutChar('^');
                    }
                }

                this.PutChar(u);
            }

            return n;
        }

        public T[] XMalloc<T>(int size)
        {
            return new T[size];
        }

        public static T[] XRealloc<T>(ref T[] array, int size)
        {
            try
            {
                if (array is null)
                    array = new T[size];
                else if (array.Length != size)
                    Array.Resize(ref array, size);
            }
            catch (OutOfMemoryException)
            {
                Debug.LogError("Whoa whoa whoa! Something needs to be restitched immediately. We ran out of RAM.");
            }

            return array;
        }

        public bool IS_SET(TermMode mode)
        {
            return (this.term.mode & mode) != 0;
        }

        public static int wcwidth(uint u)
        {
            // TODO
            return -1;
        }

        public static bool BETWEEN(int x, int a, int b)
        {
            return x >= a && x <= b;
        }

        public static bool BETWEEN(uint x, uint a, uint b)
        {
            return x >= a && x <= b;
        }

        public static bool ISCONTROLC0(uint c)
        {
            return BETWEEN(c, 0, 0x1f) || c == 0x7f;
        }

        public static bool ISCONTROLC1(uint c)
        {
            return BETWEEN(c, 0x80, 0x9f);
        }

        public static bool IsControl(uint c)
        {
            return ISCONTROLC0(c) || ISCONTROLC1(c);
        }

        public ITextConsole StartSession()
        {
            this.TtyInit(this.columnCount, this.rowCount);

            this.TerminalNew(this.columnCount, this.rowCount);

            return new SimpleTerminalSession(this.slave);
        }

        private ref Data.Line TLINE(int y)
        {
            return ref y < this.term.scr
                ? ref this.term.hist[(this.term.histi + y - this.term.scr + 1 + HISTSIZE) % HISTSIZE]
                : ref this.term.line[y - this.term.scr];
        }

        private ref Data.Line TLINEABS(int y)
        {
            return ref y < 0
                ? ref this.term.hist[(this.term.histi + y + 1 + HISTSIZE) % HISTSIZE]
                : ref this.term.line[y];
        }

        private void UPDATEWRAPNEXT(bool alt, int col)
        {
            if ((this.term.c.state & CursorState.CURSOR_WRAPNEXT) != 0 &&
                this.term.c.x + this.term.wrapcwidth[alt ? 1 : 0] < col)
            {
                this.term.c.x += this.term.wrapcwidth[alt ? 1 : 0];
                this.term.c.state &= ~CursorState.CURSOR_WRAPNEXT;
            }
        }

        public void OnSelect(BaseEventData eventData)
        {
            this.isFocused = true;
        }

        public void OnDeselect(BaseEventData eventData)
        {
            this.isFocused = false;
        }

        private int ColumnAt(float x)
        {
            return (int)Math.Floor(x / this.CharacterWidth);
        }

        private int RowAt(float y)
        {
            return (term.row - 1) - (int)Math.Floor(y / this.LineHeight);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (EventSystem.current == null)
                return;
            
            EventSystem.current.SetSelectedGameObject(this.gameObject);

            if (eventData.button == PointerEventData.InputButton.Left)
            {
                SelectionSnap snap = SelectionSnap.NONE;

                float clickDelta = this.now - this.lastClick;
                float doubleClickDelta = this.now - this.earlierClick;

                if (doubleClickDelta <= this.trippleClickTime)
                    snap = SelectionSnap.SNAP_LINE;
                else if (clickDelta <= this.doubleClickTime)
                    snap = SelectionSnap.SNAP_WORD;

                this.earlierClick = this.lastClick;
                this.lastClick = this.now;

                RectTransformUtility.ScreenPointToLocalPointInRectangle(this.rectTransform, eventData.position, null, out Vector2 localPosition);

                if (localPosition.x < 0 || localPosition.y < 0)
                    return;

                float x = Math.Abs(localPosition.x);
                float y = Math.Abs(localPosition.y);

                this.StartSelection(this.ColumnAt(x), this.RowAt(y), snap);
                this.isSelecting = true;
            }

            if (eventData.button == PointerEventData.InputButton.Right)
            {
                string selection = this.GetSelection();

                // if the selection is empty then we Paste and write the contents
                // of the clipboard to the master device. If the selection has any text,
                // even whitespace, we clear the selection after copying the text to the
                // clipboard.
                if (string.IsNullOrWhiteSpace(selection))
                {
                    string clipboardText = PlatformHelper.GetClipboardText();
                    if (!string.IsNullOrEmpty(clipboardText))
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(clipboardText);
                        this.master.Write(bytes, 0, bytes.Length);
                    }
                }
                else
                {
                    PlatformHelper.SetClipboardText(selection);
                    this.ClearSelection();
                }
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left)
            {
                this.isSelecting = false;

                RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, eventData.position, null, out Vector2 localPosition);

                if (localPosition.x < 0 || localPosition.y < 0)
                    return;

                float x = Math.Abs(localPosition.x);
                float y = Math.Abs(localPosition.y);

                this.ExtendSelection(this.ColumnAt(x), this.RowAt(y), Data.SelectionType.SEL_REGULAR, !this.isSelecting);
            }
        }

         public void OnUpdateSelected(BaseEventData eventData)
        {
            if (!this.isFocused)
                return;

            var ev = new Event();
            while (Event.PopEvent(ev))
                if (ev.rawType == EventType.KeyDown)
                {
                    // Force redraw on keyboard events, just like st does
                    if (!this.drawing)
                    {
                        this.trigger = this.now;
                        this.drawing = true;
                    }

                    bool control = (ev.modifiers & (EventModifiers.Control | EventModifiers.Command)) != 0;
                    bool alt = (ev.modifiers & EventModifiers.Alt) != 0;
                    bool shift = (ev.modifiers & EventModifiers.Shift) != 0;
                    bool modifiers = control
                                     || alt
                                     || shift;

                    switch (ev.keyCode)
                    {
                        case KeyCode.PageUp when !modifiers:
                            this.RegionScrollUp(-1);
                            break;
                        case KeyCode.PageDown when !modifiers:
                            this.RegionScrollDown(-1);
                            break;

                        case KeyCode.Delete when !modifiers:
                            this.master.WriteByte(0x7f);
                            break;
                        case KeyCode.UpArrow when !modifiers:
                            this.MasterWrite("\x1b[1A");
                            break;
                        case KeyCode.DownArrow when !modifiers:
                            this.MasterWrite("\x1b[1B");
                            break;
                        case KeyCode.RightArrow when !modifiers:
                            this.MasterWrite("\x1b[1C");
                            break;
                        case KeyCode.LeftArrow when !modifiers:
                            this.MasterWrite("\x1b[1D");
                            break;
                        case KeyCode.End when !modifiers:
                            this.MasterWrite("\x1b[4~");
                            break;
                        case KeyCode.Home when !modifiers:
                            this.MasterWrite("\x1b[1~");
                            break;
                        case KeyCode.Backspace when !modifiers:
                            this.MasterWrite("\b");
                            break;
                        case KeyCode.Return when !modifiers :
                        case KeyCode.KeypadEnter when !modifiers:
                            this.MasterWrite("\r\n");
                            break;
                        case { } when ev.character != '\0':
                            char ch = ev.character;
                            if (ch < 127 || !this.IS_SET(TermMode.MODE_UTF8))
                            {
                                this.master.WriteByte((byte)ch);
                            }
                            else
                            {
                                var buf = new byte[UTF_SIZ];
                                unsafe
                                {
                                    fixed (byte* c = buf)
                                    {
                                        TermUtf8.utf8encode(ch, c);
                                    }
                                }

                                this.master.Write(buf, 0, buf.Length);
                            }

                            break;
                    }
                }

            eventData.Use();
        }
        
        private Vector2 CalculateCharSize(char ch, TMP_FontAsset font, int fontSize, FontStyles style = FontStyles.Normal)
        {
            float fs = fontSize;
    
            // Compute scale of the target point size relative to the sampling point size of the font asset.
            float pointSizeScale = fs / (font.faceInfo.pointSize * font.faceInfo.scale);
            float emScale = fs * 0.01f;

            float height = ((font.faceInfo.lineHeight - font.faceInfo.underlineOffset) * emScale) /
                   this.transform.lossyScale.y;
            
            

            float styleSpacingAdjustment = (style & FontStyles.Bold) == FontStyles.Bold ? font.boldSpacing : 0;
            float normalSpacingAdjustment = font.normalSpacingOffset;

            // Make sure the given unicode exists in the font asset.
            font.TryAddCharacters(ch.ToString());
            if (!font.characterLookupTable.TryGetValue(ch, out TMP_Character character))
                character = font.characterLookupTable['?'];
            float width = character.glyph.metrics.horizontalAdvance * pointSizeScale +
                          (styleSpacingAdjustment + normalSpacingAdjustment) * emScale;

            return new Vector2(width, height);
        }
        
        public float TextWidthApproximation(char unicode, TMP_FontAsset fontAsset, int size)
        {
            return CalculateCharSize(unicode, fontAsset, size).x;
        }
        
        private void MasterWrite(string str)
        {
            this.RegionScrollDown(this.term.scr);
            byte[] enc = Encoding.UTF8.GetBytes(str);
            this.master.Write(enc, 0, enc.Length);
        }

        public static unsafe long strtol(byte* str, ref byte** endchar, int b)
        {
            long i = 0;

            byte* p = str;

            var ascii = '\0';
            var zero = '0';
            while (char.IsDigit(ascii = (char)*p))
            {
                int num = ascii - zero;

                Assert.IsFalse(num < 0);
                Assert.IsFalse(num > 9);

                i = i * b + num;

                p++;
            }

            (*endchar) = p;

            return i;
        }

        private bool ISDELIM(uint u)
        {
            return u == ' ';
        }

        private void MODBIT(ref CursorState x, int set, CursorState bit)
        {
            if (set == 0)
                x &= ~bit;
            else
                x |= bit;
        }

        private void MODBIT(ref TermMode x, int set, TermMode bit)
        {
            if (set == 0)
                x &= ~bit;
            else
                x |= bit;
        }

        private void EnsureListCount<T>(ref List<T> list, int count) where T : new()
        {
            if (list.Count == count)
                return;

            if (list.Count < count)
            {
                for (int i = count; i < list.Count; i++) list.Add(new T());
                return;
            }

            if (list.Count > count)
                for (int i = list.Count - 1; i > count; i--)
                    list.RemoveAt(i);
        }

        public void OnPointerMove(PointerEventData eventData)
        {
            var seltype = Data.SelectionType.SEL_REGULAR;
            bool done = !this.isSelecting;

            RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, eventData.position, null, out Vector2 localPosition);

            if (localPosition.x < 0 || localPosition.y < 0)
                return;

            float x = Math.Abs(localPosition.x);
            float y = Math.Abs(localPosition.y);

            this.ExtendSelection(this.ColumnAt(x), this.RowAt(y), seltype, done);
        }

        public void OnScroll(PointerEventData eventData)
        {
            float delta = -eventData.delta.y;

            if (delta > 0)
                this.RegionScrollUp(this.mouseScrollLineCount);
            else if (delta < 0) this.RegionScrollDown(this.mouseScrollLineCount);
        }
    }
}