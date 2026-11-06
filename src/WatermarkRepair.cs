using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

internal sealed class RepairResult : IDisposable
{
    private Bitmap image;
    public readonly Rectangle Area;
    public readonly int LayerCount;

    public RepairResult(Bitmap image, Rectangle area, int layerCount)
    {
        this.image = image;
        Area = area;
        LayerCount = layerCount;
    }

    public Bitmap TakeImage()
    {
        Bitmap result = image;
        image = null;
        return result;
    }

    public void Dispose()
    {
        if (image != null) image.Dispose();
    }
}

internal static class WatermarkRepair
{
    // The template stores the coverage of the white lettering and its dark border.
    // No source image pixels are stored in the resource.
    private const int TemplateWidth = 233;
    private const int TemplateHeight = 71;
    private const int SuffixStart = 88;
    private const int MaximumLayers = 4;
    // Keep the calibrated mattes on their native sampling grid for accurate resampling.
    private const int NativeWidth = 266;
    private const int NativeHeight = 81;
    private const float NativeScale = 1.14f;
    private static readonly float[][] Template = ReadTemplate();
    private static readonly float[] GrayMatte = ReadNativeMattes("GrayWatermarkMask", 1)[0];
    private static readonly float[][] OutlineMatte = ReadNativeMattes("OutlinedWatermarkMask", 2);
    private static readonly Probe[] Probes = BuildProbes(Profile.White);
    private static readonly Probe[] GrayProbes = BuildProbes(Profile.Gray);
    private static readonly Probe[] OutlineProbes = BuildProbes(Profile.FineOutline);
    private static readonly Probe[] SuffixProbes = Array.FindAll(Probes, p => p.X0 >= SuffixStart);
    private static readonly Probe[] GraySuffixProbes = Array.FindAll(GrayProbes, p => p.X0 >= SuffixStart);

    public static RepairResult Run(Bitmap source, Rectangle? selection)
    {
        int width, height;
        lock (source) { width = source.Width; height = source.Height; }
        var image = new Rectangle(0, 0, width, height);
        var search = selection.HasValue ? Rectangle.Intersect(image, selection.Value)
            : Rectangle.FromLTRB(width * 58 / 100, height * 68 / 100, width, height);
        if (search.Width < 24 || search.Height < 8) return null;

        // Large searches are sampled at a lower resolution. Repair always uses the original pixels.
        float sampleScale = Math.Min(1, 1050f / Math.Max(search.Width, search.Height));
        Luminance luminance = Luminance.Read(source, search, sampleScale);
        Match match = Locate(luminance, selection.HasValue, width * sampleScale, height * sampleScale, false);
        Match gray = Locate(luminance, selection.HasValue, width * sampleScale, height * sampleScale, true);
        if (gray != null && (match == null || gray.Score > match.Score + .04f)) match = gray;
        if (match == null || match.Scale / sampleScale > 8) return null;
        Match primary = match.ToImage(search, sampleScale);
        if (!primary.GrayInk) primary = RefineOutlineInImage(source, image, primary);
        var layers = new List<Match> { primary };
        Rectangle area = Rectangle.Intersect(image, primary.RepairBounds());
        if (area.Width < 4 || area.Height < 4) return null;

        Bitmap output;
        lock (source) output = source.Clone(image, PixelFormat.Format32bppArgb);
        try
        {
            while (true)
            {
                Rectangle render = area;
                if (layers.Count > 1)
                {
                    render.Inflate((int)Math.Ceiling(52 * primary.Scale), (int)Math.Ceiling(35 * primary.Scale));
                    render.Intersect(image);
                }
                bool detectOnly = layers.Count > 1 && layers.Count < MaximumLayers;
                RepairArea(source, output, render, layers, detectOnly);
                Match residual = layers.Count < MaximumLayers ? FindResidual(output, search, primary, layers) : null;
                if (residual == null)
                {
                    if (detectOnly) RepairArea(source, output, render, layers, false);
                    break;
                }
                layers.Add(residual);
                area = Rectangle.Union(area, Rectangle.Intersect(image, residual.RepairBounds()));
            }
            return new RepairResult(output, area, layers.Count);
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static float[][] ReadTemplate()
    {
        var layers = new float[4][];
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WatermarkMask"))
        using (var reader = new BinaryReader(stream))
        {
            for (int layer = 0; layer < layers.Length; layer++)
            {
                layers[layer] = new float[TemplateWidth * TemplateHeight];
                for (int i = 0; i < layers[layer].Length; i++) layers[layer][i] = reader.ReadByte() / 255f;
            }
        }
        return layers;
    }

    private static float[][] ReadNativeMattes(string resource, int count)
    {
        var layers = new float[count][];
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
        using (var reader = new BinaryReader(stream))
        {
            for (int layer = 0; layer < count; layer++)
            {
                var matte = new float[NativeWidth * NativeHeight];
                for (int i = 0; i < matte.Length; i++) matte[i] = reader.ReadUInt16() / 65535f;
                layers[layer] = matte;
            }
        }
        return layers;
    }

    private static float SampleNative(float[] matte, float x, float y)
    {
        x *= NativeScale;
        y *= NativeScale;
        return x >= 0 && x < NativeWidth - 1 && y >= 0 && y < NativeHeight - 1
            ? SampleBilinear(matte, NativeWidth, x, y) : 0;
    }

    private enum Profile { White, Gray, FineOutline }

    private struct Probe
    {
        public int X0, Y0, X1, Y1;
        public float White, Dark, Strength;
    }

    private static Probe[] BuildProbes(Profile profile)
    {
        // Spread the probes along the full text so a single letter or background edge cannot dominate.
        var probes = new List<Probe>();
        for (int x = 8; x < TemplateWidth - 8; x += 9)
        for (int y = 8; y < TemplateHeight - 8; y += 7)
        {
            var local = new List<Probe>();
            for (int py = y; py < Math.Min(y + 7, TemplateHeight - 4); py++)
            for (int px = x; px < Math.Min(x + 9, TemplateWidth - 4); px++)
            {
                AddProbe(local, px, py, px + 2, py, profile);
                AddProbe(local, px, py, px, py + 2, profile);
            }
            local.Sort((a, b) => b.Strength.CompareTo(a.Strength));
            for (int i = 0; i < Math.Min(3, local.Count); i++) probes.Add(local[i]);
        }
        // Mix the cells so the coarse scan samples the entire line.
        probes.Sort((a, b) => ProbeOrder(a).CompareTo(ProbeOrder(b)));
        return probes.ToArray();
    }

    private static uint ProbeOrder(Probe probe)
    {
        return unchecked((uint)((probe.X0 + probe.X1 * 271) * 73856093 ^ (probe.Y0 + probe.Y1 * 127) * 19349663));
    }

    private static void AddProbe(List<Probe> probes, int x0, int y0, int x1, int y1, Profile profile)
    {
        int i = x0 + y0 * TemplateWidth, j = x1 + y1 * TemplateWidth;
        float a = Template[0][j] - Template[0][i];
        float s = Template[1][j] - Template[1][i];
        if (profile == Profile.Gray)
        {
            a = 0;
            s = (SampleNative(GrayMatte, x1, y1) - SampleNative(GrayMatte, x0, y0)) * 3.2f;
        }
        else if (profile == Profile.FineOutline)
        {
            a = SampleNative(OutlineMatte[0], x1, y1) - SampleNative(OutlineMatte[0], x0, y0);
            s = SampleNative(OutlineMatte[1], x1, y1) - SampleNative(OutlineMatte[1], x0, y0);
        }
        float strength = Math.Abs(a) + .8f * Math.Abs(s);
        if (strength < .24f) return;
        probes.Add(new Probe { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, White = a, Dark = s, Strength = strength });
    }

    private sealed class Match
    {
        public float X, Y, Scale, Score;
        public bool SuffixOnly, GrayInk, FineOutline;

        public Match ToImage(Rectangle crop, float sampleScale)
        {
            return new Match
            {
                X = crop.X + (X + .5f) / sampleScale - .5f,
                Y = crop.Y + (Y + .5f) / sampleScale - .5f,
                Scale = Scale / sampleScale,
                Score = Score,
                SuffixOnly = SuffixOnly,
                GrayInk = GrayInk,
                FineOutline = FineOutline
            };
        }

        public Rectangle RepairBounds()
        {
            return Rectangle.FromLTRB(
                (int)Math.Floor(X + (SuffixOnly ? SuffixStart - 3 : 2) * Scale),
                (int)Math.Floor(Y + 4 * Scale),
                (int)Math.Ceiling(X + (TemplateWidth - 3) * Scale),
                (int)Math.Ceiling(Y + (TemplateHeight - 4) * Scale));
        }
    }

    private static Match RefineOutlineInImage(Bitmap source, Rectangle image, Match original)
    {
        // Use nearby pixels even when the user has drawn a tight search box around the letters.
        Rectangle crop = original.RepairBounds();
        int margin = (int)Math.Ceiling(12 * original.Scale);
        crop.Inflate(margin, margin);
        crop.Intersect(image);
        float scale = Math.Min(1, 670f / crop.Width);
        Luminance local = Luminance.Read(source, crop, scale);
        var start = new Match
        {
            X = (original.X - crop.X + .5f) * scale - .5f,
            Y = (original.Y - crop.Y + .5f) * scale - .5f,
            Scale = original.Scale * scale,
            Score = original.Score
        };
        if (!HasLightBackground(local, start)) return original;
        Match refined = RefineOutline(local, start);
        return refined.FineOutline && refined.Scale / scale <= 8 ? refined.ToImage(crop, scale) : original;
    }

    private static bool HasLightBackground(Luminance image, Match match)
    {
        // Compare the strips above and below the letters, away from their shadows.
        float brightness = 0;
        int count = 0, bright = 0;
        for (int x = 16; x < 216; x += 9)
        for (int y = 8; y <= 65; y += 57)
        {
            float value = image.Sample(match.X + x * match.Scale, match.Y + y * match.Scale);
            brightness += value;
            if (value >= .77f) bright++;
            count++;
        }
        return brightness >= .81f * count && bright >= .65f * count;
    }

    private static Match RefineOutline(Luminance image, Match original)
    {
        var best = new Match { X = original.X, Y = original.Y, Scale = original.Scale, Score = -1, FineOutline = true };
        for (int pass = 0; pass < 3; pass++)
        {
            float x = best.X, y = best.Y, scale = best.Scale;
            float step = Math.Max(.7f, original.Scale) / (1 << pass);
            for (int size = -2; size <= 2; size++)
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                float zoom = scale + size * step / 100;
                float u = x + dx * step - (zoom - scale) * 115;
                float v = y + dy * step - (zoom - scale) * 34;
                float score = FineScore(image, OutlineProbes, u, v, zoom);
                if (score <= best.Score) continue;
                best.X = u;
                best.Y = v;
                best.Scale = zoom;
                best.Score = score;
            }
        }
        return best.Score >= .42f && best.Score + .075f >= original.Score ? best : original;
    }

    private static Match FindResidual(Bitmap preview, Rectangle search, Match primary, List<Match> layers)
    {
        Rectangle bounds = primary.RepairBounds();
        bounds.Inflate((int)Math.Ceiling(58 * primary.Scale), (int)Math.Ceiling(27 * primary.Scale));
        bounds.Intersect(search);
        if (bounds.Width < 24 || bounds.Height < 8) return null;

        float sampleScale = Math.Min(1, 670f / bounds.Width);
        Luminance luminance = Luminance.Read(preview, bounds, sampleScale);
        float x = (primary.X - bounds.X + .5f) * sampleScale - .5f;
        float y = (primary.Y - bounds.Y + .5f) * sampleScale - .5f;
        float scale = primary.Scale * sampleScale;
        Match best = null;
        for (int style = 0; style < 2; style++)
        for (int part = 0; part < 2; part++)
        {
            Match candidate = ScanResidual(luminance, x, y, scale, part != 0, style != 0);
            candidate = ConfirmResidual(candidate, bounds, sampleScale, layers);
            if (candidate == null) continue;
            float score = candidate.Score + (candidate.SuffixOnly ? 0 : .055f);
            if (best == null || score > best.Score + (best.SuffixOnly ? 0 : .055f)) best = candidate;
        }
        return best;
    }

    private static Match ConfirmResidual(Match match, Rectangle bounds, float sampleScale, List<Match> layers)
    {
        if (match == null) return null;
        Match candidate = match.ToImage(bounds, sampleScale);
        if (candidate.Scale > 8) return null;
        float anchor = candidate.SuffixOnly ? 153 : 115;
        int similar = 0;
        foreach (Match previous in layers)
        {
            bool sameInk = candidate.GrayInk == previous.GrayInk;
            if (!sameInk && layers.Count == 1) continue;
            float shiftTolerance = (sameInk ? 2.4f : 5.2f) * candidate.Scale;
            float scaleTolerance = (sameInk ? .027f : .04f) * candidate.Scale;
            if (Math.Abs(candidate.Scale - previous.Scale) > scaleTolerance) continue;
            if (Math.Abs(candidate.X - previous.X + anchor * (candidate.Scale - previous.Scale)) > shiftTolerance)
                continue;
            if (Math.Abs(candidate.Y - previous.Y + 34 * (candidate.Scale - previous.Scale)) <= shiftTolerance)
                similar++;
        }
        // The first preview can reveal a truly coincident second layer. After a joint
        // inversion, repeated hits at known positions are usually matte alignment errors.
        if (similar > 0 && layers.Count > 1) return null;
        float threshold = layers.Count == 1 ? .48f : (candidate.GrayInk ? .61f : .53f);
        return candidate.Score >= threshold + similar * .095f ? candidate : null;
    }

    private static Match ScanResidual(Luminance image, float x, float y, float scale, bool suffixOnly, bool grayInk)
    {
        float anchor = suffixOnly ? 153 : 115;
        float step = Math.Max(.7f, 1.6f * scale);
        var best = new Match { Score = -1, SuffixOnly = suffixOnly, GrayInk = grayInk };
        Probe[] probes = grayInk ? (suffixOnly ? GraySuffixProbes : GrayProbes) : (suffixOnly ? SuffixProbes : Probes);
        for (int zoom = -7; zoom <= 7; zoom++)
        {
            float nextScale = scale * (1 + .038f * zoom);
            if (nextScale < .17f) continue;
            float startX = x - (nextScale - scale) * anchor;
            float startY = y - (nextScale - scale) * 34;
            for (float dy = -14 * scale; dy <= 14 * scale; dy += step)
            for (float dx = -34 * scale; dx <= 34 * scale; dx += step)
                TryResidual(image, best, probes, startX + dx, startY + dy, nextScale);
        }
        if (best.Score < .23f) return null;
        for (int pass = 0; pass < 3; pass++)
        {
            float move = step / (2 << pass);
            float bx = best.X, by = best.Y, bs = best.Scale;
            float zoomStep = move / 50;
            for (int zoom = -2; zoom <= 2; zoom++)
            {
                float nextScale = bs + zoom * zoomStep;
                if (nextScale < .17f) continue;
                for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    TryResidual(image, best, probes, bx + dx * move - (nextScale - bs) * anchor,
                        by + dy * move - (nextScale - bs) * 34, nextScale);
            }
        }
        return best;
    }

    private struct Evidence
    {
        public float Dot, Signal, Expected;
        public int Count;

        public void Add(float p, float q, Probe probe)
        {
            AddScore(p, q, probe.White, probe.Dark, ref Dot, ref Signal, ref Expected);
            Count++;
        }

        public float Confidence
        {
            get { return Score(Dot, Signal, Expected, Count); }
        }
    }

    private static void TryResidual(Luminance image, Match best, Probe[] probes, float x, float y, float scale)
    {
        bool suffix = best.SuffixOnly;
        if (x + (suffix ? SuffixStart : 7) * scale < -1 || y + 7 * scale < -1 ||
            x + (TemplateWidth - 9) * scale > image.Width || y + (TemplateHeight - 9) * scale > image.Height)
            return;

        var first = new Evidence();
        var middle = new Evidence();
        var last = new Evidence();
        int split1 = suffix ? 141 : SuffixStart, split2 = suffix ? 182 : 143;
        foreach (Probe probe in probes)
        {
            float p = image.Sample(x + probe.X0 * scale, y + probe.Y0 * scale);
            float q = image.Sample(x + probe.X1 * scale, y + probe.Y1 * scale);
            if (probe.X0 < split1) first.Add(p, q, probe);
            else if (probe.X0 < split2) middle.Add(p, q, probe);
            else last.Add(p, q, probe);
        }
        float score = Score(first.Dot + middle.Dot + last.Dot, first.Signal + middle.Signal + last.Signal,
            first.Expected + middle.Expected + last.Expected, first.Count + middle.Count + last.Count);
        // All groups must contribute. A strong "AI生成" alone should not qualify as the full line.
        float weakest = Math.Min(first.Confidence, Math.Min(middle.Confidence, last.Confidence));
        score = Math.Min(score, weakest + .14f);
        if (score <= best.Score) return;
        best.Score = score;
        best.X = x;
        best.Y = y;
        best.Scale = scale;
    }

    private static Match Locate(Luminance image, bool manual, float fullWidth, float fullHeight, bool grayInk)
    {
        Probe[] probes = grayInk ? GrayProbes : Probes;
        var scales = new List<float>();
        if (manual)
        {
            float maximum = Math.Min(image.Width / 207f, image.Height / 43f);
            for (float ratio = .28f; ratio <= 1.07f; ratio *= 1.075f)
                AddScale(scales, maximum * ratio);
            AddScale(scales, maximum);
        }
        else
        {
            float preferred = Math.Min(fullWidth, fullHeight) / 1344f;
            for (float ratio = .52f; ratio < 1.85f; ratio *= 1.065f)
                AddScale(scales, preferred * ratio);
            AddScale(scales, preferred);
            AddScale(scales, fullWidth / 1792f);
            AddScale(scales, fullHeight / 1344f);
        }

        var finalists = new List<Match>();
        foreach (float scale in scales)
        {
            int xmin = (int)Math.Ceiling(-8 * scale);
            int ymin = (int)Math.Ceiling(-8 * scale);
            int xmax = (int)Math.Floor(image.Width - (TemplateWidth - 10) * scale);
            int ymax = (int)Math.Floor(image.Height - (TemplateHeight - 9) * scale);
            int step = Math.Max(1, (int)Math.Round(scale * 1.9f));
            if (!manual)
            {
                xmin = Math.Max(xmin, (int)(image.Width - 330 * scale));
                ymin = Math.Max(ymin, (int)(image.Height - 139 * scale));
                xmax = Math.Min(xmax, (int)(image.Width - 222 * scale));
                ymax = Math.Min(ymax, (int)(image.Height - 62 * scale));
            }
            else
            {
                // Keep oversized selections responsive. Fine alignment below uses subpixels.
                step = Math.Max(step, (int)Math.Ceiling(Math.Sqrt((long)image.Width * image.Height / 16000.0)));
            }
            var offsets = new Offset[Math.Min(85, probes.Length)];
            for (int i = 0; i < offsets.Length; i++)
                offsets[i] = Offset.Create(probes[i], scale, image.Stride);
            var local = new List<Match>();
            for (int y = ymin; y <= ymax; y += step)
            for (int x = xmin; x <= xmax; x += step)
            {
                float score = CoarseScore(image, offsets, x, y);
                KeepMatch(local, x, y, scale, score, 3);
            }
            finalists.AddRange(local);
        }
        if (finalists.Count == 0) return null;
        finalists.Sort((a, b) => b.Score.CompareTo(a.Score));

        Match best = null;
        for (int candidate = 0; candidate < Math.Min(13, finalists.Count); candidate++)
        {
            Match seed = finalists[candidate];
            float shift = Math.Max(1, seed.Scale * 1.4f);
            Match aligned = seed;
            float alignedScore = -1;
            for (int sw = -2; sw <= 2; sw++)
            {
                float scale = seed.Scale * (1 + sw * .015f);
                for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    float x = seed.X + dx * shift - (scale - seed.Scale) * TemplateWidth / 2;
                    float y = seed.Y + dy * shift - (scale - seed.Scale) * TemplateHeight / 2;
                    float score = FineScore(image, probes, x, y, scale);
                    if (score > alignedScore)
                    {
                        alignedScore = score;
                        aligned = new Match { X = x, Y = y, Scale = scale, Score = score };
                    }
                }
            }
            if (best == null || aligned.Score > best.Score) best = aligned;
        }
        for (int pass = 0; pass < 3; pass++)
        {
            Match seed = best;
            float move = Math.Max(.22f, seed.Scale * .5f) / (pass + 1);
            for (int sw = -1; sw <= 1; sw++)
            {
                float scale = seed.Scale + sw * move / 115;
                for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    float x = seed.X + dx * move - (scale - seed.Scale) * TemplateWidth / 2;
                    float y = seed.Y + dy * move - (scale - seed.Scale) * TemplateHeight / 2;
                    float score = FineScore(image, probes, x, y, scale);
                    if (score > best.Score) best = new Match { X = x, Y = y, Scale = scale, Score = score };
                }
            }
        }
        float threshold = grayInk ? (manual ? .41f : .44f) : (manual ? .29f : .32f);
        best.GrayInk = grayInk;
        return best.Score >= threshold ? best : null;
    }

    private static void AddScale(List<float> scales, float scale)
    {
        if (scale < .19f || scale > 8f) return;
        foreach (float previous in scales)
            if (Math.Abs(previous - scale) < scale * .009f) return;
        scales.Add(scale);
    }

    private static void KeepMatch(List<Match> matches, int x, int y, float scale, float score, int count)
    {
        if (score < .10f || (matches.Count == count && score <= matches[count - 1].Score)) return;
        for (int i = 0; i < matches.Count; i++)
        {
            Match previous = matches[i];
            if (Math.Abs(x - previous.X) > Math.Max(3, 4 * scale) || Math.Abs(y - previous.Y) > Math.Max(3, 4 * scale))
                continue;
            if (score <= previous.Score) return;
            matches.RemoveAt(i);
            break;
        }
        int index = 0;
        while (index < matches.Count && matches[index].Score > score) index++;
        matches.Insert(index, new Match { X = x, Y = y, Scale = scale, Score = score });
        if (matches.Count > count) matches.RemoveAt(count);
    }

    private struct Offset
    {
        public int A, B;
        public float White, Dark;

        public static Offset Create(Probe p, float scale, int stride)
        {
            return new Offset
            {
                A = (int)Math.Round(p.X0 * scale) + stride * (int)Math.Round(p.Y0 * scale),
                B = (int)Math.Round(p.X1 * scale) + stride * (int)Math.Round(p.Y1 * scale),
                White = p.White,
                Dark = p.Dark
            };
        }
    }

    private static float CoarseScore(Luminance image, Offset[] offsets, int x, int y)
    {
        int origin = (x + Luminance.Pad) + (y + Luminance.Pad) * image.Stride;
        float dot = 0, signal = 0, expected = 0;
        foreach (Offset offset in offsets)
        {
            float p = image.Data[origin + offset.A], q = image.Data[origin + offset.B];
            AddScore(p, q, offset.White, offset.Dark, ref dot, ref signal, ref expected);
        }
        return Score(dot, signal, expected, offsets.Length);
    }

    private static float FineScore(Luminance image, Probe[] probes, float x, float y, float scale)
    {
        if (x + 7 * scale < -1 || y + 7 * scale < -1 ||
            x + (TemplateWidth - 9) * scale > image.Width || y + (TemplateHeight - 9) * scale > image.Height)
            return -1;
        float dot = 0, signal = 0, expected = 0, left = 0, right = 0, middle = 0;
        foreach (Probe probe in probes)
        {
            float p = image.Sample(x + probe.X0 * scale, y + probe.Y0 * scale);
            float q = image.Sample(x + probe.X1 * scale, y + probe.Y1 * scale);
            float part = AddScore(p, q, probe.White, probe.Dark, ref dot, ref signal, ref expected);
            if (probe.X0 < 80) left += part;
            else if (probe.X0 < 150) middle += part;
            else right += part;
        }
        float coverage = Math.Min(left, Math.Min(middle, right));
        return Score(dot, signal, expected, probes.Length) * (coverage > .065f ? 1 : .78f);
    }

    private static float AddScore(float p, float q, float white, float dark, ref float dot, ref float signal, ref float expected)
    {
        float background = Clamp((p + q) * .5f, .06f, .98f);
        float pattern = white * (1 - background) - dark * background;
        float delta = Clamp(q - p, -.34f, .34f);
        float product = delta * pattern;
        dot += product;
        signal += delta * delta;
        expected += pattern * pattern;
        return product;
    }

    private static float Score(float dot, float signal, float expected, int count)
    {
        return dot / (float)Math.Sqrt((signal + .0007f * count) * (expected + .0001f));
    }

    private sealed class Luminance
    {
        public readonly float[] Data;
        public readonly int Width, Height, Stride;
        public const int Pad = 72;

        private Luminance(Bitmap source)
        {
            Width = source.Width;
            Height = source.Height;
            Stride = Width + 2 * Pad;
            Data = new float[Stride * (Height + 2 * Pad)];
            var image = new Rectangle(0, 0, Width, Height);
            var pixels = source.LockBits(image, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[Width * 4];
                for (int y = 0; y < Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(pixels.Scan0, y * pixels.Stride), row, 0, row.Length);
                    int index = (y + Pad) * Stride + Pad;
                    for (int x = 0; x < Width; x++)
                    {
                        int i = x * 4;
                        float brightness = (.0722f * row[i] + .7152f * row[i + 1] + .2126f * row[i + 2]) / 255;
                        Data[index + x] = 1 + (brightness - 1) * row[i + 3] / 255;
                    }
                    for (int p = 1; p <= Pad; p++)
                    {
                        Data[index - p] = Data[index];
                        Data[index + Width - 1 + p] = Data[index + Width - 1];
                    }
                }
                for (int p = 0; p < Pad; p++)
                {
                    Array.Copy(Data, Pad * Stride, Data, p * Stride, Stride);
                    Array.Copy(Data, (Pad + Height - 1) * Stride, Data, (Pad + Height + p) * Stride, Stride);
                }
            }
            finally { source.UnlockBits(pixels); }
        }

        public static Luminance Read(Bitmap source, Rectangle area, float scale)
        {
            Bitmap crop;
            lock (source) crop = source.Clone(area, PixelFormat.Format32bppArgb);
            using (crop)
            {
                if (scale >= 1) return new Luminance(crop);
                using (var small = new Bitmap(Math.Max(1, (int)Math.Round(area.Width * scale)),
                    Math.Max(1, (int)Math.Round(area.Height * scale)), PixelFormat.Format32bppArgb))
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(crop, new Rectangle(0, 0, small.Width, small.Height), 0, 0, crop.Width, crop.Height, GraphicsUnit.Pixel);
                    return new Luminance(small);
                }
            }
        }

        public float Sample(float x, float y)
        {
            return SampleBilinear(Data, Stride, x + Pad, y + Pad);
        }
    }

    private static void RepairArea(Bitmap source, Bitmap output, Rectangle area, List<Match> layers, bool detectOnly)
    {
        int width = area.Width, height = area.Height, count = width * height;
        float scale = layers[0].Scale;
        foreach (Match layer in layers) scale = Math.Max(scale, layer.Scale);
        var white = new float[count];
        var dark = new float[count];
        var boundaryWhite = new float[count];
        var boundaryDark = new float[count];
        var support = new bool[count];
        bool stacked = layers.Count > 1;
        bool singleGray = !stacked && layers[0].GrayInk;
        bool singleOutline = !stacked && layers[0].FineOutline;
        float[][] mattes = stacked || singleGray || singleOutline ? new float[layers.Count * 2][] : null;
        if (mattes != null)
            for (int i = 0; i < mattes.Length; i++) mattes[i] = new float[count];
        var pixels = output.LockBits(area, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var rgba = new byte[count * 4];
            lock (source)
            {
                var input = source.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int row = 0; row < height; row++)
                        Marshal.Copy(IntPtr.Add(input.Scan0, row * input.Stride), rgba, row * width * 4, width * 4);
                }
                finally { source.UnlockBits(input); }
            }
            var seed = new float[count * 3];
            int halo = Math.Max(1, (int)Math.Ceiling((stacked ? 2.7f : singleOutline ? 2.25f : 1.8f) * scale));
            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int i = row * width + col;
                    float a = 0, s = 0;
                    for (int layerIndex = 0; layerIndex < layers.Count; layerIndex++)
                    {
                        Match layer = layers[layerIndex];
                        float tx = (area.X + col - layer.X) / layer.Scale;
                        float ty = (area.Y + row - layer.Y) / layer.Scale;
                        if (tx < (layer.SuffixOnly ? SuffixStart : 0) || tx >= TemplateWidth - 1 ||
                            ty < 0 || ty >= TemplateHeight - 1) continue;

                        float w, d, dw, dd;
                        if (layer.GrayInk)
                        {
                            w = dw = 0;
                            d = dd = SampleNative(GrayMatte, tx, ty);
                        }
                        else if (layer.FineOutline)
                        {
                            w = dw = SampleNative(OutlineMatte[0], tx, ty);
                            d = dd = SampleNative(OutlineMatte[1], tx, ty);
                        }
                        else
                        {
                            w = SampleBilinear(Template[0], TemplateWidth, tx, ty);
                            d = SampleBilinear(Template[1], TemplateWidth, tx, ty);
                            dw = SampleBilinear(Template[2], TemplateWidth, tx, ty);
                            dd = SampleBilinear(Template[3], TemplateWidth, tx, ty);
                        }
                        Compose(ref white[i], ref dark[i], w, d);
                        Compose(ref boundaryWhite[i], ref boundaryDark[i], dw, dd);
                        float fill = .87f * w + .13f * dw;
                        float border = .40f * d + .60f * dd;
                        if (mattes != null)
                        {
                            mattes[layerIndex * 2][i] = fill;
                            mattes[layerIndex * 2 + 1][i] = border;
                        }
                        else
                        {
                            a = fill;
                            s = border;
                        }
                    }
                    if (mattes == null)
                    {
                        float inverse = 1 / Math.Max(.22f, 1 - a - s);
                        for (int channel = 0; channel < 3; channel++)
                            seed[i * 3 + channel] = Clamp((rgba[i * 4 + channel] / 255f - a) * inverse, -.08f, 1.08f);
                    }
                    if (Math.Max(white[i], dark[i]) < .015f || rgba[i * 4 + 3] == 0) continue;
                    for (int v = Math.Max(1, row - halo); v <= Math.Min(height - 2, row + halo); v++)
                    for (int u = Math.Max(1, col - halo); u <= Math.Min(width - 2, col + halo); u++)
                        support[v * width + u] = true;
                }
            }

            float[] guide = null;
            float[] paper = null;
            if (mattes != null)
            {
                guide = BuildGuide(rgba, support, width, scale);
                if (singleOutline) paper = LightPaperConfidence(guide, support, width);
                float[] gains = FitLayerGains(rgba, guide, mattes, layers);
                for (int i = 0; i < count; i++)
                {
                    if (stacked && (!support[i] || rgba[i * 4 + 3] == 0)) continue;
                    float a = 0, s = 0;
                    for (int k = 0; k < gains.Length; k += 2)
                        Compose(ref a, ref s, gains[k] * mattes[k][i], gains[k + 1] * mattes[k + 1][i]);
                    float transmission = Math.Max(singleOutline ? .25f : .12f, 1 - a - s);
                    for (int c = 0; c < 3; c++)
                    {
                        int channel = i * 3 + c;
                        float value = (rgba[i * 4 + c] / 255f - a) / transmission;
                        if (!stacked)
                        {
                            if (singleOutline && paper[i] > 0)
                            {
                                float deviation = value - guide[channel];
                                value = guide[channel] + deviation / (1 + Math.Abs(deviation) * 7 * paper[i]);
                            }
                            seed[channel] = Clamp(value, -.08f, 1.08f);
                            continue;
                        }
                        if (detectOnly)
                        {
                            rgba[i * 4 + c] = (byte)(Clamp(value, 0, 1) * 255 + .5f);
                            continue;
                        }
                        // Use the original signal for texture while limiting the ringing caused
                        // by small matte errors where the stacked layers transmit little light.
                        float error = value - guide[channel];
                        seed[channel] = error / (1 + Math.Abs(error) * 4.5f);
                    }
                }
                if (detectOnly)
                {
                    WritePixels(pixels, rgba, width, height);
                    return;
                }
            }

            var horizontal = new float[count];
            var vertical = new float[count];
            for (int row = 0; row < height - 1; row++)
            for (int col = 0; col < width - 1; col++)
            {
                int i = row * width + col;
                horizontal[i] = Transition(i, i + 1, white, dark, boundaryWhite, boundaryDark, scale, stacked ? mattes : null);
                vertical[i] = Transition(i, i + width, white, dark, boundaryWhite, boundaryDark, scale, stacked ? mattes : null);
            }

            var rhs = new float[seed.Length];
            int radius = Math.Max(1, (int)Math.Round(scale));
            float[] horizontalBand = Dilate(horizontal, width, height, radius);
            float[] verticalBand = Dilate(vertical, width, height, radius);
            for (int row = 0; row < height; row++)
            for (int col = 0; col < width; col++)
            {
                int i = row * width + col;
                if (col + 1 < width && (support[i] || support[i + 1]))
                    AddGradient(seed, rhs, horizontal, horizontalBand, width, height, col, row, radius, false, stacked, singleGray, singleOutline);
                if (row + 1 < height && (support[i] || support[i + width]))
                    AddGradient(seed, rhs, vertical, verticalBand, width, height, col, row, radius, true, stacked, singleGray, singleOutline);
            }

            // Integrate the gradients with the surrounding original pixels as fixed boundary values.
            // Across stable parts of the mask, the original recovered gradients stay in the solution.
            float[] solved = (float[])seed.Clone();
            int iterations = Math.Min(144, Math.Max(28, (int)((stacked ? 95 : 65) * scale)));
            Integrate(solved, rhs, seed, stacked ? .012f : .017f, support, rgba, width, iterations);
            if (stacked)
            {
                for (int i = 0; i < solved.Length; i++) solved[i] += guide[i];
                OverlapTexture.Restore(solved, rgba, support, width, height, scale);
            }

            for (int i = 0; i < count; i++)
            {
                if (!support[i] || rgba[i * 4 + 3] == 0) continue;
                int pixel = i * 3;
                float chromaBlend = singleOutline ? .78f * paper[i] * Math.Max(horizontalBand[i], verticalBand[i]) : 0;
                // JPEG ringing can introduce color around the removed outline. Correct
                // chroma on smooth paper while keeping the solved luminance and its grain.
                float luminanceChange = chromaBlend > 0 ? Luma(solved, pixel) - Luma(guide, pixel) : 0;
                for (int c = 0; c < 3; c++)
                {
                    int channel = pixel + c;
                    float value = solved[channel];
                    if (chromaBlend > 0) value -= chromaBlend * (value - guide[channel] - luminanceChange);
                    rgba[i * 4 + c] = (byte)(Clamp(value, 0, 1) * 255 + .5f);
                }
            }
            WritePixels(pixels, rgba, width, height);
        }
        finally { output.UnlockBits(pixels); }
    }

    private static void WritePixels(BitmapData pixels, byte[] rgba, int width, int height)
    {
        for (int row = 0; row < height; row++)
            Marshal.Copy(rgba, row * width * 4, IntPtr.Add(pixels.Scan0, row * pixels.Stride), width * 4);
    }

    private static float[] LightPaperConfidence(float[] guide, bool[] support, int width)
    {
        var confidence = new float[support.Length];
        for (int i = width + 1; i < support.Length - width - 1; i++)
        {
            if (!support[i]) continue;
            int p = i * 3, row = width * 3;
            float light = Luma(guide, p);
            float variation = Math.Max(Math.Abs(Luma(guide, p - 3) - Luma(guide, p + 3)),
                Math.Abs(Luma(guide, p - row) - Luma(guide, p + row)));
            confidence[i] = Clamp((light - .81f) * 7.5f, 0, 1) * Clamp(1 - 16 * variation, 0, 1);
        }
        return confidence;
    }

    private static float Luma(float[] image, int p)
    {
        return .0722f * image[p] + .7152f * image[p + 1] + .2126f * image[p + 2];
    }

    private static void Compose(ref float white, ref float dark, float nextWhite, float nextDark)
    {
        // Residuals are found in front-to-back order. Each deeper layer is seen through
        // the transmission of all layers already above it.
        float transmission = Math.Max(0, 1 - white - dark);
        white += transmission * nextWhite;
        dark += transmission * nextDark;
    }

    private static float[] BuildGuide(byte[] rgba, bool[] support, int width, float scale)
    {
        // Extend the colors of intact pixels inward to obtain a conservative estimate
        // of the background. The recovered source gradients add the detail later.
        var guide = new float[support.Length * 3];
        var pending = (bool[])support.Clone();
        var wave = new Queue<int>();
        for (int i = 0; i < support.Length; i++)
        {
            if (support[i]) continue;
            for (int c = 0; c < 3; c++) guide[i * 3 + c] = rgba[i * 4 + c] / 255f;
        }
        for (int i = width + 1; i < support.Length - width - 1; i++)
        {
            if (support[i] && (!support[i - 1] || !support[i + 1] || !support[i - width] || !support[i + width]))
                ExtendGuide(i, width, guide, pending, wave);
        }
        while (wave.Count != 0)
        {
            int i = wave.Dequeue();
            ExtendGuide(i - 1, width, guide, pending, wave);
            ExtendGuide(i + 1, width, guide, pending, wave);
            ExtendGuide(i - width, width, guide, pending, wave);
            ExtendGuide(i + width, width, guide, pending, wave);
        }
        Integrate(guide, null, null, 0, support, rgba, width, Math.Min(120, Math.Max(25, (int)(75 * scale))));
        return guide;
    }

    private static void ExtendGuide(int i, int width, float[] guide, bool[] pending, Queue<int> wave)
    {
        if (!pending[i]) return;
        int left = pending[i - 1] ? 0 : 1, right = pending[i + 1] ? 0 : 1;
        int above = pending[i - width] ? 0 : 1, below = pending[i + width] ? 0 : 1;
        int index = i * 3, line = width * 3, total = left + right + above + below;
        for (int c = index; c < index + 3; c++)
            guide[c] = (guide[c - 3] * left + guide[c + 3] * right
                + guide[c - line] * above + guide[c + line] * below) / total;
        pending[i] = false;
        wave.Enqueue(i);
    }

    private static float[] FitLayerGains(byte[] rgba, float[] guide, float[][] mattes, List<Match> layers)
    {
        int count = mattes[0].Length;
        var gains = new float[mattes.Length];
        for (int k = 0; k < gains.Length; k++) gains[k] = 1;
        var observed = new float[count];
        var background = new float[count];
        for (int i = 0; i < count; i++)
        {
            observed[i] = (.0722f * rgba[i * 4] + .7152f * rgba[i * 4 + 1] + .2126f * rgba[i * 4 + 2]) / 255;
            background[i] = Luma(guide, i * 3);
        }
        int step = Math.Max(1, count / 35000);
        for (int pass = 0; pass < 7; pass++)
        for (int k = 0; k < gains.Length; k++)
        {
            // Each gain enters the compositing equation linearly with the other gains fixed.
            // Downweight large residuals from texture or an imperfect template boundary.
            float numerator = .20f * (1 - gains[k]), denominator = .20f;
            for (int i = 0; i < count; i += step)
            {
                if (mattes[k][i] < .025f || rgba[i * 4 + 3] < 230) continue;
                float value = background[i], derivative = 0;
                for (int layer = gains.Length - 2; layer >= 0; layer -= 2)
                {
                    float a = gains[layer] * mattes[layer][i], s = gains[layer + 1] * mattes[layer + 1][i];
                    float transmission = Math.Max(0, 1 - a - s);
                    if (k == layer) derivative = (1 - value) * mattes[k][i];
                    else if (k == layer + 1) derivative = -value * mattes[k][i];
                    else derivative *= transmission;
                    value = value * transmission + a;
                }
                float error = observed[i] - value;
                float weight = 1 / Math.Max(.039f, Math.Abs(error));
                numerator += weight * derivative * error;
                denominator += weight * derivative * derivative;
            }
            bool fill = k % 2 == 0;
            Match match = layers[k / 2];
            float minimum = fill ? .44f : 0;
            float maximum = !fill && match.GrayInk ? 2.1f : 1.30f;
            if (fill && match.FineOutline)
            {
                // The white fill has little contrast on light paper, so keep the fit near
                // the calibrated opacity when the local data cannot constrain it.
                minimum = .76f;
                maximum = 1.13f;
            }
            gains[k] = Clamp(gains[k] + numerator / denominator, minimum, maximum);
        }
        return gains;
    }

    private static void Integrate(float[] values, float[] rhs, float[] seed, float anchor,
        bool[] support, byte[] rgba, int width, int iterations)
    {
        int rowOffset = width * 3;
        float divisor = 4 + anchor;
        for (int pass = 0; pass < iterations; pass++)
        for (int i = width + 1; i < support.Length - width - 1; i++)
        {
            if (!support[i] || rgba[i * 4 + 3] == 0) continue;
            int offset = i * 3;
            for (int c = offset; c < offset + 3; c++)
            {
                float estimate = (values[c - 3] + values[c + 3] + values[c - rowOffset] + values[c + rowOffset]
                    + (rhs == null ? 0 : rhs[c] + anchor * seed[c])) / divisor;
                values[c] += 1.30f * (estimate - values[c]);
            }
        }
    }

    private static float Transition(int i, int j, float[] a, float[] s, float[] da, float[] ds, float scale, float[][] mattes)
    {
        float shape = Math.Abs(a[i] - a[j]) + Math.Abs(s[i] - s[j]);
        float detail = Math.Abs(da[i] - da[j]) + Math.Abs(ds[i] - ds[j]);
        float difference = shape + .4f * detail;
        if (mattes != null)
        {
            float combined = 0;
            foreach (float[] matte in mattes) combined += Math.Abs(matte[i] - matte[j]);
            difference = Math.Max(shape + .20f * detail, .56f * combined);
        }
        return Math.Min(1, difference * 5 * Math.Max(1, scale * .8f));
    }

    private static float[] Dilate(float[] field, int width, int height, int radius)
    {
        var rowPeaks = new float[field.Length];
        var band = new float[field.Length];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int row = y * width;
            float peak = 0;
            for (int u = Math.Max(0, x - radius); u <= Math.Min(width - 1, x + radius); u++)
                peak = Math.Max(peak, field[row + u]);
            rowPeaks[row + x] = peak;
        }
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = y * width + x;
            float peak = 0;
            for (int v = Math.Max(0, y - radius); v <= Math.Min(height - 1, y + radius); v++)
                peak = Math.Max(peak, rowPeaks[v * width + x]);
            band[i] = peak * .80f + field[i] * .20f;
        }
        return band;
    }

    private static void AddGradient(float[] seed, float[] rhs, float[] transition, float[] band,
        int width, int height, int x, int y, int radius, bool vertical, bool stacked, bool singleGray, bool singleOutline)
    {
        int i = y * width + x, step = vertical ? width : 1, j = i + step;
        int a = i * 3, b = j * 3;
        float blue = seed[b] - seed[a], green = seed[b + 1] - seed[a + 1], red = seed[b + 2] - seed[a + 2];
        float maximum = Math.Max(blue, Math.Max(green, red)), minimum = Math.Min(blue, Math.Min(green, red));
        float chroma = Clamp((maximum - minimum - .024f) * 8, 0, .65f);
        float edge = Clamp(Math.Max(maximum, -minimum) - .23f, 0, .65f) * .7f;
        float keep = stacked ? (1 - band[i]) * (1 - band[i]) : 1 - band[i] * (1 - 1.2f * Math.Max(chroma, edge));
        float intensity = (blue + green + red) / 3;
        if (singleGray) keep = Math.Max(keep, .65f * (1 - Clamp((Math.Abs(intensity) - .012f) * 18, 0, 1)));
        if (singleOutline)
            keep = 1 - band[i] * (1 - Clamp((maximum - minimum - .016f) * 12, 0, .83f));
        if (!stacked && band[i] > .05f && Math.Abs(intensity) > .025f &&
            (!singleOutline || chroma > .14f || Math.Abs(intensity) > .085f))
        {
            // A nearby edge with the same direction and polarity helps continue a real
            // structure such as a leaf vein through a letter boundary.
            float continuation = 0;
            for (int across = -4; across <= 4; across++)
            for (int along = -2; along <= 2; along++)
            {
                if (across == 0 || Math.Abs(along) > Math.Abs(across)) continue;
                int u = x + (vertical ? across : along) * radius;
                int v = y + (vertical ? along : across) * radius;
                if (u < 0 || u >= width - 1 || v < 0 || v >= height - 1) continue;
                int k = v * width + u;
                if (transition[k] >= .19f || band[k] >= .58f) continue;
                int start = k * 3, end = (k + step) * 3;
                float reference = (seed[end] + seed[end + 1] + seed[end + 2] - seed[start] - seed[start + 1] - seed[start + 2]) / 3;
                if (intensity * reference <= 0) continue;
                float amplitude = Math.Min(Math.Abs(reference), Math.Abs(intensity));
                float decay = (1 - .09f * Math.Abs(across)) * (1 - .15f * Math.Abs(along));
                continuation = Math.Max(continuation, Clamp((amplitude - .018f) * 13, 0, 1) * decay);
            }
            keep = Math.Max(keep, Math.Min(1, continuation * 1.15f));
        }
        for (int c = 0; c < 3; c++)
        {
            float gradient = (seed[b + c] - seed[a + c]) * keep;
            rhs[a + c] -= gradient;
            rhs[b + c] += gradient;
        }
    }

    private static float SampleBilinear(float[] values, int stride, float x, float y)
    {
        int u = (int)x, v = (int)y;
        float dx = x - u, dy = y - v;
        int i = v * stride + u;
        float top = values[i] + dx * (values[i + 1] - values[i]);
        float bottom = values[i + stride] + dx * (values[i + stride + 1] - values[i + stride]);
        return top + dy * (bottom - top);
    }

    private static float Clamp(float value, float low, float high)
    {
        return Math.Max(low, Math.Min(value, high));
    }
}
