using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.TextCore;
using UnityEngine.TextCore.Text;
using UnityEngine.TextCore.LowLevel;

namespace Skunkworks.System
{
    // TextCore is used only to rasterize TTF glyphs. There is no text layout engine here.
    internal sealed class TerminalFontCache : IDisposable
    {
        public readonly FontAsset Font;
        public readonly float Width, Height, Baseline;
        public readonly int PointSize;
        private readonly HashSet<uint> requested = new HashSet<uint>();
        private readonly List<FontAsset> faces = new List<FontAsset>();
        private readonly HashSet<FontAsset> boxDrawingOnly = new HashSet<FontAsset>();
        public readonly List<Texture2D> Pages = new List<Texture2D>();
        public TerminalFontCache(Font source, string path, int size)
        {
            PointSize = size;
            int edge = Mathf.Clamp(Mathf.NextPowerOfTwo(size * 16), 256, 2048);
            Font = path == null
                ? FontAsset.CreateFontAsset(source, size, 1, GlyphRenderMode.SMOOTH_HINTED, edge, edge, AtlasPopulationMode.Dynamic, true)
                : FontAsset.CreateFontAsset(path, 0, size, 1, GlyphRenderMode.SMOOTH_HINTED, edge, edge);
            if (Font == null) throw new InvalidOperationException("Unable to rasterize terminal TTF: " + (path ?? source.name));
            Font.isMultiAtlasTexturesEnabled = true;
            faces.Add(Font);
            Add("M ?\uFFFD");
            var face = Font.faceInfo;
            Width = Font.characterLookupTable.TryGetValue('M', out var m) ? Mathf.Ceil(m.glyph.metrics.horizontalAdvance) : size;
            Height = Mathf.Ceil(Mathf.Max(face.lineHeight, size));
            Baseline = Mathf.Ceil(face.ascentLine);
        }
        public void Add(string text)
        {
            var missing = new List<uint>();
            for (int i = 0; i < text.Length; i++)
            {
                uint cp = text[i];
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    cp = (uint)char.ConvertToUtf32(text[i], text[++i]);
                if (cp >= 32 && requested.Count < 4096 && requested.Add(cp)) missing.Add(cp);
            }
            if (missing.Count != 0)
            {
                uint[] remaining = missing.ToArray();
                foreach (var face in faces)
                {
                    if (boxDrawingOnly.Contains(face))
                    {
                        uint[] boxCharacters = Array.FindAll(remaining, IsBoxDrawing);
                        if (boxCharacters.Length == 0) continue;
                        face.TryAddCharacters(boxCharacters, out uint[] missingBoxes);
                        var skipped = new List<uint>(Array.FindAll(remaining, cp => !IsBoxDrawing(cp)));
                        if (missingBoxes != null) skipped.AddRange(missingBoxes);
                        remaining = skipped.ToArray();
                    }
                    else face.TryAddCharacters(remaining, out remaining);
                    if (remaining == null || remaining.Length == 0) break;
                }
                Pages.Clear();
                foreach (var face in faces)
                    for (int i = 0; i < face.atlasTextureCount; i++) Pages.Add(face.atlasTextures[i]);
            }
        }
        public void AddFallback(string path)
        {
            int edge = Mathf.Clamp(Mathf.NextPowerOfTwo(PointSize * 16), 256, 2048);
            var face = FontAsset.CreateFontAsset(path, 0, PointSize, 1, GlyphRenderMode.SMOOTH_HINTED, edge, edge);
            if (face == null) throw new InvalidOperationException("Unable to rasterize fallback TTF: " + path);
            AddFallback(face);
        }
        public void AddFallback(Font source, bool boxesOnly)
        {
            int edge = Mathf.Clamp(Mathf.NextPowerOfTwo(PointSize * 16), 256, 2048);
            var face = FontAsset.CreateFontAsset(source, PointSize, 1, GlyphRenderMode.SMOOTH_HINTED, edge, edge, AtlasPopulationMode.Dynamic, true);
            if (face == null) throw new InvalidOperationException("Unable to rasterize bundled fallback TTF: " + source.name);
            AddFallback(face, boxesOnly);
        }
        private void AddFallback(FontAsset face, bool boxesOnly = false)
        {
            face.isMultiAtlasTexturesEnabled = true; faces.Add(face);
            if (boxesOnly) boxDrawingOnly.Add(face);
            requested.Clear(); // revisit formerly absent glyphs with the new fallback
            Add("M ?\uFFFD");
        }
        public Character Glyph(uint cp, out int page)
        {
            int offset = 0;
            foreach (var face in faces)
            {
                if ((!boxDrawingOnly.Contains(face) || IsBoxDrawing(cp)) && face.characterLookupTable.TryGetValue(cp, out var result))
                { page = offset + result.glyph.atlasIndex; return result; }
                offset += face.atlasTextureCount;
            }
            page = 0;
            if (!Font.characterLookupTable.TryGetValue(0xFFFD, out var replacement)) Font.characterLookupTable.TryGetValue('?', out replacement);
            if (replacement != null) page = replacement.glyph.atlasIndex;
            return replacement;
        }
        private static bool IsBoxDrawing(uint cp) => cp >= 0x2500 && cp <= 0x257F;
        public void Dispose()
        {
            foreach (var face in faces)
            {
                foreach (var texture in face.atlasTextures) if (texture != null) UnityEngine.Object.Destroy(texture);
                if (face.material != null) UnityEngine.Object.Destroy(face.material);
                UnityEngine.Object.Destroy(face);
            }
        }
    }

    // A bounded strip per atlas avoids uGUI's 65k vertex limit, even for a 300x120 screen.
    public sealed class TerminalGridGraphic : Graphic
    {
        internal TerminalTransport.Snapshot Snapshot;
        internal TerminalFontCache FontCache;
        internal int FirstRow, LastRow, Atlas, RowOffset;
        internal float Left, Top;
        internal bool CursorShown;
        internal int SelectionStart = -1, SelectionEnd = -1;
        internal string Banner = "", Footer = "";
        public override Texture mainTexture => FontCache == null || Atlas >= FontCache.Pages.Count ? Texture2D.whiteTexture : FontCache.Pages[Atlas];
        internal void Refresh() { SetVerticesDirty(); SetMaterialDirty(); }
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            if (FontCache == null || Snapshot == null) return;
            float width = FontCache.Width, height = FontCache.Height;
            if (Atlas == 0 && FirstRow == 0)
            {
                Quad(mesh, 0, -rectTransform.rect.height, rectTransform.rect.width, 0, Color.black);
                Quad(mesh, 0, -4, rectTransform.rect.width, 0, new Color32(0, 255, 96, 255));
            }
            if (FirstRow == 0)
            {
                Text(mesh, Footer, Left, -rectTransform.rect.height + height + 4, new Color32(131, 209, 151, 255));
            }
            int low = Math.Min(SelectionStart, SelectionEnd), high = Math.Max(SelectionStart, SelectionEnd);
            for (int row = FirstRow; row < Math.Min(LastRow, Snapshot.Rows - RowOffset); row++)
                for (int col = 0; col < Snapshot.Columns; col++)
                {
                    int index = row * Snapshot.Columns + col;
                    var cell = Snapshot.Cells[index];
                    float x = Left + col * width, y = -Top - (row + RowOffset) * height;
                    bool selected = low >= 0 && index >= low && index <= high;
                    bool cursor = CursorShown && col == Snapshot.X && row == Snapshot.Y;
                    Color fg = Rgb(cell.Style.VisibleForeground), bg = Rgb(cell.Style.VisibleBackground);
                    if (cursor) { Color swap = fg; fg = bg; bg = swap; }
                    if (selected) bg = new Color(.15f, .3f, .5f, 1);
                    if (Atlas == 0 && (bg != Color.black || selected || cursor)) Quad(mesh, x, y - height, x + width, y, bg);
                    if (!cell.Continuation && cell.Text != null && cell.Text != " ")
                    {
                        float span = col + 1 < Snapshot.Columns && Snapshot.Cells[index + 1].Continuation ? 2 * width : width;
                        DrawCell(mesh, cell.Text, x, y, span, fg, cell.Style.Bold, cell.Style.Italic);
                    }
                    if (Atlas == 0 && cell.Style.Underline) Quad(mesh, x, y - height, x + width, y - height + 1, fg);
                }
        }
        private void Text(VertexHelper mesh, string text, float x, float y, Color color)
        {
            int limit = Mathf.Max(0, (int)((rectTransform.rect.width - x) / FontCache.Width));
            for (int i = 0; i < Math.Min(text.Length, limit); i++)
                DrawCell(mesh, text[i].ToString(), x + i * FontCache.Width, y, FontCache.Width, color, false, false);
        }
        private void DrawCell(VertexHelper mesh, string text, float x, float y, float span, Color color, bool bold, bool italic)
        {
            float previousAdvance = 0;
            for (int i = 0; i < text.Length; i++)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(text, i);
                uint cp = text[i];
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    cp = (uint)char.ConvertToUtf32(text[i], text[++i]);
                var character = FontCache.Glyph(cp, out int page);
                if (character == null) continue;
                var glyph = character.glyph; var metrics = glyph.metrics; var rect = glyph.glyphRect;
                bool combining = category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.EnclosingMark;
                float l = x + (combining ? previousAdvance : 0) + metrics.horizontalBearingX, t = y - FontCache.Baseline + metrics.horizontalBearingY;
                if (!combining) previousAdvance = metrics.horizontalAdvance;
                if (page != Atlas) continue;
                float r = l + metrics.width, b = t - metrics.height;
                float cl = Mathf.Max(x, l), cr = Mathf.Min(x + span, r), cb = Mathf.Max(y - FontCache.Height, b), ct = Mathf.Min(y, t);
                if (cr <= cl || ct <= cb) continue;
                var atlas = FontCache.Pages[Atlas];
                Vector2 uv0 = new Vector2((rect.x + cl - l) / atlas.width, (rect.y + cb - b) / atlas.height);
                Vector2 uv1 = new Vector2((rect.x + cr - l) / atlas.width, (rect.y + ct - b) / atlas.height);
                Quad(mesh, cl, cb, cr, ct, color, true, uv0, uv1, italic ? 1 : 0);
                if (bold) Quad(mesh, cl + .5f, cb, Mathf.Min(cr + .5f, x + span), ct, color, true, uv0, uv1, italic ? 1 : 0);
            }
        }
        private static Color Rgb(int c) => new Color32((byte)(c >> 16), (byte)(c >> 8), (byte)c, 255);
        private static void Quad(VertexHelper mesh, float l, float b, float r, float t, Color color, bool textured = false, Vector2 uv0 = default, Vector2 uv1 = default, float shear = 0)
        {
            int first = mesh.currentVertCount;
            var vertex = UIVertex.simpleVert; vertex.color = color; vertex.uv1 = new Vector2(textured ? 1 : 0, 0);
            vertex.position = new Vector3(l, b); vertex.uv0 = uv0; mesh.AddVert(vertex);
            vertex.position = new Vector3(r, b); vertex.uv0 = new Vector2(uv1.x, uv0.y); mesh.AddVert(vertex);
            vertex.position = new Vector3(r + shear, t); vertex.uv0 = uv1; mesh.AddVert(vertex);
            vertex.position = new Vector3(l + shear, t); vertex.uv0 = new Vector2(uv0.x, uv1.y); mesh.AddVert(vertex);
            mesh.AddTriangle(first, first + 1, first + 2); mesh.AddTriangle(first + 2, first + 3, first);
        }
    }
    public sealed class TerminalViewPump : MonoBehaviour
    {
        private void Update() { TerminalView.RefreshNow(); }
    }
}
