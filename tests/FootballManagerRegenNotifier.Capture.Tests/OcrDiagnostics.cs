using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text;
using FootballManagerRegenNotifier.Capture.Ocr;
using FootballManagerRegenNotifier.Capture.Preprocess;
using FootballManagerRegenNotifier.Core.Settings;
using Tesseract;
using Xunit;
using Xunit.Abstractions;

namespace FootballManagerRegenNotifier.Capture.Tests;

/// <summary>
/// Not an assertion suite: a sweep that prints how each preprocessing and engine
/// combination performs, so the shipped defaults are chosen from measurements
/// rather than from folklore. Run with <c>-v n</c> to read the tables.
/// </summary>
public class OcrDiagnostics(ITestOutputHelper output)
{
    private static string TessDataPath => Path.Combine(AppContext.BaseDirectory, "tessdata");

    private static readonly string[] Samples =
    [
        "01/07/2026", "17/07/2026", "07/11/2026", "27/07/2027", "14/03/2026", "31/12/2027",
    ];

    internal static CapturedFrame Render(string text, int width, int height, float fontSize, string family)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.FromArgb(255, 28, 32, 38));
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var font = new Font(family, fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.FromArgb(255, 235, 238, 242));
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString(text, font, brush, new RectangleF(0, 0, width, height), format);
        }

        var frame = CapturedFrame.Allocate(bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + y * data.Stride, frame.Pixels, y * frame.Stride, bitmap.Width * 4);
            }
        }
        finally { bitmap.UnlockBits(data); }
        return frame;
    }

    private static string ReadWith(CapturedFrame frame, OcrSettings settings, PageSegMode psm,
        bool numericMode, out double mean)
    {
        var processed = Preprocessor.Run(frame, settings);
        byte[] bmp = BmpEncoder.Encode24Bpp(processed.Frame);

        var mode = settings.UseLegacyEngine ? EngineMode.TesseractOnly : EngineMode.LstmOnly;
        using var engine = new TesseractEngine(TessDataPath, "eng", mode);
        if (settings.UseCharacterWhitelist)
        {
            engine.SetVariable("tessedit_char_whitelist", settings.CharacterWhitelist);
        }
        if (numericMode) engine.SetVariable("classify_bln_numeric_mode", "1");

        using var pix = Pix.LoadFromMemory(bmp);
        using var page = engine.Process(pix, psm);
        mean = page.GetMeanConfidence();
        string raw = page.GetText() ?? string.Empty;
        return settings.UseCharacterWhitelist
            ? TesseractOcrEngine.FilterToWhitelist(raw, settings.CharacterWhitelist)
            : raw.Trim();
    }

    [SkippableFact]
    public void Sweep()
    {
        Skip.IfNot(File.Exists(Path.Combine(TessDataPath, "eng.traineddata")), "No language data.");

        var report = new StringBuilder();
        var scores = new List<(string Config, int Correct, double Conf)>();

        foreach (bool legacy in new[] { false, true })
        foreach (var psm in new[] { PageSegMode.SingleLine, PageSegMode.SingleWord, PageSegMode.SingleBlock, PageSegMode.RawLine })
        foreach (bool whitelist in new[] { true, false })
        foreach (bool numeric in new[] { false, true })
        {
            var settings = OcrSettings.Default with
            {
                UseLegacyEngine = legacy,
                UseCharacterWhitelist = whitelist,
            };

            int correct = 0;
            double confSum = 0;
            var details = new List<string>();

            foreach (string sample in Samples)
            {
                var frame = Render(sample, 145, 35, 15f, "Segoe UI");
                string read;
                double mean;
                try { read = ReadWith(frame, settings, psm, numeric, out mean); }
                catch (Exception ex) { read = "ERR:" + ex.GetType().Name; mean = 0; }

                if (read == sample) correct++; else details.Add($"{sample}->{read}");
                confSum += mean;
            }

            string config = $"{(legacy ? "legacy" : "lstm")} psm={psm} wl={whitelist} num={numeric}";
            scores.Add((config, correct, confSum / Samples.Length));
            report.AppendLine($"{correct}/{Samples.Length}  conf={confSum / Samples.Length:F2}  {config}");
            if (details.Count > 0) report.AppendLine("       " + string.Join("  ", details));
        }

        output.WriteLine("=== ENGINE / MODE SWEEP (145x35, Segoe UI 15px, 4x upscale) ===");
        foreach (var line in scores.OrderByDescending(s => s.Correct).ThenByDescending(s => s.Conf).Take(12))
        {
            output.WriteLine($"  {line.Correct}/{Samples.Length}  conf={line.Conf:F2}  {line.Config}");
        }
        output.WriteLine("");
        output.WriteLine(report.ToString());
    }

    [SkippableFact]
    public void UpscaleAndQuietZoneSweep()
    {
        Skip.IfNot(File.Exists(Path.Combine(TessDataPath, "eng.traineddata")), "No language data.");

        output.WriteLine("=== UPSCALE x QUIET ZONE (lstm, psm=SingleLine, whitelist on) ===");

        foreach (int upscale in new[] { 1, 2, 3, 4, 5, 6 })
        foreach (int quiet in new[] { 0, 8, 16, 32 })
        {
            var settings = OcrSettings.Default with { UpscaleFactor = upscale, QuietZonePixels = quiet };
            int correct = 0;
            foreach (string sample in Samples)
            {
                var frame = Render(sample, 145, 35, 15f, "Segoe UI");
                string read;
                try { read = ReadWith(frame, settings, PageSegMode.SingleLine, false, out _); }
                catch { read = "ERR"; }
                if (read == sample) correct++;
            }
            output.WriteLine($"  upscale={upscale} quiet={quiet,2} -> {correct}/{Samples.Length}");
        }
    }
}
