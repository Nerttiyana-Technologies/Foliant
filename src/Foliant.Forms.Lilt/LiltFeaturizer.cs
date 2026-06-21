using Foliant;

namespace Foliant.Forms.Lilt;

/// <summary>The model inputs for one page, ready to feed to the LiLT ONNX session.</summary>
public sealed class LiltFeatures
{
    /// <summary>Fixed model sequence length (model_max_length in tokenizer_config.json).</summary>
    public const int MaxLen = 512;

    /// <summary>Token ids, length <see cref="MaxLen"/> (<c>&lt;s&gt;</c> … <c>&lt;/s&gt;</c> then <c>&lt;pad&gt;</c>).</summary>
    public required long[] InputIds { get; init; }

    /// <summary>1 for real tokens (incl. specials), 0 for padding; length <see cref="MaxLen"/>.</summary>
    public required long[] AttentionMask { get; init; }

    /// <summary>Per-token boxes, row-major <c>[MaxLen * 4]</c>, each normalized to 0..1000; specials/pad = 0.</summary>
    public required long[] Bbox { get; init; }

    /// <summary>Source word index for each token position, or -1 for <c>&lt;s&gt;</c>/<c>&lt;/s&gt;</c>/<c>&lt;pad&gt;</c>.
    /// Lets the caller map per-token VALUE predictions back to whole words.</summary>
    public required int[] TokenToWord { get; init; }

    /// <summary>Number of real (non-pad) tokens, including the two special tokens.</summary>
    public required int Length { get; init; }
}

/// <summary>
/// Turns a page's words + pixel boxes into LiLT model inputs, mirroring the training featurization
/// (<c>docs/training/train_lilt_formkv.py</c> <c>norm_box</c> + the LayoutLMv3Tokenizer call). Each word is
/// tokenized with <see cref="LiltTokenizer.EncodeWord"/> and every sub-word carries that word's box
/// (normalized to 0..1000). Special-token boxes are all [0,0,0,0] per the model's tokenizer_config.json.
/// </summary>
public sealed class LiltFeaturizer
{
    private const long ClsId = 0, SepId = 2, PadId = 1;   // <s>, </s>, <pad> (RoBERTa/LiLT)
    private static readonly long[] ZeroBox = { 0, 0, 0, 0 };

    private readonly LiltTokenizer _tok;

    public LiltFeaturizer(LiltTokenizer tokenizer) => _tok = tokenizer;

    /// <summary>
    /// Encodes one page. <paramref name="words"/> and <paramref name="boxes"/> are parallel (one box per
    /// word, in page raster pixels); <paramref name="pageWidth"/>/<paramref name="pageHeight"/> are the
    /// raster size used to normalize boxes. Empty words should be filtered by the caller (the training rig
    /// drops empty tokens before featurizing).
    /// </summary>
    public LiltFeatures Encode(
        IReadOnlyList<string> words, IReadOnlyList<BoundingBox> boxes, int pageWidth, int pageHeight)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(boxes);
        if (words.Count != boxes.Count)
            throw new ArgumentException($"words ({words.Count}) and boxes ({boxes.Count}) must be parallel.");

        int budget = LiltFeatures.MaxLen - 2;   // reserve <s> + </s>

        var ids = new List<long>(LiltFeatures.MaxLen) { ClsId };
        var boxRows = new List<long[]>(LiltFeatures.MaxLen) { ZeroBox };
        var tokenToWord = new List<int>(LiltFeatures.MaxLen) { -1 };

        bool truncated = false;
        for (int wi = 0; wi < words.Count && !truncated; wi++)
        {
            long[] norm = NormBox(boxes[wi], pageWidth, pageHeight);
            foreach (int sub in _tok.EncodeWord(words[wi]))
            {
                if (ids.Count - 1 >= budget) { truncated = true; break; }   // -1 for the <s> already added
                ids.Add(sub);
                boxRows.Add(norm);
                tokenToWord.Add(wi);
            }
        }

        ids.Add(SepId);
        boxRows.Add(ZeroBox);
        tokenToWord.Add(-1);

        int length = ids.Count;

        var inputIds = new long[LiltFeatures.MaxLen];
        var attention = new long[LiltFeatures.MaxLen];
        var bbox = new long[LiltFeatures.MaxLen * 4];
        var t2w = new int[LiltFeatures.MaxLen];

        for (int i = 0; i < LiltFeatures.MaxLen; i++)
        {
            bool real = i < length;
            inputIds[i] = real ? ids[i] : PadId;
            attention[i] = real ? 1 : 0;
            t2w[i] = real ? tokenToWord[i] : -1;
            long[] box = real ? boxRows[i] : ZeroBox;
            for (int j = 0; j < 4; j++) bbox[i * 4 + j] = box[j];
        }

        return new LiltFeatures
        {
            InputIds = inputIds,
            AttentionMask = attention,
            Bbox = bbox,
            TokenToWord = t2w,
            Length = length,
        };
    }

    /// <summary>
    /// Normalizes a pixel box to LiLT's 0..1000 grid — identical to <c>norm_box</c> in the training rig:
    /// <c>clamp(round(v * 1000 / max(1, dim)), 0, 1000)</c>. Round-half-to-even matches Python's round().
    /// </summary>
    public static long[] NormBox(BoundingBox b, int width, int height)
    {
        static long F(float v, int dim) =>
            (long)Math.Clamp(Math.Round(v * 1000.0 / Math.Max(1, dim), MidpointRounding.ToEven), 0, 1000);
        return new[] { F(b.X1, width), F(b.Y1, height), F(b.X2, width), F(b.Y2, height) };
    }
}
