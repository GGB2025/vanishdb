using System;

internal static class OverlapTexture
{
    public static void Restore(float[] image, byte[] original, bool[] support, int width, int height, float scale)
    {
        int radius = Math.Max(4, (int)Math.Round(8 * scale));
        if (width <= 2 * radius || height <= 2 * radius) return;
        int stride = width + 1;
        var occupied = new int[stride * (height + 1)];
        for (int y = 0; y < height; y++)
        {
            int rowSum = 0;
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                if (support[i] || original[i * 4 + 3] < 230) rowSum++;
                int j = (y + 1) * stride + x + 1;
                occupied[j] = occupied[j - stride] + rowSum;
            }
        }

        int targetStep = Math.Max(2, (int)Math.Round(4 * scale));
        int donorStep = Math.Max(2, (int)Math.Round(4 * scale));
        int reachX = Math.Max(radius * 3, (int)Math.Round(50 * scale));
        int reachY = Math.Max(radius * 3, (int)Math.Round(34 * scale));
        var votes = new float[image.Length];
        var weights = new float[support.Length];
        var donors = new Donor[support.Length];
        var samples = new PatchSample[25];
        var shift = new float[3];
        var window = new float[2 * radius + 1];
        for (int i = -radius; i <= radius; i++) window[i + radius] = (radius + 1 - Math.Abs(i)) / (radius + 1f);

        for (int y = radius; y < height - radius; y += targetStep)
        for (int x = radius; x < width - radius; x += targetStep)
        {
            int small = Math.Max(1, radius / 2);
            if (Count(occupied, stride, x, y, small) == 0) continue;
            int sample = 0;
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int offset = dy * small * width + dx * small;
                int i = y * width + x + offset;
                float weight = support[i] ? .20f : 1;
                samples[sample++] = new PatchSample { Offset = offset, Weight = weight,
                    Blue = image[i * 3], Green = image[i * 3 + 1], Red = image[i * 3 + 2] };
            }
            int left = Math.Max(radius, x - reachX), right = Math.Min(width - radius - 1, x + reachX);
            int top = Math.Max(radius, y - reachY), bottom = Math.Min(height - radius - 1, y + reachY);
            int bestX = -1, bestY = -1;
            float best = float.PositiveInfinity;
            int target = y * width + x;
            if (x >= radius + targetStep)
            {
                Donor previous = donors[target - targetStep];
                if (previous.Valid)
                    TryPatch(original, occupied, stride, radius, samples, x, y, previous.X + targetStep, previous.Y,
                        reachX, reachY, .0014f, ref bestX, ref bestY, ref best);
            }
            if (y >= radius + targetStep)
            {
                Donor previous = donors[target - targetStep * width];
                if (previous.Valid)
                    TryPatch(original, occupied, stride, radius, samples, x, y, previous.X, previous.Y + targetStep,
                        reachX, reachY, .0014f, ref bestX, ref bestY, ref best);
            }
            for (int v = top; v <= bottom; v += donorStep)
            for (int u = left; u <= right; u += donorStep)
                TryPatch(original, occupied, stride, radius, samples, x, y, u, v, reachX, reachY, 0, ref bestX, ref bestY, ref best);
            if (bestX < 0) continue;
            int centerX = bestX, centerY = bestY, refine = Math.Min(4, donorStep - 1);
            for (int v = Math.Max(top, centerY - refine); v <= Math.Min(bottom, centerY + refine); v++)
            for (int u = Math.Max(left, centerX - refine); u <= Math.Min(right, centerX + refine); u++)
                TryPatch(original, occupied, stride, radius, samples, x, y, u, v, reachX, reachY, 0, ref bestX, ref bestY, ref best);
            donors[target] = new Donor { X = bestX, Y = bestY, Valid = true };

            int donor = bestY * width + bestX;
            float total = 0;
            shift[0] = shift[1] = shift[2] = 0;
            foreach (PatchSample s in samples)
            {
                int i = (donor + s.Offset) * 4;
                shift[0] += s.Weight * (s.Blue - original[i] / 255f);
                shift[1] += s.Weight * (s.Green - original[i + 1] / 255f);
                shift[2] += s.Weight * (s.Red - original[i + 2] / 255f);
                total += s.Weight;
            }
            for (int c = 0; c < 3; c++) shift[c] = Math.Max(-.14f, Math.Min(.14f, shift[c] / total));

            // Overlapping votes avoid seams between donor patches.
            float quality = 1 / (.0035f + Math.Max(0, best));
            for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int i = (y + dy) * width + x + dx;
                if (!support[i]) continue;
                int p = (donor + dy * width + dx) * 4;
                float weight = quality * window[dy + radius] * window[dx + radius];
                for (int c = 0; c < 3; c++)
                    votes[i * 3 + c] += weight * (original[p + c] / 255f + shift[c]);
                weights[i] += weight;
            }
        }

        int fade = Math.Max(1, (int)Math.Round(2 * scale));
        for (int y = radius; y < height - radius; y++)
        for (int x = radius; x < width - radius; x++)
        {
            int i = y * width + x;
            if (weights[i] <= 0) continue;
            int side = 2 * fade + 1;
            float coverage = Count(occupied, stride, x, y, fade) / (float)(side * side);
            float blend = Math.Min(.84f, coverage * .92f);
            for (int c = 0; c < 3; c++)
            {
                int channel = i * 3 + c;
                image[channel] += blend * (votes[channel] / weights[i] - image[channel]);
            }
        }
    }

    private struct PatchSample
    {
        public int Offset;
        public float Weight, Blue, Green, Red;
    }

    private struct Donor
    {
        public int X, Y;
        public bool Valid;
    }

    private static void TryPatch(byte[] original, int[] occupied, int stride, int radius, PatchSample[] samples,
        int x, int y, int u, int v, int reachX, int reachY, float continuity, ref int bestX, ref int bestY, ref float best)
    {
        if (u < radius || u >= stride - 1 - radius || v < radius ||
            v >= occupied.Length / stride - 1 - radius) return;
        if (Count(occupied, stride, u, v, radius) != 0) return;
        int donor = v * (stride - 1) + u;
        float error = 0, weight = 0;
        foreach (PatchSample s in samples)
        {
            int i = (donor + s.Offset) * 4;
            float b = s.Blue - original[i] / 255f;
            float g = s.Green - original[i + 1] / 255f;
            float r = s.Red - original[i + 2] / 255f;
            error += s.Weight * (.20f * b * b + .52f * g * g + .28f * r * r);
            weight += s.Weight;
        }
        float dx = (u - x) / (float)reachX, dy = (v - y) / (float)reachY;
        float score = error / weight + .0008f * dx * dx + .0022f * dy * dy - continuity;
        if (score >= best) return;
        best = score;
        bestX = u;
        bestY = v;
    }

    private static int Count(int[] sums, int stride, int x, int y, int radius)
    {
        int left = x - radius, right = x + radius + 1;
        int top = (y - radius) * stride, bottom = (y + radius + 1) * stride;
        return sums[bottom + right] - sums[bottom + left] - sums[top + right] + sums[top + left];
    }
}
