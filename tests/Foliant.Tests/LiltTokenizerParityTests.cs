using Foliant.Forms.Lilt;
using Xunit;
using Xunit.Abstractions;

namespace Foliant.Tests;

// Tokenizer parity spike (#9 LiLT integration, Stage 1). Prints the C# token IDs for a set of sample
// strings so they can be compared against HuggingFace's IDs for the SAME strings (see
// docs/training/hf_tokenizer_ref.py). The model was trained with the HF RoBERTa tokenizer, so if these
// diverge we must fix the C# tokenizer (or fall back to hand-rolled byte-level BPE) BEFORE building any
// featurization — a silent mismatch would wreck accuracy with no error.
//
// Run:  dotnet test tests/Foliant.Tests --filter LiltTokenizerParity
// Then: python3 docs/training/hf_tokenizer_ref.py   (in your training .venv) and compare line-by-line.
public sealed class LiltTokenizerParityTests
{
    private readonly ITestOutputHelper _out;
    public LiltTokenizerParityTests(ITestOutputHelper output) => _out = output;

    // Byte-level BPE is sensitive to leading spaces (RoBERTa marks them with 'Ġ'), so we deliberately
    // include both bare and space-prefixed forms, plus punctuation/number cases the forms actually contain.
    private static readonly string[] Samples =
    {
        "Name",
        " Name",
        "Jane A. Smith",
        "1800 F Street NW",
        "(202) 555-0143",
        "[email protected]",
        "03/14/2026",
    };

    [Fact]
    public void LiltTokenizerParity_PrintIds()
    {
        string dir = FindModelDir();
        var tok = LiltTokenizer.Load(dir);

        _out.WriteLine($"model dir: {dir}");
        _out.WriteLine("=== C# Microsoft.ML.Tokenizers ids (no special tokens) ===");
        foreach (var s in Samples)
        {
            var ids = tok.EncodeToIds(s);
            _out.WriteLine($"{Quote(s),-22} -> [{string.Join(", ", ids)}]");
            Assert.NotEmpty(ids);   // a sane tokenizer never returns nothing for non-empty text
        }
    }

    private static string Quote(string s) => "\"" + s + "\"";

    // Walk up from the test bin dir to the repo root and locate the gitignored model folder.
    private static string FindModelDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            string candidate = Path.Combine(d.FullName, "models", "form-kv-lilt");
            if (Directory.Exists(candidate)) return candidate;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not find models/form-kv-lilt above the test output dir. " +
            "Place the exported model + tokenizer files there (model.onnx, vocab.json, merges.txt, …).");
    }
}
