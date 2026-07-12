using System.Text.Json;
using Foliant;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Foliant.Forms.Lilt;

/// <summary>The entity class of a predicted span.</summary>
public enum LiltSpanKind
{
    /// <summary>A printed field label ("NAME AND TITLE", "SSN", …).</summary>
    Key,

    /// <summary>A filled field value.</summary>
    Value,
}

/// <summary>
/// One predicted entity span: a run of consecutive words classified KEY or VALUE, with the model's
/// mean softmax confidence over the span's decisive (first-sub-word) tokens.
/// </summary>
public sealed record LiltSpan(LiltSpanKind Kind, IReadOnlyList<int> WordIndices, float Confidence);

/// <summary>
/// LiLT form key-value model: loads <c>model.onnx</c> + the tokenizer from a model directory and predicts
/// KEY/VALUE entity spans over a page's words. The label scheme is read from the exported
/// <c>config.json</c> (<c>id2label</c>): supports the FUNSD-style BIO scheme (O / B-KEY / I-KEY /
/// B-VALUE / I-VALUE), the BIO-VALUE scheme, and the legacy v1 O/VALUE head. Inference is fully local
/// via ONNX Runtime. Not thread-safe for concurrent calls on one instance; create one per worker or
/// serialize calls.
/// </summary>
public sealed class LiltFormKvModel : IDisposable
{
    private readonly InferenceSession _session;
    private readonly LiltFeaturizer _featurizer;
    private readonly string? _inputIdsName, _attentionName, _bboxName, _tokenTypeName;
    private readonly string _logitsName;
    private readonly string[] _labels;

    public LiltFormKvModel(string modelDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDir);
        string onnxPath = Path.Combine(modelDir, "model.onnx");
        if (!File.Exists(onnxPath))
            throw new FileNotFoundException($"model.onnx not found under '{modelDir}'.", onnxPath);

        _session = new InferenceSession(onnxPath);
        _featurizer = new LiltFeaturizer(LiltTokenizer.Load(modelDir));
        _labels = LoadLabels(modelDir);

        var inputs = _session.InputMetadata.Keys.ToList();
        _inputIdsName = FindInput(inputs, "input_ids");
        _attentionName = FindInput(inputs, "attention_mask");
        _bboxName = FindInput(inputs, "bbox");
        _tokenTypeName = FindInput(inputs, "token_type_ids");   // present in some LiLT exports; fed as zeros
        if (_inputIdsName is null || _bboxName is null)
            throw new InvalidOperationException(
                $"model.onnx is missing input_ids/bbox inputs. Declared inputs: {string.Join(", ", inputs)}");

        _logitsName = _session.OutputMetadata.Keys.FirstOrDefault(
            k => k.Equals("logits", StringComparison.OrdinalIgnoreCase))
            ?? _session.OutputMetadata.Keys.First();
    }

    /// <summary>The label names, index-aligned with the model's output classes.</summary>
    public IReadOnlyList<string> Labels => _labels;

    /// <summary>
    /// Predicts KEY/VALUE entity spans over the page's words. Word class = the prediction on its FIRST
    /// sub-word (matching training, <c>only_label_first_subword=true</c>); BIO runs become spans; a span's
    /// confidence is the mean softmax probability of its words' chosen labels. Pages larger than the
    /// 512-token model window are processed in OVERLAPPING WINDOWS (measured on the TD-41 holdout:
    /// 67% of pages overflow a single window — everything past it was invisible); each word keeps the
    /// prediction from the window where it sits most interior. Empty when nothing is predicted.
    /// </summary>
    public IReadOnlyList<LiltSpan> Predict(
        IReadOnlyList<string> words, IReadOnlyList<BoundingBox> boxes, int pageWidth, int pageHeight)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count == 0) return Array.Empty<LiltSpan>();

        // absolute word index → (label, prob, interiority of the deciding token in its window)
        var merged = new Dictionary<int, (int Label, float Prob, int Interior)>();

        int start = 0;
        while (start < words.Count)
        {
            var f = _featurizer.Encode(words, boxes, pageWidth, pageHeight, start);
            RunWindow(f, merged);

            int lastWord = -1;
            for (int t = f.Length - 1; t >= 0; t--)
                if (f.TokenToWord[t] >= 0) { lastWord = f.TokenToWord[t]; break; }
            if (lastWord < 0 || lastWord >= words.Count - 1) break;      // page fully covered

            int windowWords = lastWord - start + 1;
            start = Math.Max(start + 1, lastWord + 1 - windowWords / 4); // ~25% overlap
        }

        var wordLabel = merged.ToDictionary(kv => kv.Key, kv => (kv.Value.Label, kv.Value.Prob));

        // BIO decode over word order
        var spans = new List<LiltSpan>();
        List<int>? cur = null;
        LiltSpanKind curKind = default;
        double curProb = 0;

        void Flush()
        {
            if (cur is { Count: > 0 })
                spans.Add(new LiltSpan(curKind, cur, (float)(curProb / cur.Count)));
            cur = null; curProb = 0;
        }

        for (int wi = 0; wi < words.Count; wi++)
        {
            if (!wordLabel.TryGetValue(wi, out var wl)) { Flush(); continue; }
            string name = _labels[wl.Label];
            (string tag, string cls) = name.Length > 2 && name[1] == '-'
                ? (name[..1], name[2..])
                : (name == "O" ? "O" : "B", name);          // v1 "VALUE": adjacency merges below
            if (tag == "O") { Flush(); continue; }

            var kind = cls.Equals("KEY", StringComparison.OrdinalIgnoreCase) ? LiltSpanKind.Key : LiltSpanKind.Value;
            bool continues = cur is not null && kind == curKind
                             && (tag == "I" || (name == "VALUE" && cur[^1] == wi - 1));   // v1 runs merge
            if (!continues) Flush();
            if (cur is null) { cur = new List<int>(); curKind = kind; }
            cur.Add(wi); curProb += wl.Prob;
        }
        Flush();
        return spans;
    }

    /// <summary>
    /// Returns the indices (into <paramref name="words"/>) of the words the model classifies as VALUE.
    /// A word is VALUE when its FIRST sub-word is predicted VALUE — matching the training label scheme
    /// (<c>only_label_first_subword=true</c>). Empty when no words or none predicted. Legacy v1 read,
    /// kept for the born-digital quick eval; prefer <see cref="Predict"/>.
    /// </summary>
    public IReadOnlyList<int> PredictValueWords(
        IReadOnlyList<string> words, IReadOnlyList<BoundingBox> boxes, int pageWidth, int pageHeight) =>
        Predict(words, boxes, pageWidth, pageHeight)
            .Where(s => s.Kind == LiltSpanKind.Value)
            .SelectMany(s => s.WordIndices)
            .ToList();

    /// <summary>
    /// Runs one encoded window and merges per-word predictions (first sub-word decisive) into
    /// <paramref name="merged"/>. On overlap, the window where the deciding token sits farther from
    /// the window edges wins (edge tokens lack context); ties keep the higher-probability read.
    /// </summary>
    private void RunWindow(LiltFeatures f, Dictionary<int, (int Label, float Prob, int Interior)> merged)
    {
        const int n = LiltFeatures.MaxLen;
        var feeds = new List<NamedOnnxValue>(4)
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName!, new DenseTensor<long>(f.InputIds, new[] { 1, n })),
            NamedOnnxValue.CreateFromTensor(_bboxName!, new DenseTensor<long>(f.Bbox, new[] { 1, n, 4 })),
        };
        if (_attentionName is not null)
            feeds.Add(NamedOnnxValue.CreateFromTensor(_attentionName, new DenseTensor<long>(f.AttentionMask, new[] { 1, n })));
        if (_tokenTypeName is not null)
            feeds.Add(NamedOnnxValue.CreateFromTensor(_tokenTypeName, new DenseTensor<long>(new long[n], new[] { 1, n })));

        using var results = _session.Run(feeds);
        var logits = results.First(r => r.Name == _logitsName).AsTensor<float>();   // [1, seq, numLabels]
        int numLabels = Math.Min(logits.Dimensions[^1], _labels.Length);

        var seen = new HashSet<int>();
        for (int t = 0; t < f.Length; t++)
        {
            int wi = f.TokenToWord[t];
            if (wi < 0 || !seen.Add(wi)) continue;   // first sub-word of each word is decisive

            int best = 0;
            float bestScore = float.NegativeInfinity, max = float.NegativeInfinity;
            for (int c = 0; c < numLabels; c++)
            {
                float s = logits[0, t, c];
                if (s > max) max = s;
                if (s > bestScore) { bestScore = s; best = c; }
            }
            double denom = 0;
            for (int c = 0; c < numLabels; c++) denom += Math.Exp(logits[0, t, c] - max);
            float prob = (float)(Math.Exp(bestScore - max) / denom);
            int interior = Math.Min(t, f.Length - 1 - t);

            if (!merged.TryGetValue(wi, out var prev)
                || interior > prev.Interior
                || (interior == prev.Interior && prob > prev.Prob))
                merged[wi] = (best, prob, interior);
        }
    }

    private static string[] LoadLabels(string modelDir)
    {
        string configPath = Path.Combine(modelDir, "config.json");
        if (File.Exists(configPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.TryGetProperty("id2label", out var map))
            {
                var pairs = map.EnumerateObject()
                    .Select(p => (Id: int.Parse(p.Name), Label: p.Value.GetString() ?? "O"))
                    .OrderBy(p => p.Id)
                    .ToList();
                if (pairs.Count > 0 && !pairs.All(p => p.Label.StartsWith("LABEL_", StringComparison.Ordinal)))
                    return pairs.Select(p => p.Label).ToArray();
            }
        }
        return new[] { "O", "VALUE" };   // legacy v1 head (June smoke-test export, no id2label)
    }

    private static string? FindInput(List<string> inputs, string name) =>
        inputs.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? inputs.FirstOrDefault(k => k.EndsWith(name, StringComparison.OrdinalIgnoreCase));

    public void Dispose() => _session.Dispose();
}
