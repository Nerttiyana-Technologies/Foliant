using Foliant;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Foliant.Forms.Lilt;

/// <summary>
/// LiLT form key-value model: loads <c>model.onnx</c> + the tokenizer from a model directory and predicts
/// which of a page's words are filled field VALUES (the v1 token-classification head is O / VALUE only —
/// it flags values, not field names; pairing values with labels happens upstream). Inference is fully
/// local via ONNX Runtime. Not thread-safe for concurrent <see cref="PredictValueWords"/> calls on one
/// instance; create one per worker or serialize calls.
/// </summary>
public sealed class LiltFormKvModel : IDisposable
{
    private const int ValueLabel = 1;   // labels: 0 = O, 1 = VALUE

    private readonly InferenceSession _session;
    private readonly LiltFeaturizer _featurizer;
    private readonly string? _inputIdsName, _attentionName, _bboxName, _tokenTypeName;
    private readonly string _logitsName;

    public LiltFormKvModel(string modelDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDir);
        string onnxPath = Path.Combine(modelDir, "model.onnx");
        if (!File.Exists(onnxPath))
            throw new FileNotFoundException($"model.onnx not found under '{modelDir}'.", onnxPath);

        _session = new InferenceSession(onnxPath);
        _featurizer = new LiltFeaturizer(LiltTokenizer.Load(modelDir));

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

    /// <summary>
    /// Returns the indices (into <paramref name="words"/>) of the words the model classifies as VALUE.
    /// A word is VALUE when its FIRST sub-word is predicted VALUE — matching the training label scheme
    /// (<c>only_label_first_subword=true</c>). Empty when no words or none predicted.
    /// </summary>
    public IReadOnlyList<int> PredictValueWords(
        IReadOnlyList<string> words, IReadOnlyList<BoundingBox> boxes, int pageWidth, int pageHeight)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count == 0) return Array.Empty<int>();

        var f = _featurizer.Encode(words, boxes, pageWidth, pageHeight);
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
        int numLabels = logits.Dimensions[^1];

        var valueWords = new List<int>();
        var wordSeen = new HashSet<int>();
        for (int t = 0; t < f.Length; t++)
        {
            int wi = f.TokenToWord[t];
            if (wi < 0) continue;                 // <s> / </s>
            if (!wordSeen.Add(wi)) continue;      // only the first sub-word of each word is decisive

            int best = 0;
            float bestScore = float.NegativeInfinity;
            for (int c = 0; c < numLabels; c++)
            {
                float score = logits[0, t, c];
                if (score > bestScore) { bestScore = score; best = c; }
            }
            if (best == ValueLabel) valueWords.Add(wi);
        }
        return valueWords;
    }

    private static string? FindInput(List<string> inputs, string name) =>
        inputs.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? inputs.FirstOrDefault(k => k.EndsWith(name, StringComparison.OrdinalIgnoreCase));

    public void Dispose() => _session.Dispose();
}
