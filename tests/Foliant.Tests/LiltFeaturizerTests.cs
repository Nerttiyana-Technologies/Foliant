using Foliant;
using Foliant.Forms.Lilt;
using Xunit;

namespace Foliant.Tests;

// Featurization tests (#9 LiLT integration, Stage 2). Pure C# — no model/Python needed beyond the
// tokenizer files. Verifies the words+boxes -> input_ids/bbox/attention_mask assembly matches the
// training rig (norm_box 0..1000) and the model's tokenizer_config.json (cls/sep/pad boxes [0,0,0,0],
// <s>=0, </s>=2, <pad>=1, max_length 512, right truncation/padding).
public sealed class LiltFeaturizerTests
{
    private static LiltTokenizer Tok() => LiltTokenizer.Load(FindModelDir()!);
    private static LiltFeaturizer Feat() => new(Tok());

    [Fact]
    public void NormBox_MatchesTrainingRig_AndClamps()
    {
        // round(v*1000/dim), clamped to 0..1000.
        Assert.Equal(new long[] { 50, 50, 150, 65 },
            LiltFeaturizer.NormBox(new BoundingBox(50, 100, 150, 130), width: 1000, height: 2000));

        // Out-of-range pixels clamp into [0,1000].
        Assert.Equal(new long[] { 0, 0, 1000, 1000 },
            LiltFeaturizer.NormBox(new BoundingBox(-10, 0, 2000, 100), width: 1000, height: 100));
    }

    [Fact]
    public void Encode_AssemblesSpecialsBodyAndPadding()
    {
        if (FindModelDir() is null) return;   // LiLT model is local-only (models/ gitignored) — skip in CI
        var tok = Tok();
        var feat = new LiltFeaturizer(tok);

        var words = new[] { "Name", "Smith" };
        var boxes = new[]
        {
            new BoundingBox(0, 0, 100, 50),       // -> norm [0,0,500,500] on a 200x100 page
            new BoundingBox(100, 50, 200, 100),   // -> norm [500,500,1000,1000]
        };
        var f = feat.Encode(words, boxes, pageWidth: 200, pageHeight: 100);

        // Expected body = subwords of each word, derived from the (parity-proven) tokenizer.
        var nameSub = tok.EncodeWord("Name");
        var smithSub = tok.EncodeWord("Smith");
        int bodyLen = nameSub.Count + smithSub.Count;
        int length = bodyLen + 2;   // <s> + body + </s>

        Assert.Equal(length, f.Length);
        Assert.Equal(LiltFeatures.MaxLen, f.InputIds.Length);

        // <s> … </s> then <pad>.
        Assert.Equal(0, f.InputIds[0]);                          // <s>
        Assert.Equal(2, f.InputIds[length - 1]);                 // </s>
        Assert.Equal(1, f.InputIds[length]);                     // first <pad>
        var expectedBody = nameSub.Concat(smithSub).Select(i => (long)i).ToArray();
        Assert.Equal(expectedBody, f.InputIds.Skip(1).Take(bodyLen).ToArray());

        // Attention: 1 for real tokens, 0 for pad.
        Assert.Equal(length, f.AttentionMask.Count(a => a == 1));
        Assert.All(f.AttentionMask.Skip(length), a => Assert.Equal(0, a));

        // Special-token boxes are zero; body boxes are the owning word's normalized box.
        AssertBox(f, 0, 0, 0, 0, 0);                             // <s>
        AssertBox(f, length - 1, 0, 0, 0, 0);                    // </s>
        for (int i = 0; i < nameSub.Count; i++) AssertBox(f, 1 + i, 0, 0, 500, 500);
        for (int i = 0; i < smithSub.Count; i++) AssertBox(f, 1 + nameSub.Count + i, 500, 500, 1000, 1000);

        // Token -> word map: specials/pad = -1; body points at the owning word.
        Assert.Equal(-1, f.TokenToWord[0]);
        Assert.Equal(-1, f.TokenToWord[length - 1]);
        for (int i = 0; i < nameSub.Count; i++) Assert.Equal(0, f.TokenToWord[1 + i]);
        for (int i = 0; i < smithSub.Count; i++) Assert.Equal(1, f.TokenToWord[1 + nameSub.Count + i]);
    }

    [Fact]
    public void Encode_TruncatesToMaxLen_AndStillClosesWithSep()
    {
        if (FindModelDir() is null) return;   // LiLT model is local-only (models/ gitignored) — skip in CI
        var feat = Feat();
        var words = Enumerable.Repeat("a", 600).ToArray();             // far more than 510 sub-words
        var boxes = Enumerable.Repeat(new BoundingBox(0, 0, 10, 10), 600).ToArray();

        var f = feat.Encode(words, boxes, pageWidth: 1000, pageHeight: 1000);

        Assert.Equal(LiltFeatures.MaxLen, f.Length);                   // filled to the cap
        Assert.Equal(0, f.InputIds[0]);                               // still opens with <s>
        Assert.Equal(2, f.InputIds[LiltFeatures.MaxLen - 1]);         // still closes with </s>
        Assert.All(f.AttentionMask, a => Assert.Equal(1, a));         // no padding at the cap
        Assert.Equal(-1, f.TokenToWord[LiltFeatures.MaxLen - 1]);     // </s> is not a word
    }

    private static void AssertBox(LiltFeatures f, int token, long x1, long y1, long x2, long y2)
    {
        Assert.Equal(x1, f.Bbox[token * 4 + 0]);
        Assert.Equal(y1, f.Bbox[token * 4 + 1]);
        Assert.Equal(x2, f.Bbox[token * 4 + 2]);
        Assert.Equal(y2, f.Bbox[token * 4 + 3]);
    }

    // Returns null when the local-only model is absent (models/ is gitignored). LiLT is experimental and
    // excluded from the NuGet package, so model-using tests early-return rather than fail — e.g. in CI.
    private static string? FindModelDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            string candidate = Path.Combine(d.FullName, "models", "form-kv-lilt");
            if (Directory.Exists(candidate)) return candidate;
            d = d.Parent;
        }
        return null;
    }
}
